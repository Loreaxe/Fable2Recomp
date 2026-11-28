// refii - Fable 2's host bridge feeds
//
// A C ABI over the guest runtime, so Godot can host the recompiled guest IN ITS OWN
// PROCESS rather than spawning `refii` as a child.
//
// The exports every title shares — the ABI version, the guest's lifecycle, the debug
// channel, audio, input, time, mods, player state, achievements, RexNet — are
// refii_hostcore's (src/hostcore), linked whole into this library. What is here is Fable 2's:
// the feeds that read Fable 2's guest, the no-GPU stand-in its D3D needs, and its part of the
// core's lifecycle (TitleCallbacks, registered at the end of this file).

#include <algorithm>
#include "core/guest_build.h"
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <csignal>
#ifndef _WIN32
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <sys/syscall.h>
#include <sys/resource.h>
#include <setjmp.h>
#include <ucontext.h>
#include <unistd.h>
#include <ctime>
#endif
#include <cstring>
#include <span>
#include <exception>
#include <filesystem>
#include <fstream>
#include <set>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <dbghelp.h>
#endif

#include <rex/chrono/clock.h>
#include <rex/cvar.h>
#include <rex/logging.h>

#include <rex/system/kernel_state.h>

#include "appearance_sublist.h"
#include "core/host_asset_reader.h"
#include "core/precise_sleep.h"
#include "core/payload_bridge_fable2.h"
#include "core/property_condition.h"
#include "core/rex_payload_bridge.h"
#include <rex/memory/utils.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

#include <rex/runtime.h>
#include <rex/system/xthread.h>

#include <array>
#include <set>
#include <utility>
#ifdef REXGLUE_ENABLE_PROFILING
#include <tracy/Tracy.hpp>
#include <tracy/TracyC.h>
#endif

#include "guest_span.h"
#include "host_abi.h"
#include "host_core.h"
#include "core/traced_mutex.h"

#include "core/hero_derived.h"

namespace refii::vfs {
// Defined in Fable2/src/vfs/src/core/bink_frame_state.cpp.
uint32_t BinkFrameBuffersGuestAddress();

uint64_t NewGameTransitionResult(uint32_t* generation);
uint32_t GuestTickPumpHeroStateApplied(uint32_t player);
uint32_t DebugCallResult(uint64_t* out_seq, uint32_t* out_addr, uint64_t* out_r3,
                         char* out_note, uint32_t note_capacity);
int32_t GuestSavedGameCount();
void RefreshGuestSavedGameCount();
void FinishStuckInGameLoadingScreenFromHost(uint8_t* base);
int32_t FrontEndSubtitles();

/// Which pause tabs the guest's own predicate said it would offer, sampled on the GUI
/// tick. Bit N = tab N available, N in 2..8; 0 means not sampled yet.
uint32_t PauseTabsMask();

/// Local co-op split screen in effect, as the pump last stated it (guest_tick_pump_state.cpp).
int32_t CoopSplitInEffect();

/// The pause minimap's quest markers the guest stated this open (gui_pause_hooks.cpp).
uint32_t PauseMarkers(uint32_t* out_tokens, float* out_world, uint32_t capacity);

// Defined in Fable2/src/vfs/src/core/stated_fx_state.cpp and
// Fable2/src/vfs/src/core/target_highlight_state.cpp —
// what the guest's own setters stated, recorded by the hooks in the payload. Ordinary C++
// symbols because both ends of THIS call are our build; the payload's end crosses through the
// C entries in core/payload_bridge.h. These were dlsym'd out of the payload until 2026-09-18,
// which cannot compile on Windows and was never right on Linux either.
uint32_t StatedFxCount();
uint32_t StatedFxTransforms(uint32_t* out_words, uint32_t word_capacity);
uint32_t StatedHighlightFor(uint32_t component);
// Fable2/src/vfs/src/core/entity_appearance_state.cpp — `out` takes kAppearanceWords words.
bool StatedAppearanceFor(uint32_t component, uint32_t* out);
// Fable2/src/vfs/src/core/creature_corpse_state.cpp.
bool CreatureLeftCorpse(uint32_t creature);
// Fable2/src/vfs/src/core/part_morph_state.cpp.
uint32_t StatedPartMorphs(uint32_t entity, uint32_t* out, uint32_t capacity,
                          uint32_t* out_generation, uint64_t* out_statements);
// Fable2/src/vfs/src/core/texture_layer_state.cpp.
uint32_t StatedTextureLayers(uint32_t entity, uint32_t* out, uint32_t capacity,
                             uint32_t* out_generation, uint64_t* out_statements);
// Fable2/src/vfs/src/core/decal_state.cpp.
uint32_t StatedDecals(uint32_t* out, uint32_t capacity, uint32_t* out_serial);
uint32_t StatedFxMeshes(uint32_t* out, uint32_t capacity);
void StatedFxMeshLook(uint32_t descriptor, uint32_t handle, float* out);
// Fable2/src/vfs/src/core/item_dye_layer_state.cpp.
uint32_t StatedItemDyeLayers(uint32_t item_address, uint32_t* out, uint32_t capacity,
                             uint32_t* out_generation);

// Defined in Fable2/src/vfs/src/core/pubgame_state.cpp — the pub-game seam. The host posts what
// Godot's game said and the pump replacement in the payload drains it on the guest's render
// thread; the events come back the other way.
void ClaimPubGames(bool claimed);
void PostPubGameCommand(uint32_t kind, const char* text, const double* numbers,
                        const int64_t* integers);
uint32_t TakePubGameEvents();

// The characters behind an LHString handle — two hops, see core/guest_state.h. Declared, not
// included: this file cannot see the vfs hook headers that header pulls in.

// The captured menu rows — see RefiiHostGetMenuRow. The host reads and the payload writes, so
// the store lives with the pump's other state, not beside the hook. Capacities are size_t to
// match the definition exactly; widening them here would be a silent ODR mismatch.
uint32_t MenuRowLabel(uint32_t index, char* out_label, size_t label_capacity);

/// The screen the published capture belongs to — the key that says whose population it is.
uint32_t MenuScreenId();
uint32_t MenuRows(uint32_t index, char* out_name, size_t name_capacity, char* out_parent,
                  size_t parent_capacity, uint32_t* out_id, int32_t* out_event_type,
                  uint64_t* out_generation, int32_t* out_rating);
uint32_t MenuRowItem(uint32_t index, char* out_icon, size_t icon_capacity, int32_t* out_emphasis,
                     uint32_t* out_words, size_t word_capacity);
}  // namespace refii::vfs
#include "refii_host.h"

#include "core/gui_native_calls.h"
#include "gui/gui_display_box.h"
#include "gui/gui_menu_chunks.h"
#include "core/queue_put_back.h"
#include "gui/gui_told_statements.h"

using namespace refii::hostcore;

namespace {

/// A guest string handle — `char***`, exactly two hops, NUL-terminated, capped. The same
/// shape `sub_826B9510` reads; a zero inner pointer is the guest's own empty string. Local on
/// purpose: the VFS library's reader of this shape is not exported, and calling it links as a
/// dangling lazy PLT entry that ends the process with "symbol lookup error" (exit 127).
std::string ReadGuestString(const uint8_t* base, uint32_t handle, std::size_t cap = 256) {
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };
    if (base == nullptr || !plausible(handle)) {
        return std::string();
    }
    const uint32_t inner = GuestWordAt(base, handle);
    if (inner == 0 || !plausible(inner)) {
        return std::string();
    }
    const uint32_t characters = GuestWordAt(base, inner);
    if (!plausible(characters)) {
        return std::string();
    }
    // A 64-byte block at a time, never across one: a read reaches no page the characters up to the
    // NUL do not already lie on. This runs for every attachment's dummy on every guest frame.
    std::string out;
    while (out.size() < cap) {
        const uint32_t at = characters + static_cast<uint32_t>(out.size());
        const std::size_t chunk = std::min<std::size_t>(64u - (at & 63u), cap - out.size());
        char block[64];
        CopyGuest(base, at, block, chunk);
        const void* end = std::memchr(block, 0, chunk);
        if (end != nullptr) {
            out.append(block, static_cast<const char*>(end) - block);
            break;
        }
        out.append(block, chunk);
    }
    return out;
}

/// A guest WIDE string handle. The game has TWO string classes and they are not
/// interchangeable:
///
///   * `char***` — two hops to 8-bit characters. Element names, region names, the record names
///     at `record+8`. `ReadGuestString` above reads this one, and it is correct for it.
///   * The refcounted LHString — the SAME two hops, but the characters are UTF-16 and the block
///     carries its own length. Verified by decompile, not taken from a report: `sub_822D6408`
///     allocates a 16-byte block and counts its source as `_WORD`; `sub_82B3FDC8` finishes with
///     `a1[1] = count`, `*(_WORD *)(2*count + data) = 0` and `XMemCpy(data, src, 2 * count)`.
///     Its formatter `sub_8230AE78` prints through the wide literal at 0x820A3D7C, whose bytes
///     are `00 25 00 64 00 00` — `L"%d"`.
///
/// Block layout, from `sub_82B3FDC8`: `+0` character data, `+4` length in WIDE UNITS, `+8`
/// capacity in bytes with a flag in bit 0, `+12` refcount (the word `sub_822B88A8` reserves on
/// when it assigns a handle).
///
/// Reading this class byte-wise returns EMPTY for all ASCII content, because the high byte of
/// every unit is the 0 the walk stops at — the guest is big-endian. That is what left the good
/// and evil deed sprites with an icon and no amount.
///
/// UTF-16 BE out as UTF-8, basic plane only: a surrogate pair cannot appear in these strings,
/// and emitting a lone surrogate is worse than stopping at it.
std::string ReadGuestWideString(const uint8_t* base, uint32_t handle, uint32_t cap = 256) {
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };
    if (base == nullptr || !plausible(handle)) {
        return std::string();
    }
    const uint32_t block = GuestWordAt(base, handle);
    if (!plausible(block)) {
        return std::string();  // the guest's own empty string
    }
    const uint32_t characters = GuestWordAt(base, block);
    if (!plausible(characters)) {
        return std::string();
    }

    uint32_t length = GuestWordAt(base, block + 4u);
    if (length > cap) {
        length = cap;
    }

    std::string out;
    for (uint32_t c = 0; c < length; ++c) {
        const uint16_t unit = GuestHalfAt(base, characters + (c * 2u));
        if (unit == 0) {
            break;
        }

        if (unit < 0x80u) {
            out.push_back(static_cast<char>(unit));
        } else if (unit < 0x800u) {
            out.push_back(static_cast<char>(0xC0u | (unit >> 6)));
            out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        } else {
            out.push_back(static_cast<char>(0xE0u | (unit >> 12)));
            out.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
            out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        }
    }

    return out;
}

/// FNV-1 over a name, the hash Babel keys on — multiply then xor, 32-bit.
uint32_t RefiiFnv1(const std::string& text) {
    uint32_t hash = 0x811C9DC5u;
    for (const char c : text) {
        hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(c);
    }

    return hash;
}




/// Guest globals and device fields the no-GPU latch needs. All IDA-proven against
/// `default.xex`; see ArmGuestNoGpuMode for the proof chain.
constexpr refii::guest::Addr kGuestDevicePointer{0x83360364u};  // g_D3DDevice_PresentModule
constexpr uint32_t kDeviceNoGpuFlagByte = 0x2ABDu;     // dev+0x2ABD
constexpr uint8_t kDeviceNoGpuFlagBit = 0x02u;         // "GPU is dead" latch
constexpr uint32_t kDeviceDebugConsumer = 0x541Cu;     // dev+0x541C, NULL in retail
constexpr uint32_t kDeviceOutstandingIbs = 0x2B00u;    // dev+0x2B00

// Atomics: written by the fast-arm watcher thread below as well as the step thread.
std::atomic<bool> g_no_gpu_armed{false};
std::atomic<uint32_t> g_guest_device{0};

std::atomic<bool> g_arm_watcher_stop{false};
std::thread g_arm_watcher;

/// The completion publisher's own thread. Published from the host's render loop, a present
/// waited up to a whole host frame for its completion, and the guest's frame became
/// present + host-frame: measured 14.4 guest frames/s under a 27 fps host (2026-09-04). A
/// 1 ms poll answers each present as the GPU would have, and the guest paces itself.
/// REFII_COMPLETION_THREAD=0 keeps the once-per-step publishing for comparison.
std::atomic<bool> g_completion_stop{false};
std::thread g_completion_thread;
refii::TracedMutex g_completion_mutex{"guest I/O completion"};
uint32_t g_completions_by_thread = 0;

/// Puts Fable 2's own D3D into the "no GPU present" mode it ships with.
///
/// THE PROBLEM. With no GPU behind it, the guest boots, initialises its ring buffer,
/// and then parks forever in `D3D_CDevice_BlockOnFence` (0x82242668) spinning on the
/// fence write-back at `*(*(dev+0x2A90))` that only a command processor would ever
/// advance. The spin does not yield — it pegs a core — and it is reached from
/// `D3DDevice_BlockUntilIdle` on the SECOND `D3D_InitializeRingBuffer` entry. That is
/// where every run has been stopping, ~34ms into boot, long before any game logic.
///
/// THE FIX IS THE GAME'S OWN. Bit 0x02 of `dev+0x2ABD` is a one-way latch that D3D
/// checks in three places:
///   * `D3D_CDevice_AddCallsToPrimaryBuffer` (0x822866F8) returns without emitting PM4,
///     without advancing the write index, and WITHOUT the doorbell store at 0x82286870
///     — the only `0x7FC80714` MMIO write in the image. D3D9 stops posting.
///   * `D3D_CDevice_SetFence` (0x822986EC) writes the fence and ring position into the
///     write-back block ITSELF, from the CPU, exactly as the GPU would have.
///   * `D3D_CBlocker_Check` (0x82BA1FF8) returns 0, so every GPU wait in the binary
///     exits on its first iteration — including the spin above.
/// Nothing in the executable ever clears the bit; a census of all 17 stores to
/// `dev+0x2ABD` found only `ori 2` and `ori 3` setters, and `StopWorkerQueue`'s
/// `andi 0xDF` preserves it.
///
/// WHY THE GUEST CANNOT DO IT ALONE. It tries. `D3D_CDevice_KickOff` sets this very bit
/// at 0x821E8FA0 when `VdIsHSIOTrainingSucceeded` reported failure — which our stub
/// does. But the store sits AFTER a `BlockOnFence` at 0x821E8F94, so the guest has to
/// survive the wait in order to reach the flag that would have skipped it. The hang
/// handler would also latch it, but only after `D3D_CBlocker_Check`'s 5000-tick
/// deadline, and that deadline is measured against a KTHREAD field the host never
/// advances, so it never expires. Hence: the host sets it.
///
/// SAFE TO ARM LATE. `D3D_CBlocker_Check` is re-evaluated every iteration of every
/// spin, so arming after the guest is already parked releases it just the same. That is
/// why polling from the step loop is sufficient and no hook is needed.
///
/// Requires `dev+0x541C == 0` (a debug/PIX command consumer, with no store anywhere in
/// the executable — NULL in retail). A non-NULL consumer would take precedence in both
/// `AddCallsToPrimaryBuffer` and `SetFence`, so the arm is skipped if one is present.
void ArmGuestNoGpuMode() {
    if (g_no_gpu_armed) {
        return;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    // Guest memory is big-endian; these are plain aligned loads the guest also does.
    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };

    const uint32_t device = load_be32(kGuestDevicePointer);
    if (device == 0) {
        // Device not created yet; try again next step.
        return;
    }

    if (load_be32(device + kDeviceDebugConsumer) != 0) {
        REXLOG_WARN(
            "Refii host: dev+0x541C is non-NULL; leaving the no-GPU latch alone");
        g_no_gpu_armed = true;
        return;
    }

    const uint8_t before = GuestByteAt(base, device + kDeviceNoGpuFlagByte);
    const uint8_t latched = static_cast<uint8_t>(before | kDeviceNoGpuFlagBit);
    StoreGuest(base, device + kDeviceNoGpuFlagByte, &latched, sizeof(latched));
    g_no_gpu_armed = true;
    g_guest_device = device;

    REXLOG_INFO(
        "Refii host: armed the guest's no-GPU mode (device=0x{:08X}, "
        "dev+0x2ABD 0x{:02X} -> 0x{:02X})",
        device, before, latched);
}

/// Keeps `D3DDevice_BlockUntilIdle` satisfiable.
///
/// This is the ONE GPU wait the no-GPU latch does not cover. `D3DDevice_BlockUntilIdle`
/// (loop at 0x82BA27A4) spins on `dev+0x2B00 == 0` with no CBlocker, no watchdog and no
/// escape hatch of any kind — the only unguarded spin of its class in the binary:
///
///     82ba27a4  lwz   r11, 0x2B00(r31)
///     82ba27a8  cmpwi cr6, r11, 0
///     82ba27ac  bne   cr6, loc_82BA27A4
///
/// `D3D_CDevice_QueueIndirectBuffer` INCREMENTS it, and nothing in the guest ever
/// decrements it — on hardware the GPU retired those indirect buffers. The increment is
/// by zero at four of its five call sites, but `D3D_StopWorkerQueue` passes one, and
/// that is reached from `D3DDevice_EndTiling` — a path Fable 2 does use. So the counter
/// can become nonzero and then never come back down, wedging the guest permanently with
/// no diagnostic.
///
/// Zeroing it every step is correct precisely BECAUSE there is no GPU: with nothing to
/// retire indirect buffers, "outstanding" can only ever grow. The binary's own hang
/// handler does exactly this write at 0x82BA7678.
/// Retires the guest's command-stream rings, which nothing else does headless.
///
/// The guest reserves ring space through `sub_8222ABE0`, and when the ring looks full it
/// SPINS — `Sleep(0)` with no timeout and no GPU-dead check:
///
///     while ( a1[9] >= a1[8] ) {                       /*0x8222ACB4*/
///       if ( v6 + a1[8] <= a1[9] ) break;
///       sub_82CC2E80(0);                                // Sleep(0)
///       sub_821A5BD0(a1); }                             // re-read the reader window
///
/// and `sub_821A5BD0` sources that window from exactly one place:
///
///     v2 = a1[16];  a1[9] = v2;                         /*0x821A5BF4 / 0x821A5C04*/
///
/// `a1[16]` is +64, the CONSUMER's read-back. Neither function consults `dev+0x2ABD`, so
/// the no-GPU latch above does not release this one — it is a producer waiting on a
/// consumer, not on hardware. With Godot rendering and the guest's commands going nowhere,
/// there is no consumer, so the reserve waits forever. Measured: a New Game parks here
/// before `GameFlow_QueueCommand(CmdLevelChange)` at 0x823115D0, which is why the level
/// change is never even queued.
///
/// The retire itself is not invented here. It mirrors the guest's own
/// SceneStreamSyncPublishCursor + SceneStreamRefresh pair, the same mapping the renderer
/// module already implements in RepairFableSceneStreamPublishHandoff — that code is correct
/// but lives behind REFII_BUILD_RENDERER_HOOKS, which is OFF by design because Godot
/// renders. Draining is a command-stream contract, not renderer work, so it belongs here
/// where it is always built.
///
/// Discarding the commands is the right semantic rather than a shortcut: the guest is a
/// simulation server, it states what it would have drawn, and a null device consumes and
/// drops it.
///
/// Four slots, `dword_8349F9B0[0..3]`. Field map (shared with the renderer's):
///   +8 capacity · +12 read cursor · +16 visible end · +20 read limit · +24 last read
///   +28 read cap · +32 write cursor · +36 write consumed · +40 write mark · +52 write wrap
///   +60 published end · +64 READER CONSUMED · +68 published cap
void RetireGuestSceneStreams() {
    // OFF unless asked for: measured harmful (it stops the startup logos playing) and its
    // premise was refuted — the ring is never full when the reserve is blocked. Kept only
    // because the field map below is correct and a real consumer will want it.
    static const bool enabled = [] {
        const char* value = std::getenv("REFII_RETIRE_SCENE_STREAMS");
        return value != nullptr && std::string_view(value) == "1";
    }();
    if (!enabled) {
        return;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto write32 = [base](uint32_t at, uint32_t value) {
        const uint32_t raw = __builtin_bswap32(value);
        StoreGuest(base, at, &raw, sizeof(raw));
    };

    // A cursor must land inside its own ring. Anything else means we are looking at a slot
    // that is not a scene stream — half-built, freed, or simply another kind of object —
    // and writing to it would be scribbling on guest memory.
    constexpr uint32_t kMaxRingBytes = 16u * 1024u * 1024u;
    const auto sane_offset = [](uint32_t offset, uint32_t cap) {
        return cap != 0u && cap <= kMaxRingBytes && offset <= cap;
    };

    const uint32_t kSchedulerStreams = refii::guest::Address(0x8349F9B0u);

    for (uint32_t slot = 0; slot < 4u; ++slot) {
        const uint32_t object = read32(kSchedulerStreams + slot * 4u);
        if (object < 0x10000u || object >= 0xC0000000u) {
            continue;
        }

        const uint32_t capacity = read32(object + 8u);
        const uint32_t write_cursor = read32(object + 32u);
        const uint32_t write_consumed = read32(object + 36u);
        const uint32_t write_mark = read32(object + 40u);
        const uint32_t write_wrap = read32(object + 52u);
        const uint32_t published_end = read32(object + 60u);
        const uint32_t reader_consumed = read32(object + 64u);
        const uint32_t published_cap = read32(object + 68u);

        if (!sane_offset(write_cursor, capacity) ||
            !sane_offset(write_mark, capacity) ||
            !sane_offset(write_wrap, capacity) ||
            !sane_offset(reader_consumed, capacity)) {
            continue;
        }

        // Publish the writer's boundary and import the reader's, exactly as the guest's own
        // publish does. This does not consume unread messages by itself.
        uint32_t next_published_end = write_mark;
        uint32_t next_published_cap = write_wrap;
        if (reader_consumed < write_cursor || reader_consumed > write_wrap) {
            next_published_cap = capacity;
        }
        if (next_published_end == next_published_cap) {
            next_published_end = 0u;
        }

        // The refresh half. The renderer's version stops at `last_read - 1`, because when
        // the renderer is built there is a real consumer moving the read head and this only
        // has to IMPORT its position. Headless there is no such consumer, so importing a
        // head that never moves publishes a ring that never drains: measured, this left
        // reader_consumed pinned at 0xEF while the writer advanced, which stalls the moment
        // the writer wraps onto it.
        //
        // So the host asserts what a null device actually did: it consumed everything. The
        // read head follows the write head, and reader_consumed sits ONE BEHIND it — the
        // ring's own "empty" encoding, the same `x == 0 ? capacity - 1 : x - 1` the guest
        // uses. That satisfies both of `sub_8222ABE0`'s spins at once, which is the tell
        // that it is the right state rather than a number that happens to unblock one:
        // the pre-wrap loop wants `write >= read` and the post-wrap loop wants the reader
        // not to be trapping the writer, and "empty" is the only state that is both.
        const uint32_t next_reader_consumed =
            write_cursor == 0u ? capacity - 1u : write_cursor - 1u;

        if (!sane_offset(next_reader_consumed, capacity)) {
            continue;
        }

        bool changed = false;
        const auto store_if = [&](uint32_t at, uint32_t was, uint32_t now) {
            if (was != now) {
                write32(at, now);
                changed = true;
            }
        };

        // PRODUCER-FACING FIELDS ONLY.
        //
        // An earlier version also wrote the consumer's own read state — +12 read cursor,
        // +16 visible end, +20 read limit, +24 last read, +28 read cap — mirroring what the
        // renderer's repair does. That killed the process within seconds of guest init, and
        // an A/B with REFII_RETIRE_SCENE_STREAMS=0 confirmed the write was the cause.
        //
        // The reason is that the guest's consumer thread is NOT absent, only starved: it is
        // the one spinning at 0x822DFF2C inside sub_822DFE08, which our own drain logger has
        // been reporting for months. Moving its read head from Godot's thread while it is
        // mid-read hands it a position it never asked for. Publishing the writer's boundary
        // and the space the producer may use is safe; steering the reader is not.
        store_if(object + 36u, write_consumed, reader_consumed);
        store_if(object + 60u, published_end, next_published_end);
        store_if(object + 68u, published_cap, next_published_cap);
        store_if(object + 64u, reader_consumed, next_reader_consumed);

        // Once per slot, so a run says the drain engaged without printing every step.
        static bool announced[4] = {false, false, false, false};
        if (changed && !announced[slot]) {
            announced[slot] = true;
            REXLOG_INFO(
                "Refii host: retiring guest scene stream slot {} (object=0x{:08X} "
                "capacity=0x{:X} write=0x{:X} reader_consumed 0x{:X} -> 0x{:X})",
                slot, object, capacity, write_cursor, reader_consumed,
                next_reader_consumed);
        }
    }
}

/// The guest's frame-sync state, from `Render_FrameCompletionCallback_INFERRED` (0x82B90180)
/// and its reader `sub_82B90048`. Two records of 24 bytes — a double buffer, not a ring.
/// Base = `kFrameSyncRecords + 24*i`:
/// `+0` u64 stamp (this frame), `+8` u64 stamp (the previous one, carried), `+16` u32 completed
/// count, `+20` u32 reader lock. `kFrameSyncIndex` names the newest published record.
constexpr refii::guest::Addr kPresentFrameCount{0x83497120u};
constexpr refii::guest::Addr kFrameSyncIndex{0x83497124u};
constexpr refii::guest::Addr kFrameSyncRecords{0x83491D98u};
constexpr uint32_t kFrameSyncStride = 24u;
constexpr uint32_t kFrameSyncSlots = 2u;

/// How many completions one step may report. A bound, not a rate — see the loop.
constexpr uint32_t kMaxCompletionsPerStep = 16u;

uint32_t g_frame_completions_signalled = 0;

/// Reports the swap completions that no GPU is here to report.
///
/// `Render_PresentFrame_INFERRED` bumps `g_PresentFrameCount` at 0x82B6F290; on hardware the
/// GPU then runs `Render_FrameCompletionCallback_INFERRED` to publish a new frame record.
/// `sub_82B6FCE8` reads both and hands `presented - completed + 1` to the 3D Engine thread as
/// its frame deadline, so completions frozen at zero make that term grow without bound and the
/// game thread parks for ever in `while (!*(BYTE*)(a1+168)) SleepEx(0,1)`.
///
/// The six steps are the callback's own, in its order, with the index published LAST exactly
/// as its `stwcx.` does: pick the slot the reader is not holding, carry the previous stamp
/// into `+8`, stamp `+0` with the guest timebase, set `+16` to the previous count + 1, then
/// publish the index.
void SignalGuestFrameCompletions() {
    // Only while standing in for the GPU: with a real submission path the guest's own callback
    // owns this state.
    if (!g_no_gpu_armed) {
        return;
    }

    // Two publishers (the step and the completion thread) must not race the double buffer.
    std::lock_guard publishers(g_completion_mutex);

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto write32 = [base](uint32_t at, uint32_t value) {
        const uint32_t raw = __builtin_bswap32(value);
        StoreGuest(base, at, &raw, sizeof(raw));
    };

    const uint32_t presented = read32(kPresentFrameCount);

    // Caught up: nothing presented since the last step, so nothing to complete.
    uint32_t index = read32(kFrameSyncIndex);
    if (index >= kFrameSyncSlots) {
        // The callback only ever publishes 0 or 1; anything else is not this state.
        return;
    }

    for (uint32_t reported = 0; reported < kMaxCompletionsPerStep; ++reported) {
        const uint32_t current = kFrameSyncRecords + (index * kFrameSyncStride);
        const uint32_t completed = read32(current + 16u);

        // Count, not index: `presented` and `+16` are matching counters, caught up one per
        // iteration so a step spanning several presents leaves no permanent lag.
        if (completed >= presented) {
            break;
        }

        const uint32_t next = read32(kFrameSyncRecords + 20u) != 0u ? 1u : 0u;
        const uint32_t target = kFrameSyncRecords + (next * kFrameSyncStride);

        // The previous stamp moves into +8; +0 takes the time now. Two big-endian halves: the
        // guest's qword is stored high word first.
        write32(target + 8u, read32(current));
        write32(target + 12u, read32(current + 4u));

        // The guest's own clock: `mftb` in recompiled code is the payload's tick count (under the
        // driven time base, the virtual tick), so the stamp is in the units `sub_82B90048`'s
        // readers divide by `dbl_834970A8` to get seconds.
        const uint64_t now = refii_bridge_rex_query_guest_tick_count();
        write32(target, static_cast<uint32_t>(now >> 32));
        write32(target + 4u, static_cast<uint32_t>(now & 0xFFFFFFFFu));

        write32(target + 16u, completed + 1u);

        // Last, and on its own: the reader takes the index and then the record it names.
        write32(kFrameSyncIndex, next);
        index = next;
        ++g_frame_completions_signalled;
    }
}

void KeepGuestIdleCounterClear() {
    if (!g_no_gpu_armed || g_guest_device == 0) {
        return;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const uint32_t zero = 0;
    StoreGuest(base, g_guest_device + kDeviceOutstandingIbs, &zero, sizeof(zero));
}

}  // namespace


#if defined(REXGLUE_ENABLE_REXNET)
#include <rex/net/rexnet.h>
#include <rex/net/rexnet_ffi.h>
#include <rex/net/pseudonym.h>

namespace {
/// `g_XrnmNetContext` — IDA names it; `sub_822EDCF0` reads the admit gates off it.
constexpr refii::guest::Addr kXrnmNetContext{0x83497490u};

/// Are we hosting a session at all — the session machinery's byte, never ours to write.
constexpr uint32_t kNetHostingByte = 0xF3u;

/// Will we admit a joiner: `XboxLive.SetSessionJoinable`'s byte.
constexpr uint32_t kNetJoinableByte = 0xF5u;

/// `g_XrnmNetContext`, or 0 when the guest has none yet.
uint32_t NetContext(const uint8_t* base) {
    const uint32_t context = base == nullptr ? 0u : GuestWordAt(base, kXrnmNetContext);
    return NetPlausible(context) ? context : 0u;
}

/// The guest's own bytes say it is in a session as a joiner: `g_XrnmNetContext` +52 session,
/// +54 joiner. TitleCallbacks::guest_joined_session.
bool GuestJoinedSession(const uint8_t* base) {
    const uint32_t context = NetContext(base);
    return context != 0u && GuestByteAt(base, context + 52u) != 0u &&
           GuestByteAt(base, context + 54u) != 0u;
}
}  // namespace
#endif


extern "C" {

/// THE GUEST'S STATEMENTS TO ITS PRESENTATIONS (hooks/gui_element_hooks.cpp), oldest first: six
/// words each — kind (1 slide entry, 2 paused, 3 attribute, 4 active), presentation, root name
/// hash, element ordinal, then the slide / paused / attribute id / on, and the attribute's value
/// bits. Takes them; returns how many were written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeToldStatements(uint32_t* out_words, uint32_t word_capacity) {
    return refii::vfs::TakeToldStatements(out_words, word_capacity);
}

/// The texts the guest wrote into its presentations' elements (`SetText` / `SetTextFromTag`,
/// already localised), oldest first: four words each — presentation, root name hash, ordinal,
/// UTF-16 length — with the units back to back in `units`. Takes them; returns how many.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeToldTexts(uint32_t* out_words, uint32_t word_capacity,
                                                      uint16_t* units, uint32_t unit_capacity) {
    return refii::vfs::TakeToldTexts(out_words, word_capacity, units, unit_capacity);
}

/// A HEADLESS presentation's file (hooks/gui_headless_presentation.cpp), the key its statements
/// carry being (presentation, serial in the root-hash word, `0x80000000 | id`): the path its create
/// named, NUL-terminated into `out`; returns the length, 0 when `serial` is not the one there.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostToldHeadlessFile(uint32_t presentation, uint32_t serial, char* out,
                                                         uint32_t capacity) {
    return refii::vfs::ToldHeadlessFile(presentation, serial, out, capacity);
}

/// One element of a headless presentation by the id its statements carry: its parent's id (0 the
/// root) and its name hash. 1 when known, else 0.
REFII_HOST_ABI_EXPORT int32_t RefiiHostToldHeadlessElement(uint32_t presentation, uint32_t serial, uint32_t id,
                                                           uint32_t* parent_id, uint32_t* name_hash) {
    return refii::vfs::ToldHeadlessElement(presentation, serial, id, parent_id, name_hash) ? 1 : 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostUiDataGeneration(void) { return refii_bridge_ui_data_generation(); }

/// The taken silver keys still in the world: (address, mask) pairs into `out`, at most `capacity`
/// words. Returns the pairs written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCollectedKeys(uint32_t* out, uint32_t capacity) {
    return out != nullptr ? refii_bridge_collectable_masks(out, capacity) : 0u;
}

/// The next question a co-op script put to the partner: 1 with its ticket, kind (1 sleep, 2 sex,
/// 0 a mod's own question, its words in `text`), asking player and detail (the sleep type); 0 when
/// none is waiting.
REFII_HOST_ABI_EXPORT int32_t RefiiHostConsentTake(uint32_t* ticket, int32_t* kind, int32_t* asker, int32_t* detail,
                                                   char* text, uint32_t capacity) {
    if (ticket == nullptr || kind == nullptr || asker == nullptr || detail == nullptr) {
        return 0;
    }
    return refii_bridge_consent_take(ticket, kind, asker, detail, text, capacity);
}

/// The partner's answer to a question taken with RefiiHostConsentTake.
REFII_HOST_ABI_EXPORT void RefiiHostConsentGive(uint32_t ticket, int32_t yes) { refii_bridge_consent_give(ticket, yes); }

/// The player who owns the open in-game GUI: 2 when player two opened it (the guest routes their
/// pause to the henchman root and only they may close it), else 1. The pause root's player as its
/// setter stated it, while the guest's game flow says the GUI mode is up (the pump restates it each
/// guest tick); meaningful while a menu is open.
REFII_HOST_ABI_EXPORT int RefiiHostGetMenuOwner(void) { return refii_bridge_menu_owner_two() != 0 ? 2 : 1; }

/// The player whose expression wheel is open, 0 for none, as the wheel's open and close state it
/// (lhGuiManager OpenExpressionWheel TU1 0x8236F110 / disc 0x82337310, CloseExpressionWheel
/// 0x8236F340 / 0x82337540; mod_events.cpp).
REFII_HOST_ABI_EXPORT int RefiiHostGetExpressionWheelPlayer(void) { return refii_bridge_expression_wheel_player(); }

REFII_HOST_ABI_EXPORT int RefiiHostTakeExitRefused(void) { return refii_bridge_take_exit_refused(); }

/// The things whose use prompt answers player two this frame (coop_hero_hooks.cpp, the nearer
/// player rule), for seating their prompt icons in player two's half. Returns the count written.
REFII_HOST_ABI_EXPORT int RefiiHostGetExitWaiting(void) { return refii_bridge_exit_waiting(); }

/// The things whose use prompt only player two sees this frame (player one's half hides them):
/// each prompt's in-range test is run for both players (coop_hero_hooks.cpp), and a targeted
/// thing's "Interact" icon is its targeting players' (target_highlight.cpp). Returns the count.
REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerOneHiddenPrompts(uint32_t* out, int capacity) {
    return refii_bridge_prompts_one_hidden(out, capacity);
}

/// Who targets each thing, as lhECTargeted_SetTargetedByPlayer leaves it (target_highlight.cpp):
/// (thing, mask) pairs, bit 0 player one, bit 1 player two, for each split view to keep its own
/// player's targeting highlight. Returns the pairs written.
REFII_HOST_ABI_EXPORT int RefiiHostGetTargetedBy(uint32_t* out_pairs, int capacity_pairs) {
    return refii_bridge_targeted_by(out_pairs, capacity_pairs, 0);
}

/// Player two's own hold-to-use meter on split screen: the fraction 0..1, and whether they hold A.
/// The guest keeps one, for player one; player two's is integrated at the guest's own rates and
/// completes through the guest's own use (coop_hero_hooks.cpp).
/// Player two's own hold-to-use prompt in local co-op (the guest keeps one, player one's; player
/// two's is kept beside it, coop_hero_hooks.cpp): 1 with its guest world point (3 floats) and
/// title (UTF-8), 0 when they have none.
REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoHoldPrompt(float* out_at, char* title, uint32_t capacity) {
    return refii_bridge_player_two_hold_prompt_get(out_at, title, capacity);
}

/// Local co-op: the use-prompt text the player the guest's prompt is NOT for would be shown, per
/// thing (coop_hero_hooks.cpp, the dry pass): `thing \t player \t title` lines, UTF-8. Returns
/// the byte count, 0 when none or the buffer is short.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPromptTitles(char* out, uint32_t capacity) {
    return refii_bridge_prompt_titles(out, capacity);
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoHold(float* out_fraction, int* out_pressed) {
    float fraction = 0.0f;
    int32_t pressed = 0;
    refii_bridge_player_two_hold(&fraction, &pressed);
    if (out_fraction != nullptr) *out_fraction = fraction;
    if (out_pressed != nullptr) *out_pressed = pressed;
    return 1;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetExitRequest(float* out_xyz) {
    return refii_bridge_exit_request(out_xyz);
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoPrompts(uint32_t* out, int capacity) {
    return refii_bridge_prompts_two(out, capacity);
}

REFII_HOST_ABI_EXPORT uint64_t RefiiHostGetNewGameState(uint32_t* generation) {
    return refii::vfs::NewGameTransitionResult(generation);
}


REFII_HOST_ABI_EXPORT int32_t RefiiHostGuestBuild(void) { return refii_guest_build(); }


REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddress(uint32_t tu1_address) {
    return refii_guest_address(tu1_address);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddressToTu1(uint32_t live_address) {
    return refii_guest_address_to_tu1(live_address);
}

/// THE SHAPE, PER PART, as the guest states it to `sub_82A48B90`: per morphed part [model hash,
/// count, then count x (target model hash, weight float bits)]; the part itself takes 1 - sum.
/// A part that is not listed draws as its own model. `out_statements` low word counts every
/// part submission seen (0 means the hook never ran), high word those that listed a target.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartMorphs(uint32_t entity, uint32_t* out_words,
                                                            uint32_t word_capacity,
                                                            uint32_t* out_generation,
                                                            uint64_t* out_statements) {
    return refii::vfs::StatedPartMorphs(entity, out_words, word_capacity, out_generation,
                                        out_statements);
}

/// THE SKIN AND THE CLOTH. Per original texture the guest composites: [original texture id,
/// layer count | kind << 16, then count x 7 words {texture id, blend mode, weight bits,
/// has-tint, tint bytes B G R A, brightness bits, bump texture id}]. Kind 0 is a composite,
/// layers in draw order with layer 0 the base; kind 1 is the overlay list (scars, mode-0
/// morphs) the guest draws over the finished material. Covers replacement
/// and skin-set textures, stat morphs, tattoos and dyes. Returns the words written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityTextureLayers(uint32_t entity, uint32_t* out_words,
                                                               uint32_t word_capacity,
                                                               uint32_t* out_generation,
                                                               uint64_t* out_statements) {
    return refii::vfs::StatedTextureLayers(entity, out_words, word_capacity, out_generation,
                                           out_statements);
}

/// One inventory item INSTANCE's dye composites, as the guest's own dye builder states them for
/// the item's dye component — [original, count | kind << 16, count x 7 layer words] per texture,
/// the words RefiiHostGetEntityTextureLayers uses. `item` is the instance's guest address as the
/// captured row names it (RefiiHostGetMenuRowItem); asking names it for the guest's next GUI tick,
/// which reads that one item while a captured row still holds it (guest_tick_pump.cpp
/// StateRowItemDyeLayers — the list's one stated exception: the guest states no garment colour in a
/// list). Empty for an item that is not dyed, or not read yet. Returns the words written;
/// `out_generation` moves when the layers changed.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetItemDyeLayers(uint32_t item, uint32_t* out_words,
                                                        uint32_t word_capacity,
                                                        uint32_t* out_generation) {
    refii_bridge_want_item_dye_layers(item);
    return refii::vfs::StatedItemDyeLayers(item, out_words, word_capacity, out_generation);
}

/// THE PAUSE MINIMAP'S QUEST MARKERS, as the guest states them: each marker the pass
/// `sub_826A3150` (disc `sub_8240F508`) hands its placer `sub_826A3FD8` (disc `sub_82410390`) —
/// gui_pause_hooks.cpp. `out_hashes[i]` is the token, 0 `Quest`, 1 `QuestExit`, 2 `QuestGiver`
/// (`Foreground.ReusableTokens.<name>`); `out_values` takes 10 floats per marker, the world point
/// x, y, z then seven zeros. The table empties as each open's pass begins. Returns how many.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMapMarkers(uint32_t* out_hashes,
                                                      float* out_values,
                                                      uint32_t cap) {
    if (out_hashes == nullptr || out_values == nullptr || cap == 0) {
        return 0;
    }
    uint32_t tokens[16] = {};
    float world[16 * 3] = {};
    const uint32_t count = refii::vfs::PauseMarkers(tokens, world, cap < 16u ? cap : 16u);
    for (uint32_t i = 0; i < count; ++i) {
        out_hashes[i] = tokens[i];
        float* record = out_values + (i * 10u);
        for (uint32_t lane = 0; lane < 10u; ++lane) {
            record[lane] = lane < 3u ? world[(i * 3u) + lane] : 0.0f;
        }
    }
    return count;
}

/// The screen fade as the guest's own start and advance state it (screen_fade_stated.cpp): the
/// colour the lerp `sub_822C7970` gives — `from + t * (to - from)` per byte, truncated, the bytes
/// B, G, R, A — as four floats 0..1 in RGBA order, the progress `t`, and the lock the advance
/// tested. "Faded in" is alpha zero. Returns the fader, 0 before the guest stated one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetScreenFade(float* out_rgba, float* out_progress,
                                                     uint32_t* out_locked) {
    uint8_t from[4]{};
    uint8_t to[4]{};
    float t = 0.0f;
    uint32_t locked = 0u;
    const uint32_t fader = refii_bridge_screen_fade_read(from, to, &t, &locked);
    t = !(t > 0.0f) ? 0.0f : (t > 1.0f ? 1.0f : t);

    uint8_t lerped[4] = {0, 0, 0, 0};
    for (uint32_t i = 0; i < 4u; ++i) {
        const float a = static_cast<float>(from[i]);
        const float b = static_cast<float>(to[i]);
        lerped[i] = static_cast<uint8_t>(a + t * (b - a));
    }

    if (out_rgba != nullptr) {
        out_rgba[0] = static_cast<float>(lerped[2]) * (1.0f / 255.0f);
        out_rgba[1] = static_cast<float>(lerped[1]) * (1.0f / 255.0f);
        out_rgba[2] = static_cast<float>(lerped[0]) * (1.0f / 255.0f);
        out_rgba[3] = static_cast<float>(lerped[3]) * (1.0f / 255.0f);
    }
    if (out_progress != nullptr) {
        *out_progress = t;
    }
    if (out_locked != nullptr) {
        *out_locked = locked;
    }
    return fader;
}

// The hero and its components — shared by every proxy below. `extern "C++"` because
// this file's exports live inside one big `extern "C"` block.

extern "C++" {
namespace {
uint32_t PlayerEntityForKey(const uint8_t* base, uint32_t wanted);

/// The player's hero ENTITY, or 0 when there is not one yet. One walk, shared by every proxy
/// below; from `sub_826B83D0` / `sub_82264230`:
///
///   game = *(*(0x83496AB8) + 12); sess = *(game + 0x8C)
///   key  = (bytes sess+0x34 && sess+0x35) ? 1 : 2          -- the co-op slot choice
///   hero = PlayerEntityForKey(key)                          -- EM slot first, player map second
///
/// ONE RESOLVER: the key is chosen here, the entity for it comes from `PlayerEntityForKey`, so
/// the hero, player one and player two can never be different addresses for the same key.
/// Every failure is 0, meaning "no hero", not "error": before a save is loaded there is none.
uint32_t ResolvePlayerHero(const uint8_t* base) {
    // Change-keyed: the step that refused, so a blank readout names its cause once.
    static std::atomic<int> last_step{-1};
    const auto refuse = [&](int step, uint32_t detail) {
        if (last_step.exchange(step) != step) {
            REXLOG_INFO("Refii hero resolver: no hero — refused at step {} (detail {:08X})", step, detail);
        }
        return 0u;
    };

    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t game_root = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) return refuse(1, game_root);
    const uint32_t game = read32(game_root + 12u);
    if (!plausible(game)) return refuse(2, game);

    // THE MANAGER ITSELF, not the game object's counted copy at +0x8C: `g_XrnmNetContext`
    // (0x83497490). A title-joined client had the copy empty in game, so this read 0 there and
    // the joiner's whole hero pipeline — vitals, inventory, the weapons fold — went blank.
    uint32_t session = read32(refii::guest::Address(0x83497490u));
    if (!plausible(session)) session = read32(game + 0x8Cu);
    if (!plausible(session)) return refuse(3, session);
    // Local co-op with player two's pause open: the menus are player two's (the payload's
    // MenusActForPlayerTwo, the same owner sample).
    const bool menus_for_two = refii_bridge_menu_owner_two() != 0 && read32(game + 0x11Cu) == 2u;
    const uint32_t player_key =
        menus_for_two || !(read8(session + 0x34u) != 0u && read8(session + 0x35u) != 0u) ? 2u : 1u;

    // In a session at all: +0x34 is the "in a session" byte, +0x36 the joiner bit. Solo reads
    // as host (+0x34 and +0x35 set, +0x36 clear), so this is false for a single-player run.
    const bool in_session = read8(session + 0x34u) != 0u && read8(session + 0x36u) != 0u;

    // PREFER THE ROLE'S SLOT, FALL BACK TO THE OTHER OUTSIDE A SESSION. Key 2 is right for a
    // joiner IN a session, but the same instance before it joins has only its own solo hero,
    // and a fixed key 2 read nothing there — so the joiner's own hero was never captured for
    // the kit. Solo there is one hero, so the other slot is this player's; in a session it
    // would be the other player's hero, so the lookup refuses.
    uint32_t hero = PlayerEntityForKey(base, player_key);
    if (hero == 0u && !in_session) {
        hero = PlayerEntityForKey(base, player_key == 1u ? 2u : 1u);
    }
    if (hero == 0u) return refuse(4, player_key);
    if (last_step.exchange(0) != 0) {
        REXLOG_INFO("Refii hero resolver: player key {} → entity {:08X}", player_key, hero);
    }
    return hero;
}

/// The player map's record for key 1 or 2 — the sorted stride-12 map `{s32 key, u32 record,
/// u32}` at `game+0x9C` (+8 begin, +12 end), as `sub_82264230`'s lower_bound reads it — or 0.
/// The record is the Player object: `+16` its entity handle's control block, `+0x28` its pad.
uint32_t PlayerMapRecordForKey(const uint8_t* base, uint32_t wanted) {
    if (base == nullptr) return 0u;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t game_root = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) return 0u;
    const uint32_t game = read32(game_root + 12u);
    if (!plausible(game)) return 0u;
    const uint32_t player_map = read32(game + 0x9Cu);
    if (!plausible(player_map)) return 0u;
    uint32_t entry = read32(player_map + 8u);
    const uint32_t entries_end = read32(player_map + 12u);
    if (!plausible(entry) || entries_end < entry || (entries_end - entry) > 12u * 64u) return 0u;
    for (; entry < entries_end; entry += 12u) {
        if (read32(entry) != wanted) continue;
        const uint32_t record = read32(entry + 4u);
        return plausible(record) ? record : 0u;
    }
    return 0u;
}

/// The entity of player 1 or 2, or 0.
///
/// THE GUEST'S OWN ACCESSORS FIRST. The entity manager keeps the two player entities in its own
/// slots — `EM+104` (object at `+108`) is player one, `EM+112` (object at `+116`) is player two:
/// `sub_8222C1B8` / `sub_828E5A78`, filled by `sub_82363BA0` when the entity with UniqueID 1 or
/// 2 registers. The player map at `game+0x9C` is tried second: a CLIENT's map has no key 1 (the
/// host's hero is a replica there) and a HOST's map was seen holding only key 1 with player two
/// up (2026-10-07, live), so a walk that trusted it alone reported the partner as 0 — Godot
/// then drew, posed and ranged that body like a villager, and the held-weapon export found no
/// body to read.
uint32_t PlayerEntityForKey(const uint8_t* base, uint32_t wanted) {
    if (base == nullptr) return 0u;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t game_root = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) return 0u;
    const uint32_t game = read32(game_root + 12u);
    if (!plausible(game)) return 0u;

    if (wanted == 1u || wanted == 2u) {
        // `**(game+88)+4`: the entity manager, as `sub_8246C970` reads it at 0x8246C9A8.
        const uint32_t holder = read32(game + 88u);
        const uint32_t slot = plausible(holder) ? read32(holder + 4u) : 0u;
        const uint32_t manager = plausible(slot) ? read32(slot) : 0u;
        const uint32_t object = !plausible(manager) ? 0u
                                : wanted == 1u ? refii_bridge_hero_one_holder(base, manager)
                                               : read32(manager + 116u);
        const uint32_t entity = plausible(object) ? read32(object) : 0u;
        if (plausible(entity)) return entity;
    }

    const uint32_t record = PlayerMapRecordForKey(base, wanted);
    if (record == 0u) return 0u;
    const uint32_t control = read32(record + 16u);
    if (!plausible(control)) return 0u;
    const uint32_t hero = read32(control);
    return plausible(hero) ? hero : 0u;
}

/// One component off an entity by id, or 0 when the entity does not carry it.
///
/// Check the presence bitmask FIRST — eight dwords at `entity+0x24`. The engine's own slow
/// path returns garbage on a miss rather than null, so skipping it succeeds with rubbish.
///
/// Then the accelerator: a byte table at `entity+0x8C` indexed by component id, giving the slot
/// in the sorted stride-8 `{s32 id, u32 ptr}` vector at `entity+0x48`..`+0x4C`. When there is no
/// accelerator the vector is walked. The id is re-checked after the accelerator hop, which the
/// guest does not do: another thread may be rebuilding the table and a stale slot index would
/// hand back a different component.
uint32_t EntityComponent(const uint8_t* base, uint32_t entity, uint32_t id) {
    if (base == nullptr || id >= 256u) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    if (!plausible(entity)) {
        return 0u;
    }

    const uint32_t present = read32(entity + 0x24u + (id / 32u) * 4u);
    if (((present >> (id % 32u)) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t pairs = read32(entity + 0x48u);
    const uint32_t pairs_end = read32(entity + 0x4Cu);
    if (!plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
        return 0u;
    }

    const uint32_t accelerator = read32(entity + 0x8Cu);
    if (plausible(accelerator)) {
        const uint32_t slot = read8(accelerator + id);
        const uint32_t at = pairs + 8u * slot;
        if (at + 8u <= pairs_end && read32(at) == id) {
            const uint32_t component = read32(at + 4u);
            return plausible(component) ? component : 0u;
        }
    }

    for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
        if (read32(at) == id) {
            const uint32_t component = read32(at + 4u);
            return plausible(component) ? component : 0u;
        }
    }

    return 0u;
}

/// One read of the entity fields the list walks ask of every entity: UniqueID (+0x14) through the
/// state byte (+0x90). A walk of thousands of entities made a guarded call per field.
using EntityFields = GuestSpan<0x14u, 0x91u>;

/// EntityComponent over fields already read.
uint32_t EntityComponentOf(const uint8_t* base, const EntityFields& fields, uint32_t id) {
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    if (((fields.Word(0x24u + (id / 32u) * 4u) >> (id % 32u)) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t pairs = fields.Word(0x48u);
    const uint32_t pairs_end = fields.Word(0x4Cu);
    if (!plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
        return 0u;
    }

    const auto pair_at = [base](uint32_t at, uint32_t& pair_id, uint32_t& component) {
        uint32_t pair[2];
        CopyGuest(base, at, pair, sizeof(pair));
        pair_id = __builtin_bswap32(pair[0]);
        component = __builtin_bswap32(pair[1]);
    };

    uint32_t pair_id = 0u;
    uint32_t component = 0u;
    const uint32_t accelerator = fields.Word(0x8Cu);
    if (plausible(accelerator)) {
        const uint32_t at = pairs + 8u * GuestByteAt(base, accelerator + id);
        if (at + 8u <= pairs_end) {
            pair_at(at, pair_id, component);
            if (pair_id == id) {
                return plausible(component) ? component : 0u;
            }
        }
    }

    for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
        pair_at(at, pair_id, component);
        if (pair_id == id) {
            return plausible(component) ? component : 0u;
        }
    }

    return 0u;
}
}  // namespace
}  // extern "C++"

/// THE HIGHLIGHTED ROW GODOT STATES for an in-game screen's list (screen 0 clears it): the guest's
/// highlighted-row reader `sub_826C1B50` answers with it (gui_menu_hooks.cpp) instead of asking
/// its hidden Lua menu, so every screen handler acts on the row the player is on.
REFII_HOST_ABI_EXPORT void RefiiHostSetHighlightedRow(uint32_t screen, int32_t event_type, int32_t id,
                                                      const char* name, const char* label) {
    refii_bridge_set_stated_highlight(screen, event_type, id, name, label);
}

/// THE FOLD DEPTH OF GODOT'S LIST for one screen (TU1 in-game id, or 1010 its front-end main
/// menu); 0 clears. The guest's `g_Menu:IsHighlightedItemAtRoot()` readers `sub_826C1D98` and
/// `sub_826DED60` answer from it (gui_menu_hooks.cpp).
REFII_HOST_ABI_EXPORT void RefiiHostSetListFoldDepth(uint32_t screen, uint32_t depth) {
    refii_bridge_set_list_fold_depth(screen, depth);
}

/// THE CO-OP SCREEN'S STATE (screen 24) — see `CoopScreen` in hero_derived.h. Twenty words:
/// known, session, online client, right signed in, peer ready, flags
/// 297/298, child, child level, right save user, save chosen, three pickboxes' selected /
/// enabled / frame state, then the two profile widgets' states.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopState(uint32_t* out_words, uint32_t word_capacity) {
    const refii::vfs::CoopScreen coop = refii::vfs::CoopScreenSnapshot();
    const uint32_t words[] = {
        coop.known ? 1u : 0u,
        static_cast<uint32_t>(coop.session),
        coop.online_client ? 1u : 0u,
        coop.right_signed_in ? 1u : 0u,
        coop.peer_ready ? 1u : 0u,
        coop.flag297 ? 1u : 0u,
        coop.flag298 ? 1u : 0u,
        coop.child ? 1u : 0u,
        coop.child_level ? 1u : 0u,
        static_cast<uint32_t>(coop.user_right_save),
        coop.save_chosen ? 1u : 0u,
        static_cast<uint32_t>(coop.pick_selected[0]),
        static_cast<uint32_t>(coop.pick_selected[1]),
        static_cast<uint32_t>(coop.pick_selected[2]),
        (coop.pick_enabled[0] ? 1u : 0u) | (coop.pick_enabled[1] ? 2u : 0u) | (coop.pick_enabled[2] ? 4u : 0u),
        static_cast<uint32_t>(coop.pick_state[0]),
        static_cast<uint32_t>(coop.pick_state[1]),
        static_cast<uint32_t>(coop.pick_state[2]),
        static_cast<uint32_t>(coop.profile_left),
        static_cast<uint32_t>(coop.profile_right),
    };
    if (out_words != nullptr) {
        for (uint32_t i = 0; i < word_capacity && i < sizeof(words) / sizeof(words[0]); ++i) {
            out_words[i] = words[i];
        }
    }
    return coop.known ? 1u : 0u;
}

/// Gamma, the three volumes and the speaker mode, as the guest's own calls state them
/// (Fable2/src/vfs/src/hooks/options_levels_stated.cpp): the profile's gamma getter and setter and
/// the audio manager's four level setters. Levels are 0..1, the speaker mode 0..2. Returns 1 once
/// all five are stated; no guest memory is read.
REFII_HOST_ABI_EXPORT int RefiiHostGetOptionsSliders(float* out_gamma, float* out_sfx,
                                                     float* out_music, float* out_speech,
                                                     uint32_t* out_speaker) {
    // Stated-value keys (payload_bridge.h).
    constexpr uint32_t kGamma = 7u, kSfx = 8u, kMusic = 9u, kSpeech = 10u, kSpeaker = 11u, kStated = 12u;
    const bool known = refii_bridge_stated_value_now(kStated) == 0x1Fu;
    const auto level = [known](uint32_t key) {
        const uint32_t raw = known ? refii_bridge_stated_value_now(key) : 0u;
        float value = 0.0f;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };
    if (out_gamma != nullptr) *out_gamma = level(kGamma);
    if (out_sfx != nullptr) *out_sfx = level(kSfx);
    if (out_music != nullptr) *out_music = level(kMusic);
    if (out_speech != nullptr) *out_speech = level(kSpeech);
    if (out_speaker != nullptr) *out_speaker = known ? refii_bridge_stated_value_now(kSpeaker) : 0u;
    return known ? 1 : 0;
}


/// The hero's COMPLETED QUESTS — one entry per row of the logbook's quests page.
///
/// Filtered on the guest thread (three byte tests on the quest object) and left UNSORTED. The
/// page's order is descending by the GDB property `DisplayPriority`; the guest sorts before
/// filtering, but filtering preserves relative order, so sorting afterwards is equivalent.
/// Returns the TOTAL count regardless of `capacity`.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCompletedQuests(uint32_t index, uint32_t* out_record,
                                                           int32_t* out_money,
                                                           int32_t* out_renown,
                                                           char* out_conclusion,
                                                           uint32_t conclusion_capacity) {
    static refii::vfs::QuestEntry entries[refii::vfs::kMaxCompletedQuests];
    const uint32_t total = refii::vfs::QuestSnapshot(entries, refii::vfs::kMaxCompletedQuests);

    if (index < total && index < refii::vfs::kMaxCompletedQuests) {
        const refii::vfs::QuestEntry& entry = entries[index];
        if (out_record != nullptr) *out_record = entry.record;
        if (out_money != nullptr) *out_money = entry.money;
        if (out_renown != nullptr) *out_renown = entry.renown;
        WriteOut(entry.conclusion, out_conclusion, conclusion_capacity);
    }

    return total;
}

/// The hero's DERIVED state — what the guest's own accessors answer, not what its fields hold.
/// `sub_8223D320` adds the GDB script constants `EvilMoralityAddition`/`HolyMoralityAddition`
/// to the raw field plus real-estate and worship terms; `sub_821C7D30` adds modifier 10 and
/// clamps. The logbook's personality bands test at 0.375 and 0.625 of the normalised range.
/// The pump samples the accessors on the guest tick thread; this only reads the shared store.
///
/// Fills 5 words and 26 floats:
///   words  0 known · 1 renown · 2 dog present · 3 dog state index · 4 (unused) ·
///          5 dog combat level · 6 dog searching level · 7 learned-trick count ·
///          8 stat counters valid
///   floats 0..5 morality/min/max, purity/min/max · 6..16 the eleven attractiveness terms
///          17 attractiveness slider fraction 0..1 · 20..25 spell damage multiplier by LEVEL
///
/// `stats` takes the 124 logbook counters and `trick_ids` the learned tricks' GDB RECORD IDS
/// (not name hashes — the guest stores what its hash-to-id lookup returned).
///
/// Returns 1 once the pump has sampled a hero at least once.
REFII_HOST_ABI_EXPORT int RefiiHostGetHeroDerived(uint32_t* words, uint32_t word_capacity,
                                                  float* values, uint32_t value_capacity,
                                                  char* title_tag, uint32_t title_capacity,
                                                  char* dog_name, uint32_t dog_capacity,
                                                  int32_t* stats, uint32_t stats_capacity,
                                                  uint32_t* trick_ids, uint32_t trick_capacity) {
    const refii::vfs::HeroDerived hero = refii::vfs::HeroDerivedSnapshot();

    const auto word = [words, word_capacity](uint32_t slot, uint32_t value) {
        if (words != nullptr && slot < word_capacity) words[slot] = value;
    };

    const auto real = [values, value_capacity](uint32_t slot, float value) {
        if (values != nullptr && slot < value_capacity) values[slot] = value;
    };

    word(0, hero.known ? 1u : 0u);
    word(1, static_cast<uint32_t>(hero.renown));
    word(2, hero.dog_known ? 1u : 0u);
    word(3, static_cast<uint32_t>(hero.dog_state));
    word(4, 0u);  // was a trick bitmask; the ids go across in their own buffer now

    real(0, hero.morality);
    real(1, hero.morality_min);
    real(2, hero.morality_max);
    real(3, hero.purity);
    real(4, hero.purity_min);
    real(5, hero.purity_max);

    for (uint32_t i = 0; i < 11u; ++i) {
        real(6u + i, hero.attractiveness[i]);
    }

    real(17, hero.attractiveness_fraction);
    // int32 levels 1..5, published as words; reading them as floats gives denormal garbage.
    word(5, static_cast<uint32_t>(hero.dog_combat));
    word(6, static_cast<uint32_t>(hero.dog_searching));
    word(7, hero.dog_trick_count);
    word(8, hero.stats_known ? 1u : 0u);
    word(9, hero.dog_name_key);
    word(10, hero.dog_definition);
    word(11, hero.dog_entity);

    for (uint32_t i = 0; i < 6u; ++i) {
        real(20u + i, hero.spell_damage[i]);
    }

    // Fixed-size char arrays in the store, already NUL-terminated by the sampler.
    if (stats != nullptr) {
        const uint32_t count =
            stats_capacity < refii::vfs::HeroDerived::kStatCount
                ? stats_capacity
                : refii::vfs::HeroDerived::kStatCount;
        for (uint32_t i = 0; i < count; ++i) {
            stats[i] = hero.stats[i];
        }
    }

    if (trick_ids != nullptr) {
        const uint32_t count = trick_capacity < hero.dog_trick_count ? trick_capacity
                                                                    : hero.dog_trick_count;
        for (uint32_t i = 0; i < count; ++i) {
            trick_ids[i] = hero.dog_trick_ids[i];
        }
    }

    WriteOut(hero.title_tag, title_tag, title_capacity);
    WriteOut(hero.dog_name, dog_name, dog_capacity);
    return hero.known ? 1 : 0;
}

/// The hero's VITALS, as pure reads: the component data the C++ screen managers spawn their
/// rows from. Every field is a single aligned load in the guest, which is what makes it safe
/// to read from Godot's thread. From the accessors' own bytes:
///
///     GetHealth      0x829495D0  C0 23 00 14  lfs f1, 0x14(r3) ; blr
///     GetBaseHealth  0x828BF918  C0 23 00 18  lfs f1, 0x18(r3) ; blr
///     GetMaxHealth   0x82314BD0  C0 23 00 1C  lfs f1, 0x1C(r3) ; blr
///     GetMoney       0x82EB55A8  80 63 00 0C  lwz r3,  0x0C(r3) ; blr
///     Invulnerable   0x824CA750  88 63 01 31  lbz r3, 0x131(r3) ; blr
///     CantDie        0x824CA758  88 63 01 33  lbz r3, 0x133(r3) ; blr
///
/// `CECHealth+0x10` is NOT health: it is an incoming-damage multiplier initialised to 1.0.
/// Health starts at `+0x14`.
///
/// Three values are not answered here — they can only be CALLED:
///
///   * Displayed Morality, Purity and Renown: the stored fields are BASE, and the accessors add
///     real-estate and worship bonuses from the maps at `stats+0x84`/`+0x9C` (bonus types 11,
///     12 and 10) plus an Evil/Holy term from a global config string.
///   * Ability levels for ids 2, 3 and 6, which take buffed arms and clamp against a game-defs
///     ceiling. The raw slots are reported and flagged; only the displayed NUMBER is wrong.
///
/// Those accessors' map walks carry MSVC iterator-debug checks that TRAP (`twui r0, 0x16`) if
/// another thread mutates the map, so anything derived stays a thread-constrained call.
///
/// Layout. <paramref name="words"/> takes 17:
///
///     [0]      gold                    CECMoney+0x0C, i32
///     [1]      renown, BASE            CECStats+0x1C, i32
///     [2]      flags: bit0 invulnerable, bit1 cantDie
///     [3..16]  ability levels for ids 1..14, RAW (`stats+0x110 + 4*id`)
///
/// <paramref name="values"/> takes 20:
///
///     [0..2]   health, baseHealth, maxHealth
///     [3..4]   morality, purity — BASE
///     [5..8]   moralityMin, moralityMax, purityMin, purityMax
///     [9..11]  strength, skill, will — BODY MORPH percentages in [0,1], NOT experience
///     [12..15] experience AVAILABLE  per pool: General, Strength, Skill, Will
///     [16..19] experience TOTAL EARNED per pool, same order
///
/// Spent is `total - available`; the guest keeps no spent counter. Returns the hero entity, or
/// 0 when there is not one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroVitals(uint32_t* words, uint32_t word_capacity,
                                                     float* values, uint32_t value_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (hero == 0u) {
        return 0;
    }

    const auto word = [&](uint32_t index, uint32_t value) {
        if (words != nullptr && index < word_capacity) {
            words[index] = value;
        }
    };
    const auto real = [&](uint32_t index, float value) {
        if (values != nullptr && index < value_capacity) {
            values[index] = value;
        }
    };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float out = 0.0f;
        std::memcpy(&out, &bits, sizeof(out));
        return out;
    };

    for (uint32_t i = 0; i < 17u; ++i) word(i, 0u);
    for (uint32_t i = 0; i < 20u; ++i) real(i, 0.0f);

    // Component ids are the ones each component's own registration stores beside its name —
    // CECInventory 13, CECHealth 36, CECStats 57, CECMoney 81, CECExperience 164.
    if (const uint32_t money = EntityComponent(base, hero, 81u)) {
        word(0, GuestWordAt(base, money + 0x0Cu));
    }

    if (const uint32_t health = EntityComponent(base, hero, 36u)) {
        real(0, read_float(health + 0x14u));
        real(1, read_float(health + 0x18u));
        real(2, read_float(health + 0x1Cu));

        uint32_t flags = 0u;
        if (GuestByteAt(base, health + 0x131u) != 0u) flags |= 1u;
        if (GuestByteAt(base, health + 0x133u) != 0u) flags |= 2u;
        word(2, flags);
    }

    if (const uint32_t stats = EntityComponent(base, hero, 57u)) {
        word(1, GuestWordAt(base, stats + 0x1Cu));

        real(3, read_float(stats + 0x10u));
        real(4, read_float(stats + 0x24u));
        real(5, read_float(stats + 0x14u));
        real(6, read_float(stats + 0x18u));
        real(7, read_float(stats + 0x28u));
        real(8, read_float(stats + 0x2Cu));

        real(9, read_float(stats + 0x40u));
        real(10, read_float(stats + 0x44u));
        real(11, read_float(stats + 0x48u));

        // Ids 1..14; slot 0 is never a hero ability, and the guest's own loop skips it.
        for (uint32_t id = 1u; id <= 14u; ++id) {
            word(2u + id, GuestWordAt(base, stats + 0x110u + 4u * id));
        }
    }

    // Two heap arrays, four floats each, through the vector headers based at +0x0C and +0x1C,
    // so the DATA pointers are at +0x10 and +0x20. Pools: General, Strength, Skill, Will.
    if (const uint32_t experience = EntityComponent(base, hero, 164u)) {
        const uint32_t available = GuestWordAt(base, experience + 0x10u);
        const uint32_t earned = GuestWordAt(base, experience + 0x20u);
        const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

        for (uint32_t pool = 0; pool < 4u; ++pool) {
            if (plausible(available)) {
                real(12u + pool, read_float(available + 4u * pool));
            }
            if (plausible(earned)) {
                real(16u + pool, read_float(earned + 4u * pool));
            }
        }
    }

    return hero;
}

/// The hero's INVENTORY — what the items, weapons, clothing, gift and shop screens are lists OF.
///
/// An MS-STL `std::list`, not an array, and `+0x14` is not the item count. `+0x10` is
/// `_Myhead`, a POINTER to a heap-allocated 16-byte self-linked sentinel node; `+0x14` is
/// `_Mysize`, the number of RECORDS, while the number of ITEMS is the sum of every record's
/// `Count`. From `GetNumberOfItems` (`sub_824F23D0`):
///
///     824f23d0  addi  r9, r3, 0xC      ; the list header is based at inv+0x0C
///     824f23d4  lwz   r8, 0x10(r3)     ; sentinel = *(inv+0x10)
///     824f23dc  lwz   r11, 4(r9)       ; == the same word
///     824f23e0  lwz   r11, 0(r11)      ; node = node->next
///     824f23f0  cmplw cr6, r11, r8
///     824f23f4  beqlr cr6              ; stop AT THE SENTINEL
///     824f2408  lwz   r7, 8(r11)       ; the item hangs at node+8
///     824f2410  lwz   r10, 0xC(r7)     ; Count
///     824f2414  add   r3, r10, r3      ; ...summed
///
/// (The `twui r0, 0x16` between those lines are MSVC iterator-debug traps; this walk bounds
/// its own hops instead.)
///
/// SERVED FROM THE PUMP'S SNAPSHOT, walked on the guest's own thread (SampleHeroInventory in
/// guest_tick_pump.cpp) every 15 ticks and on the tick after a UI change. Walked from here, on
/// Godot's thread, the list was read while the guest patched it — `_Mysize` is bumped before
/// the links and an erase frees its node the moment a `Count` reaches zero — and a torn read
/// was a list with a row missing or doubled.
///
/// A RecordID is NOT unique across records: two share one id whenever the first still holds
/// instance data. Callers must group, not assume.
///
/// Five words per record — {recordId, count, category, flags, item} — where `recordId` is the
/// FNV-1 GDB key, `category` is −1 when the item states none, `flags` bit 0 is `Viewed` (the
/// new-item marker) and `item` is the ITEM INSTANCE the inventory list node holds (`node+8`), the
/// key the dye layer export uses (RefiiHostGetItemDyeLayers). Two records of one id are two
/// instances, dyed apart. `item` is 0 when the pump published without instances. Returns the
/// number of records in the snapshot, which may exceed what fitted.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroInventoryItems(uint32_t* words, uint32_t word_capacity) {
    return refii_bridge_hero_inventory_items_snapshot(words, word_capacity);
}

/// PLAYER 1's OR 2's INVENTORY, whoever's menus are open — what a mod reads for a named player
/// (`RefiiHost.inventory_items`). Served from the pump's snapshot (SamplePlayerInventories, walked
/// on the guest's thread beside the hero's) of the hero `refii_mod_player_hero` resolves, the
/// entity manager's player slots the guest itself reads. Four words per record — {recordId, count,
/// category, flags} — as RefiiHostGetHeroInventoryItems' first four, with flags bit 1 set when the hero WEARS that
/// record (component 15's worn vector). Returns the number of records, 0 for any other player.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPlayerInventory(int32_t player, uint32_t* words,
                                                           uint32_t word_capacity) {
    return refii_bridge_player_inventory_snapshot(player, words, word_capacity);
}

/// The hero's QUEST LOG — three words each: {recordId, flags, 0}.
///
/// `flags` bit 0 Unlocked, 1 Completed, 2 Failed, 3 AppearOnWorldMap, 4 Viewed. Read for quest
/// markers: the world map's minimap page and the minimap mod; the world map's Quests and Jobs rows
/// are the guest's own population, not this.
///
/// THE CONTAINER, verified in disassembly (`sub_8251CA50`):
///
///     lwz r31, 0x10(r30)   begin
///     lwz r11, 0x14(r30)   end
///     lwz r11, 4(r31)      the Quest*   -- element + 4
///     addi r31, r31, 0xC   stride       -- 12 bytes per entry
///
/// It is a sorted flat map: element+0 is FNV-1 of the quest name, element+4 the `Quest*`,
/// element+8 unread by anything. The guest SORTS its own rows by the GDB `DisplayPriority` of each
/// quest afterwards (`sub_82529258` with comparator `sub_8251C9C0`), so container order is not
/// display order.
///
/// The quest's own fields, from the serializer `sub_825237C8` which names them: `+4` RecordID (the
/// GDB record hash — `sub_82522CB0` resolves it and reads `NameTag` off it), `+12` Unlocked, `+21`
/// Completed, `+22` Failed, `+77` AppearOnWorldMap, `+78` Viewed.
///
/// A PLAIN VECTOR WITH NO LOCK: accepting or dropping a quest reallocates it. Same treatment as
/// the worn vector — the bounds are re-read afterwards and a walk that saw them move is discarded.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetQuestLog(uint32_t* words, uint32_t word_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (hero == 0u) {
        return 0;
    }

    // 102 — QuestLog.
    const uint32_t log = EntityComponent(base, hero, 102u);
    if (log == 0u) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t begin = GuestWordAt(base, log + 0x10u);
    const uint32_t end = GuestWordAt(base, log + 0x14u);
    if (begin == 0u && end == 0u) {
        return 0;  // an empty log, not a failure
    }

    if (!plausible(begin) || !plausible(end) || end < begin) {
        return 0;
    }

    constexpr uint32_t kStride = 12u;
    constexpr uint32_t kMaxQuests = 512u;

    // EIGHT WORDS, not three. The world map's quest and job pages show a great deal more than
    // "does this quest exist": the star rating, both rewards, the objective's `%1` parameter
    // and the days remaining are all plain fields on the quest object, and the pages are blank
    // without them. Every offset is from the save serialiser `sub_825237C8`, which names them.
    // NINE since ABI 166: word 8 is the quest's OBJECTIVE ENTITY (`sub_82523500`: the weak
    // reference at +84, holder at +88, entity at the holder's +0), which the world map's
    // minimap page plots as an "other quest" marker (`sub_829ABFC0`, `minimap3`).
    constexpr uint32_t kWordsPerQuest = 9u;

    const uint32_t span = end - begin;
    if ((span % kStride) != 0u || (span / kStride) > kMaxQuests) {
        return 0;
    }

    uint32_t found = 0;
    for (uint32_t at = begin; at < end; at += kStride) {
        const uint32_t quest = GuestWordAt(base, at + 4u);
        if (!plausible(quest)) {
            continue;
        }

        if (words != nullptr && (found * kWordsPerQuest + 8u) < word_capacity) {
            uint32_t flags = 0u;
            flags |= GuestByteAt(base, quest + 12u) != 0u ? 1u : 0u;
            flags |= GuestByteAt(base, quest + 21u) != 0u ? 2u : 0u;
            flags |= GuestByteAt(base, quest + 22u) != 0u ? 4u : 0u;
            flags |= GuestByteAt(base, quest + 77u) != 0u ? 8u : 0u;
            flags |= GuestByteAt(base, quest + 78u) != 0u ? 16u : 0u;

            // `Primary` and `Active` pick the banner the quest page opens with.
            flags |= GuestByteAt(base, quest + 20u) != 0u ? 32u : 0u;
            flags |= GuestByteAt(base, quest + 13u) != 0u ? 64u : 0u;

            uint32_t* row = words + found * kWordsPerQuest;
            row[0] = GuestWordAt(base, quest + 4u);
            row[1] = flags;
            row[2] = GuestWordAt(base, quest + 28u);   // Rating — the star strip's slide
            row[3] = GuestWordAt(base, quest + 32u);   // RewardRenown
            row[4] = GuestWordAt(base, quest + 36u);   // RewardMoney
            row[5] = GuestWordAt(base, quest + 40u);   // CurrentObjectiveParameter, the `%1`
            row[6] = GuestWordAt(base, quest + 44u);   // DaysRemaining, raw float bits

            {
                const uint32_t holder = GuestWordAt(base, quest + 88u);
                const uint32_t target = plausible(holder) ? GuestWordAt(base, holder) : 0u;
                row[8] = plausible(target) ? target : 0u;
            }

            // How many task lines it has. The vector at +104/+108 is stride 8,
            // `{ LHString babelTag; u8 Succeeded; }`; the LAST entry is the current task and
            // everything before it is a completed one.
            const uint32_t tasks_begin = GuestWordAt(base, quest + 104u);
            const uint32_t tasks_end = GuestWordAt(base, quest + 108u);
            row[7] = plausible(tasks_begin) && tasks_end > tasks_begin &&
                             ((tasks_end - tasks_begin) % 8u) == 0u &&
                             (tasks_end - tasks_begin) <= 0x400u
                         ? (tasks_end - tasks_begin) / 8u
                         : 0u;
        }

        ++found;
    }

    if (GuestWordAt(base, log + 0x10u) != begin || GuestWordAt(base, log + 0x14u) != end) {
        return 0;
    }

    return found;
}

/// WHAT THE WORLD MAP'S MINIMAP PAGE PLOTS FROM LIVE STATE — `sub_829ABFC0`'s inputs that are
/// not GDB: the current level's key and bounds, the dog, the primary quest's trail target, and
/// whether the primary quest is in this level.
///
///   [0]  current level key, `L+180` — the page plots the hero only when it equals the selected
///        region's key (`World_ComputeWorldLevelFnv1Key(World, "Albion", &firstLevel)`).
///   [1..4] `L+152, +156, +160, +164` — signed level bounds in metres (minX, minY, maxX, maxY);
///        the page's extent is `max(maxX − minX, maxY − minY)` and the projection divides the
///        RAW world position by it (0x829AEAE4..: `n = pos / extent; m = 240·(n − 0.5)`).
///   [5]  the dog entity: `sub_82363D38(roster)` = the weak reference at roster+192 (holder at
///        +196, entity at the holder's +0), drawn when its `+144 & 0x40` is set (`minimap11`).
///   [6]  1 when [7..9] hold a position: the trail target of the primary quest — the entity the
///        hero's component 33 weak-references at +316 (holder +320), its component 162, the
///        float4 at +32 (`sub_82551D40`). Plotted with `minimap1` when the primary quest is in
///        this level, `minimap17` (the exit) otherwise; the guest plots the same point for both.
///   [10] 1 when the primary quest's level name (`quest+132`) equals `L+172`, case-insensitive
///        (the `strcmpi` at 0x829AC...), 0 otherwise or when no quest is primary.
///   [11] the hero entity.
///   [12] how many TRAVELLING MERCHANTS follow, then their entities (`minimap10`): the page's
///        `sub_823235A0(mgr, &27, 2, out)` — list 2 of the entity manager (`*(*(party+12))+124`,
///        then `+ 12·2`: an intrusive list, node `{next, _, entity}`), each entity alive
///        (`+144 & 0x40`), of KIND 2 (`sub_822035E0(e, 2)`: the object at `e+120`, vtable slot
///        16 returns 2) and carrying component 27 (`sub_8250FD20`: bit 27 of the set at `e+36`),
///        then `sub_828738D8(comp27)`: the weak reference at comp27+16 (holder +20) alive, its
///        `+144 & 0x40` set and bit 26 of its `+36` set. The kind test is read, not called: the
///        slot's function is the two-word stub `li r3, 2; blr`.
///
/// `L = *(*(World+36)+4)`, `World = *(*(*(*(*(0x83496AB8+12)+88)+4)+8)+4)`, roster =
/// `*(*(*(*(0x83496AB8+12)+88)+4))`. Returns 1 when the level resolved; every other word is 0
/// when its chain did not.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMinimapLive(uint32_t* out, uint32_t capacity) {
    constexpr uint32_t kWords = 13u;
    constexpr uint32_t kMaxMerchants = 32u;
    if (out == nullptr || capacity < kWords) {
        return 0u;
    }
    for (uint32_t i = 0; i < kWords; ++i) {
        out[i] = 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? load_be32(root + 12u) : 0u;
    const uint32_t holder = plausible(game) ? load_be32(game + 88u) : 0u;
    const uint32_t party = plausible(holder) ? load_be32(holder + 4u) : 0u;
    const uint32_t h = plausible(party) ? load_be32(party + 8u) : 0u;
    const uint32_t world = plausible(h) ? load_be32(h + 4u) : 0u;
    const uint32_t level_handle = plausible(world) ? load_be32(world + 36u) : 0u;
    const uint32_t level = plausible(level_handle) ? load_be32(level_handle + 4u) : 0u;
    if (!plausible(level)) {
        return 0u;
    }

    out[0] = load_be32(level + 180u);
    out[1] = load_be32(level + 152u);
    out[2] = load_be32(level + 156u);
    out[3] = load_be32(level + 160u);
    out[4] = load_be32(level + 164u);

    // The LOCAL player's dog, as the pump's dog sample found it: hero one's (players+196) or,
    // on a joiner, player two's own co-op dog.
    {
        const uint32_t dog = refii::vfs::HeroDerivedSnapshot().dog_entity;
        if (plausible(dog) && (GuestByteAt(base, dog + 144u) & 0x40u) != 0u) {
            out[5] = dog;
        }
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (!plausible(hero)) {
        return 1u;
    }
    out[11] = hero;

    const uint32_t comp33 = EntityComponent(base, hero, 33u);
    if (plausible(comp33)) {
        const uint32_t target_holder = load_be32(comp33 + 320u);
        const uint32_t target = plausible(target_holder) ? load_be32(target_holder) : 0u;
        const uint32_t comp162 = plausible(target) ? EntityComponent(base, target, 162u) : 0u;
        if (plausible(comp162)) {
            uint32_t bits[3];
            bool finite = true;
            for (uint32_t i = 0; i < 3u; ++i) {
                bits[i] = load_be32(comp162 + 32u + (4u * i));
                float f = 0.0f;
                std::memcpy(&f, &bits[i], sizeof(f));
                finite = finite && std::isfinite(f);
            }
            if (finite) {
                out[6] = 1u;
                out[7] = bits[0];
                out[8] = bits[1];
                out[9] = bits[2];
            }
        }
    }

    // The travelling merchants.
    {
        const uint32_t manager = plausible(party) ? party + 12u : 0u;
        const uint32_t inner = plausible(manager) ? load_be32(manager) : 0u;
        const uint32_t table = plausible(inner) ? load_be32(inner + 124u) : 0u;
        const uint32_t list = plausible(table) ? table + 24u : 0u;
        uint32_t node = plausible(list) ? load_be32(list) : 0u;
        const uint32_t tail = plausible(list) ? load_be32(list + 4u) : 0u;
        uint32_t count = 0u;
        for (uint32_t guard = 0; guard < 4096u && plausible(node) && count < kMaxMerchants; ++guard) {
            const uint32_t entity = load_be32(node + 8u);
            if (plausible(entity) && (GuestByteAt(base, entity + 144u) & 0x40u) != 0u) {
                // Component 27 present: bit 27 of the set at +36 (`sub_8250FD20` with &27).
                const bool has27 = (load_be32(entity + 36u) & (1u << 27)) != 0u;

                // Kind 2: `*(e+120)` -> vtable slot 16 -> `li r3, 2; blr`.
                bool kind2 = false;
                const uint32_t kind = load_be32(entity + 120u);
                const uint32_t kind_vtable = plausible(kind) ? load_be32(kind) : 0u;
                const uint32_t kind_fn = plausible(kind_vtable) ? load_be32(kind_vtable + 64u) : 0u;
                if (kind_fn >= refii::guest::Address(0x82000000u) && kind_fn < 0x83000000u) {
                    kind2 = load_be32(kind_fn) == 0x38600002u && load_be32(kind_fn + 4u) == 0x4E800020u;
                }

                if (has27 && kind2) {
                    const uint32_t comp27 = EntityComponent(base, entity, 27u);
                    const uint32_t owner_holder = plausible(comp27) ? load_be32(comp27 + 20u) : 0u;
                    const uint32_t owner = plausible(owner_holder) ? load_be32(owner_holder) : 0u;
                    if (plausible(owner) && (GuestByteAt(base, owner + 144u) & 0x40u) != 0u &&
                        (load_be32(owner + 36u) & (1u << 26)) != 0u &&
                        (kWords + count) < capacity) {
                        out[kWords + count] = entity;
                        ++count;
                    }
                }
            }

            if (node == tail) {
                break;
            }
            node = load_be32(node);
        }
        out[12] = count;
    }

    const uint32_t log = EntityComponent(base, hero, 102u);
    if (plausible(log)) {
        const uint32_t begin = load_be32(log + 16u);
        const uint32_t end = load_be32(log + 20u);
        if (plausible(begin) && end > begin && (end - begin) <= 0x10000u) {
            for (uint32_t at = begin; at + 12u <= end; at += 12u) {
                const uint32_t quest = load_be32(at + 4u);
                if (!plausible(quest) || GuestByteAt(base, quest + 20u) == 0u) {
                    continue;
                }
                const std::string quest_level = ReadGuestString(base, quest + 132u, 128u);
                const std::string here = ReadGuestString(base, level + 172u, 128u);
                if (!quest_level.empty() && quest_level.size() == here.size()) {
                    bool same = true;
                    for (std::size_t i = 0; i < here.size(); ++i) {
                        if (std::tolower(static_cast<unsigned char>(quest_level[i])) !=
                            std::tolower(static_cast<unsigned char>(here[i]))) {
                            same = false;
                            break;
                        }
                    }
                    out[10] = same ? 1u : 0u;
                }
                break;
            }
        }
    }

    return 1u;
}

/// One quest's task lines' COMPLETED bytes, in the order of the task vector: the
/// 8-byte entry at `quest+104..+108` is {+0 tag, +4 completed}, set by the Lua native
/// "SetObjectiveAsCompleted" (`sub_8251E400`). The pause root's objective line (`sub_82522E48`)
/// is the LAST entry whose byte is clear. Returns the entries written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetQuestTaskStates(uint32_t record_id, uint8_t* out,
                                                           uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out == nullptr || capacity == 0u) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t log = hero != 0u ? EntityComponent(base, hero, 102u) : 0u;
    if (log == 0u) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t begin = GuestWordAt(base, log + 0x10u);
    const uint32_t end = GuestWordAt(base, log + 0x14u);
    if (!plausible(begin) || !plausible(end) || end < begin || ((end - begin) % 12u) != 0u) {
        return 0;
    }

    for (uint32_t at = begin; at < end; at += 12u) {
        const uint32_t quest = GuestWordAt(base, at + 4u);
        if (!plausible(quest) || GuestWordAt(base, quest + 4u) != record_id) {
            continue;
        }

        const uint32_t tasks = GuestWordAt(base, quest + 104u);
        const uint32_t tasks_end = GuestWordAt(base, quest + 108u);
        if (!plausible(tasks) || tasks_end <= tasks || ((tasks_end - tasks) % 8u) != 0u ||
            (tasks_end - tasks) > 0x400u) {
            return 0;
        }

        uint32_t count = 0;
        for (uint32_t entry = tasks; entry < tasks_end && count < capacity; entry += 8u) {
            out[count++] = base[entry + 4u] != 0u ? 1u : 0u;
        }

        return count;
    }

    return 0;
}


/// True while a menu population is in flight and the captured rows are not yet whole.
///
/// A consumer that rebuilds its model from the capture must hold its last whole population
/// while this is set, or the screen blanks between `DeleteAllSpawnedMenuItems` and the last
/// `SpawnButton`.
REFII_HOST_ABI_EXPORT int RefiiHostMenuPopulating(void) {
    return refii_bridge_menu_populating();
}

/// Drains the `g_Menu:` chunk calls captured since the last drain.
///
/// Each record is a chunk plus what was staged for it. The encoding is flat so a whole batch
/// crosses in two buffers: `out_utf8` receives NUL-separated fields, `out_kinds` one byte per
/// field. Per record:
///
///     chunk \0 name0 \0 value0 \0 name1 \0 value1 \0 ...
///
/// with `out_kinds` holding the record's ATTRIBUTE COUNT followed by one kind byte per attribute
/// (0 = string, 1 = number). Numbers arrive as their decimal text.
///
/// The presentation is part of the identity and is written as the record's first field, before
/// the chunk, in hex: `menu/menuemphasisandghosting` defines a SECOND `SetItemEmphasis` and
/// `SetItemGhosting` for the non-expandable menu with different semantics (`Float1` as a
/// boolean rather than the 0..3 enum, the field named `Ghosting` rather than `Ghosted`), and
/// which one runs depends on the presentation the chunk was dispatched to.
///
/// Returns the number of records written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeMenuChunks(char* out_utf8, uint32_t utf8_capacity,
                                                       uint8_t* out_kinds, uint32_t kinds_capacity,
                                                       uint32_t max_records) {
    if (out_utf8 == nullptr || out_kinds == nullptr || utf8_capacity == 0 || kinds_capacity == 0) {
        return 0;
    }

    std::vector<refii::vfs::MenuChunkCall> calls = refii::vfs::TakeMenuChunkCalls();
    uint32_t written = 0;
    uint32_t text_at = 0;
    uint32_t kind_at = 0;

    const auto put = [&](const std::string& field) {
        if (text_at + field.size() + 1u > utf8_capacity) {
            return false;
        }
        std::memcpy(out_utf8 + text_at, field.data(), field.size());
        text_at += static_cast<uint32_t>(field.size());
        out_utf8[text_at++] = '\0';
        return true;
    };

    // The first chunk NOT handed over; it and everything after go back to the front of the queue.
    std::size_t handed = 0;
    for (const refii::vfs::MenuChunkCall& call : calls) {
        if (written >= max_records || kind_at + 1u + call.staged.size() > kinds_capacity) {
            break;
        }

        const uint32_t text_mark = text_at;
        const uint32_t kind_mark = kind_at;
        bool ok = put(fmt::format("{:08X}", call.presentation)) && put(call.chunk);
        out_kinds[kind_at++] = static_cast<uint8_t>(call.staged.size());

        for (const refii::vfs::MenuChunkAttribute& attribute : call.staged) {
            if (!ok) break;
            out_kinds[kind_at++] = attribute.is_number ? 1u : 0u;
            ok = put(attribute.name) &&
                 put(attribute.is_number ? fmt::format("{}", attribute.number) : attribute.text);
        }

        if (!ok) {
            // A record is all or nothing: half a chunk attaches the wrong attributes to a
            // name. Roll back and stop.
            text_at = text_mark;
            kind_at = kind_mark;
            break;
        }

        ++written;
        ++handed;
    }

    // Nothing is dropped: the caller drains again while chunks wait and grows its buffers for a
    // first record that fits none; only one past the ceiling is dropped, by name.
    if (handed < calls.size() && written == 0u &&
        (utf8_capacity >= refii::vfs::kDrainTextCeiling || kinds_capacity >= refii::vfs::kDrainKindCeiling)) {
        REXLOG_WARN("Refii menu chunks: chunk '{}' ({} attribute(s)) fits no drain buffer ({} text bytes, {} kind bytes) — dropped",
                    calls[handed].chunk, calls[handed].staged.size(), utf8_capacity, kinds_capacity);
        ++handed;
    }
    refii::vfs::PutBackMenuChunkCalls(calls, handed);

    return written;
}

/// Recorded, pending and DROPPED menu-chunk counts. The queue has a ceiling, and a truncated
/// population is a menu silently missing rows.
REFII_HOST_ABI_EXPORT void RefiiHostMenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending,
                                                      uint64_t* out_dropped) {
    refii::vfs::MenuChunkCounters(out_recorded, out_pending, out_dropped);
}

/// The front end's own storage criteria, so a host-drawn menu gates on what the guest gates on.
///
/// THREE NUMBERS, AND THEY COME FROM DIFFERENT PLACES ON PURPOSE.
///
/// `signed_in` is `(1 << *0x83496AF8) & *0x83496A70` — the active XAM user index against the
/// signed-in bitmask that `sub_8230AEE8` rebuilds on notification 0xA. Pure memory, so it is
/// read here rather than posted.
///
/// `limit` is `dword_83319620`. The image holds 150; `sub_82378618` — the front-end enter,
/// vtable slot 0x820A9260 — unconditionally stores 6 over it before the menu can dispatch, and
/// our post-hook on it (frontend_stated.cpp) raises that to 10. So
/// this is read live and is only meaningful once the front end has entered, which is the same
/// moment the count below becomes meaningful.
///
/// `count` CANNOT be read. It is the return value of `sub_82443308`, a guest call, so it comes
/// from the tick pump's sample — see SampleSavedGameCount in guest_tick_pump.cpp. -1 means the
/// pump has not taken one yet, and a caller must not read that as "no saves": the front-end
/// menu is drawn from this, and answering 0 too early is how Load Game goes missing on a
/// profile that has six saves.
///
/// These are the exact inputs to the guest's two decisions. Load Game spawns when
/// `signed_in && count > 0` (`sub_826E2220` at 0x826E2264..0x826E2278); New Game is refused
/// with GUI_REACHED_SAVE_GAME_LIMIT_WARNING when `count >= limit` (0x826E0148).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveState(uint32_t* out_signed_in, int32_t* out_count,
                               uint32_t* out_limit) {
    if (out_signed_in != nullptr) {
        *out_signed_in = 0;
    }
    if (out_count != nullptr) {
        *out_count = -1;
    }
    if (out_limit != nullptr) {
        *out_limit = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    const uint32_t kActiveUserIndex = refii::guest::Address(0x83496AF8u);
    const uint32_t kSignedInMask = refii::guest::Address(0x83496A70u);
    const uint32_t kSaveSlotLimit = refii::guest::Address(0x83319620u);

    const uint32_t user = read32(kActiveUserIndex);
    const uint32_t mask = read32(kSignedInMask);

    if (out_signed_in != nullptr) {
        // Guarded shift: the index is a 0..3 XAM user index, but it is guest memory and a
        // shift by 32 or more is undefined rather than merely wrong.
        *out_signed_in = (user < 32u && ((mask >> user) & 1u) != 0u) ? 1u : 0u;
    }
    if (out_count != nullptr) {
        *out_count = refii::vfs::GuestSavedGameCount();
    }
    if (out_limit != nullptr) {
        *out_limit = read32(kSaveSlotLimit);
    }
    return 1;
}

/// Asks the pump to re-take the saved-game count on its next front-end tick.
REFII_HOST_ABI_EXPORT void RefiiHostRefreshSaveState() {
    refii::vfs::RefreshGuestSavedGameCount();
}

/// The guest's installed-language table, so a host drawing the language menu draws the real one.
///
/// Base `dword_83319930`, stride 96, count `dword_83319934`, current index `dword_83319938`.
/// Within a row, `+0` is the ASCII locale code ("en-uk") and `+88` is a `WCHAR*` DISPLAY NAME.
/// The NAME is what the guest spawns its menu entry with — `sub_826E1C98` hands it straight to
/// the spawner at 0x826E1E88 and the front end resolves it through Babel afterwards — so that
/// is the string a host wants, not the locale code.
///
/// Names are written to `names` as NUL-separated UTF-8 in table order, so the caller splits on
/// the terminators. Returns the number of entries reported, or 0 when the table is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLanguages(uint32_t* out_current, char* names, uint32_t capacity) {
    if (out_current != nullptr) {
        *out_current = 0;
    }
    if (names != nullptr && capacity > 0) {
        names[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t kLanguageTable = refii::guest::Address(0x83319930u);
    const uint32_t kLanguageCount = refii::guest::Address(0x83319934u);
    const uint32_t kLanguageCurrent = refii::guest::Address(0x83319938u);
    constexpr uint32_t kLanguageStride = 96u;
    constexpr uint32_t kLanguageNameOffset = 88u;

    const uint32_t table = read32(kLanguageTable);
    const uint32_t count = read32(kLanguageCount);
    if (!plausible(table) || count == 0 || count > 64u) {
        return 0;
    }

    if (out_current != nullptr) {
        *out_current = read32(kLanguageCurrent);
    }

    uint32_t written = 0;
    uint32_t reported = 0;
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t wide = read32(table + (i * kLanguageStride) + kLanguageNameOffset);
        ++reported;

        if (names == nullptr || capacity == 0) {
            continue;
        }

        // UTF-16 BE in guest memory, encoded out as UTF-8. Only the basic plane is handled:
        // a surrogate pair would be a language name outside it, which none of the shipped
        // locales are, and emitting a lone surrogate is worse than stopping at it.
        if (plausible(wide)) {
            for (uint32_t c = 0; c < 128u; ++c) {
                const uint16_t unit = GuestHalfAt(base, wide + (c * 2u));
                if (unit == 0) {
                    break;
                }

                char encoded[3];
                uint32_t length = 0;
                if (unit < 0x80u) {
                    encoded[0] = static_cast<char>(unit);
                    length = 1;
                } else if (unit < 0x800u) {
                    encoded[0] = static_cast<char>(0xC0u | (unit >> 6));
                    encoded[1] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    length = 2;
                } else {
                    encoded[0] = static_cast<char>(0xE0u | (unit >> 12));
                    encoded[1] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
                    encoded[2] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    length = 3;
                }

                if (written + length + 1u >= capacity) {
                    break;
                }

                for (uint32_t b = 0; b < length; ++b) {
                    names[written++] = encoded[b];
                }
            }
        }

        if (written + 1u < capacity) {
            names[written++] = '\0';
        }
    }

    if (names != nullptr && written < capacity) {
        names[written] = '\0';
    }

    return reported;
}

/// The locale code of the language the guest runs in: `+0` of the CURRENT row of the
/// installed-language table (base `dword_83319930`, stride 96, current `dword_83319938`).
///
/// That code is what the guest switches by — `sub_82382C68` copies `96 * index + table` into
/// the settings block it hands XLip (`strcpy_s` at 0x82382CC8, then `sub_82BF6F18`) — so it
/// names the culture the guest's language roots (text, fonts, speechdoc) resolve under.
///
/// A switch frees and rebuilds the table on the tick thread (`sub_823826D8`: free at
/// 0x8238276C, reallocation at 0x82382810, rows filled after), so the read is taken twice and
/// kept only when both agree. Writes a NUL-terminated ASCII code and returns its length; 0
/// when the table is not up or the code is not a plain culture name.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentCulture(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }
    out[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t kLanguageTable = refii::guest::Address(0x83319930u);
    const uint32_t kLanguageCount = refii::guest::Address(0x83319934u);
    const uint32_t kLanguageCurrent = refii::guest::Address(0x83319938u);
    constexpr uint32_t kLanguageStride = 96u;
    constexpr uint32_t kCodeCapacity = 16u;

    auto read_code = [&](char (&code)[kCodeCapacity + 1]) -> uint32_t {
        const uint32_t table = GuestWordAt(base, kLanguageTable);
        const uint32_t count = GuestWordAt(base, kLanguageCount);
        const uint32_t current = GuestWordAt(base, kLanguageCurrent);
        if (table < 0x10000u || table >= 0xC0000000u || count == 0 || count > 64u ||
            current >= count) {
            return 0;
        }

        const uint32_t row = table + (current * kLanguageStride);
        for (uint32_t i = 0; i < kCodeCapacity; ++i) {
            const char c = static_cast<char>(GuestByteAt(base, row + i));
            if (c == '\0') {
                code[i] = '\0';
                return i;
            }

            const bool plain = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                               (c >= '0' && c <= '9') || c == '-';
            if (!plain) {
                return 0;
            }
            code[i] = c;
        }
        return 0;
    };

    char first[kCodeCapacity + 1] = {};
    char second[kCodeCapacity + 1] = {};
    const uint32_t length = read_code(first);
    if (length == 0 || read_code(second) != length ||
        std::memcmp(first, second, length) != 0 || length + 1u > capacity) {
        return 0;
    }

    std::memcpy(out, first, length);
    out[length] = '\0';
    return length;
}


/// Copies the current Bink video frame out of guest memory as RGBA8.
///
/// Fable 2 decodes Bink on the CPU into four 8-bit LINEAR planes (Y, cR, cB, A) that the
/// GPU used to sample in place. With no GPU those planes are still produced and still
/// correct, so showing video needs no emulation at all — only this read and a colour
/// conversion. Layout confirmed against ExpandBink (0x832C49E8), whose FIRST ARGUMENT is
/// the BINKFRAMEBUFFERS block:
///
///   FB+4/+8    luma width/height        FB+12/+16  chroma width/height
///   FB+20      FrameNum (2 frames, alternating)
///   FB+24 + 48*n + 12*k + 4/+8         plane k Buffer / pitch  (k: 0=Y 1=cR 2=cB 3=A)
///
/// CHROMA IS NOT HALF LUMA — every dimension is independently (x+7) & ~7, so the chroma
/// sizes are read from the struct and the sample position is scaled by the real ratio.
///
/// Writes width*height*4 bytes of RGBA into `out_rgba`. Returns 1 on success, 0 when no
/// video is playing or the buffer is too small; `out_width`/`out_height` are filled in
/// either way when known, so a caller can size its buffer from a failed probe.
REFII_HOST_ABI_EXPORT int RefiiHostGetVideoFrame(uint8_t* out_rgba, uint32_t capacity, uint32_t* out_width,
                           uint32_t* out_height) {
    const uint32_t fb = refii::vfs::BinkFrameBuffersGuestAddress();
    if (fb == 0) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t luma_width = load_be32(fb + 4);
    const uint32_t luma_height = load_be32(fb + 8);
    const uint32_t chroma_width = load_be32(fb + 12);
    const uint32_t chroma_height = load_be32(fb + 16);
    const uint32_t total_frames = load_be32(fb + 0);
    // Read the OTHER frame, not the one FrameNum names.
    //
    // TotalFrames is 2 because this is a double buffer, and ExpandBink decodes INTO the
    // new frame and then sets FrameNum to it (`*(FB+20) = v52 ^ 1` at 0x832c4b3c, with
    // the expander's destination taken from that same index). So FrameNum is the frame
    // being written; reading it races the decoder and produces a picture assembled from
    // two different moments — the Lionhead logo came out sheared and banded, which reads
    // as a stride bug but is tearing. The previous frame is complete and stable.
    const uint32_t current = load_be32(fb + 20) & 1u;
    const uint32_t frame = total_frames > 1 ? (current ^ 1u) : current;

    // A STRUCTURAL plausibility gate, not just a size clamp.
    //

    // The block is only meaningful while a movie is live. Once a logo finishes and the
    // player is reused the same memory reads as whatever came next, and a pure size
    // clamp happily accepts it: a run produced a correct 1280x720 frame and then
    // "2258x2257", which is under any sane cap and would have been uploaded as a frame.
    // TotalFrames is the strongest discriminator — Bink uses 1 or 2 — and the pitch and
    // chroma relations have to hold for any real plane set.
    if (total_frames == 0 || total_frames > 2) {
        return 0;
    }

    if (luma_width < 16 || luma_height < 16 || luma_width > 4096 || luma_height > 4096 ||
        chroma_width == 0 || chroma_height == 0 || chroma_width > luma_width ||
        chroma_height > luma_height) {
        return 0;
    }

    // Dimensions are published only once the block has PASSED validation. Reporting them
    // earlier meant a caller that ignored the return value sized a texture from a torn
    // read — one run allocated 2258x2257 from a dead player's memory.
    if (out_width != nullptr) {
        *out_width = luma_width;
    }
    if (out_height != nullptr) {
        *out_height = luma_height;
    }

    // A null buffer is a SIZE PROBE and succeeds: the caller is asking whether video is
    // live and how big it is. Returning 0 here would make "no video playing" and "your
    // buffer is too small" indistinguishable, and the caller would size against garbage.
    if (out_rgba == nullptr) {
        return 1;
    }

    if (capacity < luma_width * luma_height * 4u) {
        return 0;
    }

    const uint32_t plane_base = fb + 24u + (48u * frame);
    const uint32_t y_ptr = load_be32(plane_base + 4);
    const uint32_t y_pitch = load_be32(plane_base + 8);
    const uint32_t cr_ptr = load_be32(plane_base + 12 + 4);
    const uint32_t cr_pitch = load_be32(plane_base + 12 + 8);
    const uint32_t cb_ptr = load_be32(plane_base + 24 + 4);
    const uint32_t cb_pitch = load_be32(plane_base + 24 + 8);

    // A plane's pitch cannot be narrower than its own width, and the buffers must sit in
    // the guest's 32-bit space with room for the whole plane.
    if (y_ptr == 0 || cr_ptr == 0 || cb_ptr == 0) {
        return 0;
    }

    if (y_pitch < luma_width || cr_pitch < chroma_width || cb_pitch < chroma_width) {
        return 0;
    }

    const auto plane_fits = [](uint32_t ptr, uint32_t pitch, uint32_t rows) {
        const uint64_t end = (uint64_t)ptr + ((uint64_t)pitch * rows);
        return ptr != 0 && end <= 0x100000000ull;
    };

    if (!plane_fits(y_ptr, y_pitch, luma_height) ||
        !plane_fits(cr_ptr, cr_pitch, chroma_height) ||
        !plane_fits(cb_ptr, cb_pitch, chroma_height)) {
        return 0;
    }

    // One-shot, AFTER validation: reporting before it burned the single log on the
    // window between the movie-open hook (which runs before the original fills the
    // block) and BinkGetFrameBuffersInfo actually populating it.
    {
        static std::atomic<bool> announced{false};
        if (!announced.exchange(true)) {
            REXLOG_INFO(
                "Refii video: total={} luma={}x{} chroma={}x{} frame={} "
                "y={{0x{:08X} pitch {}}} cr={{0x{:08X} pitch {}}} cb={{0x{:08X} pitch {}}}",
                total_frames, luma_width, luma_height, chroma_width, chroma_height,
                frame, y_ptr, y_pitch, cr_ptr, cr_pitch, cb_ptr, cb_pitch);
        }
    }

    // Rows are staged out of guest memory through the guarded copy rather than read in
    // place: a frame buffer the guest has just released must read as zeroes, not fault.
    std::vector<uint8_t> staged((std::size_t)luma_width + 2u * (std::size_t)chroma_width);
    uint8_t* y_row = staged.data();
    uint8_t* cr_row = y_row + luma_width;
    uint8_t* cb_row = cr_row + chroma_width;

    for (uint32_t row = 0; row < luma_height; ++row) {
        // Scale into chroma by the REAL ratio rather than assuming 2:1 — the planes are
        // 8-aligned independently, so at some sizes they are not exactly half.
        const uint32_t chroma_row =
            (uint32_t)((uint64_t)row * chroma_height / luma_height);
        CopyGuest(base, y_ptr + row * y_pitch, y_row, luma_width);
        CopyGuest(base, cr_ptr + chroma_row * cr_pitch, cr_row, chroma_width);
        CopyGuest(base, cb_ptr + chroma_row * cb_pitch, cb_row, chroma_width);
        uint8_t* out_row = out_rgba + ((uint64_t)row * luma_width * 4u);

        for (uint32_t col = 0; col < luma_width; ++col) {
            const uint32_t chroma_col =
                (uint32_t)((uint64_t)col * chroma_width / luma_width);

            // BT.601 studio-swing, which is what Bink encodes.
            const int32_t y = (int32_t)y_row[col] - 16;
            const int32_t u = (int32_t)cb_row[chroma_col] - 128;
            const int32_t v = (int32_t)cr_row[chroma_col] - 128;

            const int32_t c = 298 * y;
            const auto clamp = [](int32_t value) {
                if (value < 0) return (uint8_t)0;
                if (value > 255) return (uint8_t)255;
                return (uint8_t)value;
            };

            out_row[col * 4 + 0] = clamp((c + 409 * v + 128) >> 8);
            out_row[col * 4 + 1] = clamp((c - 100 * u - 208 * v + 128) >> 8);
            out_row[col * 4 + 2] = clamp((c + 516 * u + 128) >> 8);
            out_row[col * 4 + 3] = 255;
        }
    }

    return 1;
}

/// Copies the current Bink frame's PLANES out, for the GPU to combine.
///
/// The RGBA variant above converts 921,600 pixels on the CPU every frame. This hands the
/// three 8-bit planes over untouched — about 1.4 MB against 3.7 MB, with no arithmetic —
/// and lets a shader do the colour conversion where that work is free. It is also less
/// lossy: no intermediate rounding, and chroma is sampled by the GPU's own filtering
/// rather than by nearest-neighbour integer scaling.
///
/// Each plane is copied ROW BY ROW into a tightly packed destination, because the guest's
/// pitch is not the width — chroma here is 640 wide with a 768-byte pitch, and handing
/// that to a texture upload as-is produces a skewed image.
///
/// `out_desc` receives 6 uint32s: luma w, luma h, chroma w, chroma h, and the two plane
/// sizes actually written (luma, chroma). Returns 1 on success.
REFII_HOST_ABI_EXPORT int RefiiHostGetVideoPlanes(uint8_t* out_y, uint32_t y_capacity, uint8_t* out_cr,
                            uint32_t cr_capacity, uint8_t* out_cb, uint32_t cb_capacity,
                            uint32_t* out_desc) {
    const uint32_t fb = refii::vfs::BinkFrameBuffersGuestAddress();
    if (fb == 0) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t luma_width = load_be32(fb + 4);
    const uint32_t luma_height = load_be32(fb + 8);
    const uint32_t chroma_width = load_be32(fb + 12);
    const uint32_t chroma_height = load_be32(fb + 16);
    const uint32_t total_frames = load_be32(fb + 0);

    // Same double-buffer rule as the RGBA path: FrameNum names the frame being written.
    const uint32_t current = load_be32(fb + 20) & 1u;
    const uint32_t frame = total_frames > 1 ? (current ^ 1u) : current;

    if (total_frames == 0 || total_frames > 2) {
        return 0;
    }

    if (luma_width < 16 || luma_height < 16 || luma_width > 4096 || luma_height > 4096 ||
        chroma_width == 0 || chroma_height == 0 || chroma_width > luma_width ||
        chroma_height > luma_height) {
        return 0;
    }

    const uint32_t plane_base = fb + 24u + (48u * frame);
    const uint32_t y_ptr = load_be32(plane_base + 4);
    const uint32_t y_pitch = load_be32(plane_base + 8);
    const uint32_t cr_ptr = load_be32(plane_base + 16);
    const uint32_t cr_pitch = load_be32(plane_base + 20);
    const uint32_t cb_ptr = load_be32(plane_base + 28);
    const uint32_t cb_pitch = load_be32(plane_base + 32);

    if (y_ptr == 0 || cr_ptr == 0 || cb_ptr == 0 || y_pitch < luma_width ||
        cr_pitch < chroma_width || cb_pitch < chroma_width) {
        return 0;
    }

    const auto plane_fits = [](uint32_t ptr, uint32_t pitch, uint32_t rows) {
        return ptr != 0 && ((uint64_t)ptr + ((uint64_t)pitch * rows)) <= 0x100000000ull;
    };

    if (!plane_fits(y_ptr, y_pitch, luma_height) ||
        !plane_fits(cr_ptr, cr_pitch, chroma_height) ||
        !plane_fits(cb_ptr, cb_pitch, chroma_height)) {
        return 0;
    }

    const uint32_t luma_bytes = luma_width * luma_height;
    const uint32_t chroma_bytes = chroma_width * chroma_height;

    if (out_desc != nullptr) {
        out_desc[0] = luma_width;
        out_desc[1] = luma_height;
        out_desc[2] = chroma_width;
        out_desc[3] = chroma_height;
        out_desc[4] = luma_bytes;
        out_desc[5] = chroma_bytes;
    }

    // Null buffers are a size probe, as with the RGBA path.
    if (out_y == nullptr || out_cr == nullptr || out_cb == nullptr) {
        return 1;
    }

    if (y_capacity < luma_bytes || cr_capacity < chroma_bytes ||
        cb_capacity < chroma_bytes) {
        return 0;
    }

    const auto copy_plane = [&](uint32_t ptr, uint32_t pitch, uint32_t width,
                                uint32_t height, uint8_t* dst) {
        for (uint32_t row = 0; row < height; ++row) {
            CopyGuest(base, ptr + row * pitch, dst + ((std::size_t)row * width), width);
        }
    };

    copy_plane(y_ptr, y_pitch, luma_width, luma_height, out_y);
    copy_plane(cr_ptr, cr_pitch, chroma_width, chroma_height, out_cr);
    copy_plane(cb_ptr, cb_pitch, chroma_width, chroma_height, out_cb);
    return 1;
}



/// This player's role in a co-op session, off the network manager `g_XrnmNetContext`
/// (0x83497490; `*(*(0x83496AB8)+12)+140` is its counted copy, the fallback): `+52` in a
/// session, `+53` host, `+54` joiner. 0 no session, 1 host, 2 joiner. SOLO READS AS HOST:
/// a single-player game has +52 and +53 set, so 1 is "host or solo", not "co-op host".
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopRole(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    // The manager itself (`g_XrnmNetContext`, 0x83497490); `game+140` is only its counted copy
    // and was empty on a title-joined client in game, which read as role 0 there.
    uint32_t coop = GuestWordAt(base, refii::guest::Address(0x83497490u));
    if (!plausible(coop)) {
        const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
        const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
        coop = plausible(game) ? GuestWordAt(base, game + 140u) : 0u;
    }
    if (!plausible(coop) || GuestByteAt(base, coop + 52u) == 0u) return 0;
    if (GuestByteAt(base, coop + 53u) != 0u) return 1;
    return GuestByteAt(base, coop + 54u) != 0u ? 2 : 0;
}

/// The co-op screen's two share sliders, 0..1. THE GLOBALS ARE THE LIVE VALUE — `sub_82437168`
/// writes them at 0x82437238 (`flt_833193D4`, gold) and 0x82437230 (`flt_833193D8`,
/// experience); the manager's own copies at `+176`/`+172` sat at zero under a centred pip.
/// Returns 1 when a co-op manager exists to read against.
REFII_HOST_ABI_EXPORT int RefiiHostGetCoopShares(float* out_gold, float* out_experience) {
    if (out_gold != nullptr) *out_gold = 0.5f;
    if (out_experience != nullptr) *out_experience = 0.5f;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };
    // The manager itself, the game object's copy only as a fallback — see RefiiHostGetCoopRole.
    uint32_t coop = GuestWordAt(base, refii::guest::Address(0x83497490u));
    if (!plausible(coop)) {
        const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
        const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
        coop = plausible(game) ? GuestWordAt(base, game + 140u) : 0u;
    }
    if (!plausible(coop)) return 0;
    if (out_gold != nullptr) *out_gold = read_float(refii::guest::Address(0x833193D4u));
    if (out_experience != nullptr) *out_experience = read_float(refii::guest::Address(0x833193D8u));
    return 1;
}

/// Whether the guest is IN GAME, as the guest itself states it.
///
/// `byte_83496A5E` — set to 1 by `Game_InitDatabases` at 0x82311648 and cleared by the game
/// mode's shutdown at 0x82311860, with ~150 readers. This is the signal to use rather than
/// inferring from the front end going away: `dword_83496B08` is re-pointed at a 176-byte
/// base-only GUI at that moment, so every front-end field reads out of bounds.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostIsInGame(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    return GuestByteAt(base, refii::guest::Address(0x83496A5Eu));
}


REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimTick(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    uint32_t raw;
    raw = __builtin_bswap32(GuestWordAt(base, refii::guest::Address(0x83319650u)));
    return __builtin_bswap32(raw);
}

/// The guest's own simulation clock, in seconds, and its game-tick counter.
///
/// `sub_82276D10` is the in-game loop; it derives its step from two stated rates
/// (`dbl_83319660` = 15.0 game ticks/s, `dbl_83319668` = 30.0 HF/s, raised from the image's
/// 15.0 at 0x82310900, so the image bytes give the wrong HF rate) and advances its clock by
/// exactly 1/30 s per HF step:
///
///     0x82276f14  fmadd f2, f3, f27, f26     ; base + steps*(1/30)
///     0x82276f18  stfd  f2, 0x110(r31)       ; game+0x110 = the sim clock, seconds
///
/// More than one step behind, it advances one step and re-bases, discarding the lost time:
///
///     82276fa4  cmpwi cr6, r30, 1
///     82276fb0  ble   cr6, loc_82276FC8      ; owed <= 1 -> normal
///     82276fb4  addi  r29, r28, 1            ; advance exactly one
///     82276fb8  fmadd f0, f27, f24, f31      ; f24 = 0.0, so f0 = now
///     82276fbc  stfd  f0, 0x110(r31)         ; sim clock := now
///
/// So the tick rate is not a constant: 15/s while it keeps up, lower under load, with
/// discontinuous resets.
///
/// `out_tick` is `*(*(0x83496AB8) + 0x0C) + 0xFC`, measured from the second deref (the 288-byte
/// loop object), not the 16-byte game root. Zeroed at construction (0x82310630) and incremented
/// once per game tick at 0x821c9044 with no gate — unlike `dword_83319650`, whose increment
/// self-gates on the front GameFlow command matching (0x821d46e8), which is reset to 1 on world
/// teardown (0x8235075c) and saved as "WorldFrame" (0x82350A34).
///
/// Returns 0 when the world is not up, in which case neither output is written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimClock(double* out_seconds, uint32_t* out_tick) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    if (!plausible(root)) {
        return 0;
    }

    const uint32_t game = GuestWordAt(base, root + 0xCu);
    if (!plausible(game)) {
        return 0;
    }

    const uint64_t bits = GuestWord64At(base, game + 0x110u);
    double seconds = 0.0;
    std::memcpy(&seconds, &bits, sizeof(seconds));

    // Seconds since the loop based itself. Negative or non-finite is a torn read or a world
    // that has not started.
    if (!std::isfinite(seconds) || seconds < 0.0) {
        return 0;
    }

    if (out_seconds != nullptr) {
        *out_seconds = seconds;
    }

    if (out_tick != nullptr) {
        *out_tick = GuestWordAt(base, game + 0xFCu);
    }

    return 1;
}


/// Live-effect transforms refused this run: a torn read, or a node whose transform the guest
/// has never composed.
static std::atomic<uint32_t> g_fx_refused_transforms{0u};

/// EVERY PARTICLE EFFECT THE GUEST IS CURRENTLY PLAYING, so Godot can draw the ones it did not
/// place itself.
///
/// The level's ambient effects — torches, braziers, waterfalls — Godot resolves for itself out
/// of the GDB and spawns natively. Everything the guest starts at RUNTIME had no route to the
/// screen at all: spell casts, combat impacts, a fire catching. This is that route.
///
/// IT IS A MIRROR, NOT A HOOK, and that is only possible because the particle update sits on the
/// SIM path. The chain is `sub_82319BE8` (the wall-clock main loop) -> `sub_821DC270` ->
/// `sub_821DFDD0` (world update) -> `sub_83234C38` (CParticleSystemManager::Update) -> per
/// instance `sub_8322D988`. Nothing in it touches a present, a GPU wait or the render thread, so
/// the guest keeps ticking its particle systems perfectly well with our no-GPU latch set and
/// never drawing a pixel.
///
/// THE LIST is the flat vector every instance factory pushes into — `sub_8322A388`,
/// `sub_83230F00` and their siblings all end in `sub_82450DB8(&dword_8349E48C, ...)`. Its
/// removal side, `sub_83234338`, reads back `(dword_8349E494 - dword_8349E490) >> 3`, which
/// fixes the stride at 8: an instance pointer and its refcount node.
///
/// INDICES ARE NOT STABLE. That same function retires by swapping with the last element and
/// popping, so an effect's slot changes when an unrelated one ends. The instance POINTER is the
/// identity, which is why it is the first word out.
///
/// WHY THE TRANSFORM IS A WALK. `instance+0x50` looks like a world position and is not: it is
/// the authored local translation, before the type's own offset matrix and the uniform scale.
/// `sub_821DA198` composes those into the cached local 4x4 at `instance+0x70`, and
/// `sub_832295F0` concatenates that up the `+0xF0` parent chain to reach world. Mirroring
/// `+0x50` directly would put every runtime effect somewhere plausible and wrong.
/// The transforms stated through `sub_832325A8` (position) and `sub_83231C40` (orientation), as
/// recorded by the hook in `particle_transform.cpp`, keyed by the PSInstance pointer — the same
/// identity `RefiiHostGetActiveFx` reports. These setters are not the only writers of an
/// emitter's transform (see particle_transform.cpp); this is an instrument.
///
/// The hook that records these compiles into the payload, which this library must not link;
/// the table it writes lives in refii_vfs, which both link, so the read is a direct call and a
/// feed nothing has stated yet is simply empty.
///
/// Ten words a row: instance, sequence, four position lanes, four orientation lanes. All four
/// VMX lanes are carried because which lane holds x depends on the recompiler's register layout.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFx(uint32_t* out_words, uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < 10u) {
        return 0u;
    }

    return refii::vfs::StatedFxTransforms(out_words, word_capacity);
}

/// How many transform statements the hook has seen since boot; 0 before the first.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFxCount(void) {
    return refii::vfs::StatedFxCount();
}

static uint32_t ReadParticleEntityTypeId(const uint8_t* base, uint32_t type) {
    if (type < 0x1000u || (uint64_t)type + 0x200ull > 0x100000000ull) {
        return 0xFFFFFFFFu;
    }
    const uint32_t type_vt = GuestWordAt(base, type);
    if (type_vt < 0x1000u || (uint64_t)type_vt + 0x40ull > 0x100000000ull) {
        return 0xFFFFFFFFu;
    }
    const uint32_t fn = GuestWordAt(base, type_vt + 20u);
    if (fn < 0x1000u || (uint64_t)fn + 8ull > 0x100000000ull) {
        return 0xFFFFFFFFu;
    }
    const uint32_t w0 = GuestWordAt(base, fn);
    const uint32_t w1 = GuestWordAt(base, fn + 4u);
    if ((w0 & 0xFFFF0000u) == 0x38600000u && w1 == 0x4E800020u) {
        return w0 & 0xFFFFu;
    }
    return 0xFFFFFFFFu;
}

/// Reads a live Type 9 `CParticleSplineEmitterNode`'s world-space spline points (`node + 0x1C8..0x1CC`,
/// 16-byte stride, active flag at `node + 0x1B0`) for `instance_or_handle` (`fx.Instance` or `fx.Handle`).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFxSpline(uint32_t instance_or_handle, float* out, uint32_t max_points) {
    if (instance_or_handle == 0u || out == nullptr || max_points == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto plausible = [](uint32_t ptr) {
        return ptr >= 0x1000u && (uint64_t)ptr + 0x200ull <= 0x100000000ull;
    };

    const uint32_t begin = GuestWordAt(base, refii::guest::Address(0x8349E490u));
    const uint32_t end = GuestWordAt(base, refii::guest::Address(0x8349E494u));
    if (!plausible(begin) || end < begin) {
        return 0u;
    }

    const uint32_t span = end - begin;
    if ((span % 8u) != 0u || span > 0x40000u) {
        return 0u;
    }

    const uint32_t count = span / 8u;
    uint32_t matched_node = 0u;
    uint32_t fallback_handle_node = 0u;
    for (uint32_t i = 0u; i < count; ++i) {
        const uint32_t node = GuestWordAt(base, begin + i * 8u);
        if (!plausible(node) || GuestByteAt(base, node + 0xC8u) != 0u) {
            continue;
        }

        if (node == instance_or_handle) {
            matched_node = node;
            break;
        }

        if (fallback_handle_node == 0u &&
            GuestWordAt(base, node + 0xE8u) == instance_or_handle &&
            ReadParticleEntityTypeId(base, GuestWordAt(base, node + 0xD0u)) == 9u &&
            GuestByteAt(base, node + 0x1B0u) != 0u) {
            fallback_handle_node = node;
        }
    }

    const uint32_t target = matched_node != 0u ? matched_node : fallback_handle_node;
    if (!plausible(target) ||
        ReadParticleEntityTypeId(base, GuestWordAt(base, target + 0xD0u)) != 9u ||
        GuestByteAt(base, target + 0x1B0u) == 0u) {
        return 0u;
    }

    const uint32_t sp_begin = GuestWordAt(base, target + 0x1C8u);
    const uint32_t sp_end = GuestWordAt(base, target + 0x1CCu);
    if (sp_begin < 0x1000u || sp_end <= sp_begin || ((sp_end - sp_begin) & 0xFu) != 0u) {
        return 0u;
    }

    const uint32_t sp_count = (sp_end - sp_begin) >> 4;
    if (sp_count < 2u || sp_count > 512u ||
        (uint64_t)sp_begin + (uint64_t)sp_count * 16ull > 0x100000000ull) {
        return 0u;
    }

    const uint32_t to_copy = sp_count < max_points ? sp_count : max_points;
    for (uint32_t p = 0u; p < to_copy; ++p) {
        for (uint32_t axis = 0u; axis < 3u; ++axis) {
            const uint32_t bits = GuestWordAt(base, sp_begin + p * 16u + axis * 4u);
            float value;
            std::memcpy(&value, &bits, sizeof(value));
            if (!std::isfinite(value)) {
                return 0u;
            }
            out[p * 3u + axis] = value;
        }
    }

    return to_copy;
}

/// Reads the complete guest `Mode == 3` double-trail ring (`(EdgeA, EdgeB, keyTime)`, oldest to
/// newest, in raw guest world coordinates, 7 floats per point) from `d = *(node + 0x168)` for
/// `instance_or_handle` (`fx.Instance` or `fx.Handle`). Filters out torn or degenerate slots per
/// entry so a single mid-write slot never collapses a fast-moving projectile trail (e.g. Blades).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFxDoubleTrailRing(
    uint32_t instance_or_handle, float* out, uint32_t max_points) {
    if (instance_or_handle == 0u || out == nullptr || max_points < 2u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto readf = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };
    const auto plausible = [](uint32_t ptr) {
        return ptr >= 0x1000u && (uint64_t)ptr + 0x200ull <= 0x100000000ull;
    };

    const uint32_t begin = read32(refii::guest::Address(0x8349E490u));
    const uint32_t end = read32(refii::guest::Address(0x8349E494u));
    if (!plausible(begin) || end < begin) {
        return 0u;
    }

    const uint32_t span = end - begin;
    if ((span % 8u) != 0u || span > 0x40000u) {
        return 0u;
    }

    const uint32_t count = span / 8u;
    uint32_t matched_node = 0u;
    uint32_t fallback_handle_node = 0u;
    for (uint32_t i = 0u; i < count; ++i) {
        const uint32_t node = read32(begin + i * 8u);
        if (!plausible(node) || GuestByteAt(base, node + 0xC8u) != 0u) {
            continue;
        }

        if (node == instance_or_handle) {
            matched_node = node;
            break;
        }

        if (fallback_handle_node == 0u &&
            read32(node + 0xE8u) == instance_or_handle &&
            ReadParticleEntityTypeId(base, read32(node + 0xD0u)) == 0u) {
            const uint32_t sys_def = read32(node + 0x170u);
            if (plausible(sys_def) && read32(sys_def + 0x20u) == 3u) {
                fallback_handle_node = node;
            }
        }
    }

    const uint32_t target = matched_node != 0u ? matched_node : fallback_handle_node;
    if (!plausible(target)) {
        return 0u;
    }

    uint32_t sim_d = 0u;
    uint32_t twin_ptr = 0u;
    uint32_t twin_n = 0xFFFFu;
    if (ReadParticleEntityTypeId(base, read32(target + 0xD0u)) == 0u) {
        const uint32_t sys_def = read32(target + 0x170u);
        if (plausible(sys_def) && read32(sys_def + 0x20u) == 3u) {
            sim_d = read32(target + 0x168u);
        }
    }

    const uint32_t twin_table = read32(refii::guest::Address(0x8349F7ACu));
    const uint32_t twin_table_end = read32(refii::guest::Address(0x8349F7B0u));
    const uint32_t twin_handles =
        plausible(twin_table) && twin_table_end >= twin_table
            ? (twin_table_end - twin_table) / 4u
            : 0u;
    const uint32_t holder_vtable = refii::guest::Address(0x82001AC0u);
    if (twin_handles != 0u) {
        const uint32_t holder = read32(target + 0x16Cu);
        if (plausible(holder) && read32(holder) == holder_vtable) {
            const uint32_t proxy = read32(holder + 24u);
            const uint32_t twin_handle = plausible(proxy) ? read32(proxy + 4u) : twin_handles;
            const uint32_t twin = twin_handle < twin_handles ? read32(twin_table + twin_handle * 4u) : 0u;
            if (plausible(twin) && read32(twin + 16u) == twin_handle && read32(twin + 240u) == 3u) {
                twin_ptr = twin;
                twin_n = read32(twin + 176u);
                const uint32_t shared = read32(twin + 180u);
                if (!plausible(sim_d) && plausible(shared)) {
                    sim_d = read32(shared + 4u);
                }
            }
        }
    }

    if (!plausible(sim_d) || twin_n < 2u) {
        return 0u;
    }

    const uint32_t cap = max_points < 64u ? max_points : 64u;
    float best_buf[64u * 7u];
    uint32_t best_written = 0u;

    // Lock-free seqlock-style retry (up to 3 attempts): the guest updates each 32-byte slot
    // without a lock, so a single read can catch edge A written before edge B (span > 6m) or
    // `(head, cnt, twin_n)` mid-advance. Clamping to `twin_n` (`twin + 176u`) matches
    // `RefiiHostGetClassicTrails` (`sub_82179C00`) so stale wrapped entries beyond the active
    // render count never corrupt the leading tip of fast projectile trails (e.g. Blades).
    for (int attempt = 0; attempt < 3; ++attempt) {
        const uint32_t cur_twin_n = twin_ptr != 0u ? read32(twin_ptr + 176u) : 0xFFFFu;
        if (cur_twin_n < 2u) {
            return 0u;
        }
        const uint32_t ring = read32(sim_d + 8u);
        const uint32_t ring_end = read32(sim_d + 12u);
        const uint32_t head = read32(sim_d + 20u);
        const uint32_t mask = read32(sim_d + 32u);
        const uint32_t cnt = read32(sim_d + 36u);
        if (ring < 0x1000u || mask == 0u || mask > 4095u || ((mask & (mask + 1u)) != 0u)) {
            return 0u;
        }
        const uint64_t expected_end = (uint64_t)ring + (uint64_t)(mask + 1u) * 32ull;
        if (expected_end > 0x100000000ull || (ring_end != 0u && (uint64_t)ring_end != expected_end)) {
            return 0u;
        }
        const uint32_t active_cnt = (cur_twin_n != 0xFFFFu && cur_twin_n < cnt) ? cur_twin_n : cnt;
        if (active_cnt < 2u || active_cnt > (mask + 1u) || cnt > (mask + 1u)) {
            return 0u;
        }

        // `head + 0` is the oldest tail entry and `head + cnt - 1` is the newest leading sword tip:
        // when `active_cnt < cnt` (`cur_twin_n < cnt`), skip expired tail entries at the front so the
        // leading tip at the flying projectile is always preserved intact.
        const uint32_t tail_skip = cnt - active_cnt;
        const uint32_t cap_skip = active_cnt > cap ? (active_cnt - cap) : 0u;
        const uint32_t start = tail_skip + cap_skip;
        float tmp[64u * 7u];
        uint32_t written = 0u;
        bool saw_torn = false;
        float prev_mx = 0.0f, prev_my = 0.0f, prev_mz = 0.0f;
        for (uint32_t i = start; i < cnt && written < cap; ++i) {
            const uint32_t idx = (head + i) & mask;
            const uint32_t entry = ring + idx * 32u;
            const float ax = readf(entry + 0u), ay = readf(entry + 4u), az = readf(entry + 8u);
            const float bx = readf(entry + 16u), by = readf(entry + 20u), bz = readf(entry + 24u);
            const float key_time = readf(entry + 28u);
            if (!std::isfinite(ax) || !std::isfinite(ay) || !std::isfinite(az) ||
                !std::isfinite(bx) || !std::isfinite(by) || !std::isfinite(bz) ||
                !std::isfinite(key_time)) {
                saw_torn = true;
                continue;
            }

            const float dx = ax - bx, dy = ay - by, dz = az - bz;
            const float span_sq = dx * dx + dy * dy + dz * dz;
            if (span_sq <= 1e-6f || span_sq > 36.0f) {
                saw_torn = true;
                continue;
            }

            const float mx = 0.5f * (ax + bx);
            const float my = 0.5f * (ay + by);
            const float mz = 0.5f * (az + bz);
            if (written > 0u) {
                const float jx = mx - prev_mx, jy = my - prev_my, jz = mz - prev_mz;
                if (jx * jx + jy * jy + jz * jz > 100.0f) {
                    saw_torn = true;
                    continue;
                }
            }

            float* dst = tmp + written * 7u;
            dst[0] = ax; dst[1] = ay; dst[2] = az;
            dst[3] = bx; dst[4] = by; dst[5] = bz;
            dst[6] = key_time;
            prev_mx = mx; prev_my = my; prev_mz = mz;
            ++written;
        }

        const uint32_t head_after = read32(sim_d + 20u);
        const uint32_t cnt_after = read32(sim_d + 36u);
        const uint32_t twin_n_after = twin_ptr != 0u ? read32(twin_ptr + 176u) : 0xFFFFu;
        if (written > best_written) {
            best_written = written;
            std::memcpy(best_buf, tmp, written * 7u * sizeof(float));
        }

        if (!saw_torn && head_after == head && cnt_after == cnt && twin_n_after == cur_twin_n && written >= 2u) {
            std::memcpy(out, tmp, written * 7u * sizeof(float));
            return written;
        }
    }

    if (best_written >= 2u) {
        std::memcpy(out, best_buf, best_written * 7u * sizeof(float));
        return best_written;
    }

    return 0u;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveFx(uint32_t* out, uint32_t max_effects) {
    if (out == nullptr || max_effects == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto readf = [&read32](uint32_t at) {
        const uint32_t bits = read32(at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };

    /// A guest pointer we are willing to dereference. The walk runs while the guest is mutating
    /// the vector, so a torn read is expected and must be refused rather than followed.
    ///
    /// THE WHOLE 32-BIT SPACE IS VALID, and narrowing it is how this returned nothing at all on
    /// its first run. The bound used to be `>= 0x82000000 && < 0x90000000`, which is roughly the
    /// EXECUTABLE IMAGE's range — fine for a global, useless for a pointer to a heap object.
    /// Every particle instance is allocated at runtime and lands outside it, so every entry was
    /// rejected and the feed reported an empty world.
    ///
    /// This is the same test the rest of this file uses for a guest buffer: non-null, and inside
    /// the guest's own 4 GB space with room for what we are about to read.
    const auto plausible = [](uint32_t ptr) {
        return ptr >= 0x1000u && (uint64_t)ptr + 0x200ull <= 0x100000000ull;
    };

    // The composed world matrix is read from `node + 0x20`; see the read site below.

    const uint32_t kVectorBegin = refii::guest::Address(0x8349E490u);
    const uint32_t kVectorEnd = refii::guest::Address(0x8349E494u);

    const uint32_t kReservedRootName = refii::guest::Address(0x8349E678u);

    /// 22 words per node: instance pointer, name hash, flags, live-particle count, the world
    /// position (3 floats), the world basis rows 0..2 (9 floats), the effect handle, the
    /// effect's intensity (float), the element's scale (float, +412), its fade-out fraction
    /// (float), the entity that owns the effect and the entity it is attached to (0 when
    /// none), and a stated colour's red, green, blue and alpha (floats; white here, where the
    /// effect's colour is the intensity above), and the particle flushes stated (0 here). Must
    /// match `HostBridge.ActiveFxStride`; no compiler checks it.
    constexpr uint32_t kStride = 27u;

    // Both ends re-read together: the vector reallocates as it grows and no lock is taken.
    const uint32_t begin = read32(kVectorBegin);
    const uint32_t end = read32(kVectorEnd);
    if (!plausible(begin) || end < begin) {
        return 0u;
    }

    const uint32_t span = end - begin;
    if ((span % 8u) != 0u || span > 0x40000u) {
        return 0u;
    }

    const uint32_t reserved_root = read32(kReservedRootName);

    // The guest's own per-effect "still working" count.
    //
    // The retire pass `sub_83234338` walks this registry (stride 28, `v8 = (end - begin) / 28`),
    // asks each node its virtual has-work question (`sub_832299C8`), and on a yes decrements
    // record+24 (`v15 = v10[6] - 1; v10[6] = v15;`). At zero the record is erased and the
    // effect's owner is told, so `record+24 > 0` is the guest's stated liveness. The has-work
    // body is virtual and differs per particle class, so it is not transcribed here.
    struct EffectWork {
        uint32_t instance;
        uint32_t working;
    };
    constexpr uint32_t kMaxEffectRegistry = 2048u;
    EffectWork registry[kMaxEffectRegistry];
    uint32_t registry_count = 0u;
    {
        const uint32_t rbegin = read32(refii::guest::Address(0x8349E480u));
        const uint32_t rend = read32(refii::guest::Address(0x8349E484u));
        if (plausible(rbegin) && rend > rbegin && ((rend - rbegin) % 28u) == 0u &&
            (rend - rbegin) <= 28u * kMaxEffectRegistry) {
            for (uint32_t at = rbegin; at + 28u <= rend && registry_count < kMaxEffectRegistry; at += 28u) {
                const uint32_t instance = read32(at);
                if (!plausible(instance)) {
                    continue;
                }

                registry[registry_count].instance = instance;
                registry[registry_count].working = read32(at + 24u);
                ++registry_count;
            }
        }
    }

    uint32_t registry_hint = 0u;
    const uint32_t twin_table = read32(refii::guest::Address(0x8349F7ACu));
    const uint32_t twin_table_end = read32(refii::guest::Address(0x8349F7B0u));
    const uint32_t twin_handles =
        plausible(twin_table) && twin_table_end >= twin_table
            ? (twin_table_end - twin_table) / 4u
            : 0u;
    const uint32_t holder_vtable = refii::guest::Address(0x82001AC0u);
    const uint32_t count = span / 8u;
    uint32_t written = 0u;

    for (uint32_t i = 0; i < count && written < max_effects; ++i) {
        const uint32_t node = read32(begin + i * 8u);
        if (!plausible(node)) {
            continue;
        }

        // `+0xC8` is "kill requested", set by `sub_83228E88` and cleared in the ctor. The retire
        // pass drops these the same tick; skipping them stops a dying effect flickering back.
        if (read8(node + 0xC8u) != 0u) {
            continue;
        }

        const uint32_t type = read32(node + 0xD0u);
        if (!plausible(type)) {
            continue;
        }

        // `type+4` is FNV-1 of the node's LOWERCASED name — `sub_8323BE18` lower-cases twice and
        // calls `rex_lhResourceName_ComputeHash`, and `sub_822CA588` is FNV-1 (multiply THEN
        // xor), the same hash the particle bank is keyed on.
        const uint32_t name_hash = read32(type + 4u);
        if (name_hash == 0u) {
            continue;
        }

        // The synthetic root is exported, flagged (flags & 8). `sub_83233300` gives every
        // type-vector one root carrying the reserved name, and its matrix is the effect's
        // placement: the positioners write it (`sub_832325A8` → elem+80, `sub_83231C40` →
        // elem+16/+32) and every emitter hangs below it. A child's world matrix includes its own
        // motion (a spline node moves along its spline each tick), so it is not the placement.
        const bool is_root = name_hash == reserved_root;
        const uint32_t type_id = is_root ? 6u : ReadParticleEntityTypeId(base, type);

        // Still emitting: `+0xC4` counts down with -1 meaning forever, gated by the finite flag
        // at `+0x104` and the age/limit pair. Tells "spawning" from "running its last particles
        // out".
        const uint32_t remaining = read32(node + 0xC4u);
        const uint8_t finite = read8(node + 0x104u);
        const float age = readf(node + 0xFCu);
        const float limit = readf(node + 0x108u);
        const bool emitting = remaining != 0u && (finite == 0u || age <= limit);

        // Alive comes from the effect's stated working count in `0x8349E480`. When `sub_83234338`
        // stops or finishes an effect, it erases the record from `0x8349E480` and sets
        // `*(node_handle + 0x2C)` to `0xFFFFFFFF`, while the owning game component may keep the
        // instance and its `0x8349E490` nodes pooled with stale `remaining` / `live_particles`
        // fields. Any effect absent from the live registry is retired.
        const uint32_t node_handle = read32(node + 0xE8u);
        const uint32_t live_particles = read32(node + 0x154u);
        bool alive = emitting || live_particles != 0u;
        if (plausible(node_handle)) {
            if (read32(node_handle + 0x2Cu) == 0xFFFFFFFFu) {
                alive = false;
            } else if (registry_count != 0u) {
                alive = false;
                for (uint32_t probe = 0u; probe < registry_count; ++probe) {
                    const uint32_t at = (registry_hint + probe) % registry_count;
                    if (registry[at].instance == node_handle) {
                        registry_hint = at;
                        alive = registry[at].working != 0u;
                        break;
                    }
                }
            }
        }

        // `node + 0x20` is the node's OWN transform, row-major — rows 0..2 the basis, row 3 the
        // translation with w = 1.0. It is WORLD only on a root: `sub_832295F0` composes a node's
        // world as +0x70 (`sub_821DA198`) concatenated up the +240 parent chain, and a root has
        // no parent. The host places effects from roots alone. Row 3 aliases
        // `node + 0x50`, the destination of the position setter `sub_832325A8`
        // (`stvx128 v12, r31, r24` with r24 = 0x50, after `stb r28, 0xB0(r31)`). The
        // orientation setter `sub_83231C40` writes the quaternion at `+0x10` and rebuilds these
        // rows; the per-tick writers land here too.
        float world[16];
        bool stated_finite = true;
        for (int i = 0; i < 16; ++i) {
            world[i] = readf(node + 0x20u + static_cast<uint32_t>(i) * 4u);
            if (!std::isfinite(world[i])) {
                stated_finite = false;
            }
        }

        // A torn or uncomposed matrix is refused and counted. The vector is read without a
        // lock, so a node retired mid-walk can hand back anything; a never-composed node
        // carries an all-zero matrix, not an identity.
        if (!stated_finite || (world[0] == 0.0f && world[5] == 0.0f && world[10] == 0.0f)) {
            g_fx_refused_transforms.fetch_add(1u, std::memory_order_relaxed);
            continue;
        }

        // Rows 0..2 carry the node's scale; the caller wants a rotation, so hand over the rows
        // normalised and let it build the basis.
        uint32_t* row = out + written * kStride;
        row[0] = node;
        row[1] = name_hash;

        // THE EFFECT INSTANCE THIS NODE BELONGS TO. One effect is many nodes — `sub_83233300`
        // builds one instance per entity in each type-vector plus a synthetic root for each, so
        // the beehive's six (spline, emitter) vectors are eighteen instances of one effect. They
        // all share this handle, and without it the host draws the whole effect once per node.
        const uint32_t handle = node_handle;
        row[16] = handle;

        // Whether this node owns a guest render twin (`CParticleGroup` at `*(node+0x16C)` for
        // `0x82006718` billboard emitters, or `CTrailGroup` at `*(node+0x1CC)` for `0x82006CD0`
        // trail emitters), even on frames where the twin currently holds 0 active quads.
        const uint32_t node_vt = read32(node);
        bool has_guest_twin = false;
        if (node_vt == refii::guest::Address(0x82006718u)) {
            const uint32_t holder = read32(node + 0x16Cu);
            has_guest_twin = plausible(holder) &&
                             read32(holder) == refii::guest::Address(0x82001AC0u) &&
                             plausible(read32(holder + 8u));
        } else if (node_vt == refii::guest::Address(0x82006CD0u)) {
            const uint32_t trail = read32(node + 0x1CCu);
            has_guest_twin = plausible(trail) &&
                             read32(trail) == refii::guest::Address(0x82001AA8u);
        }

        row[2] = (alive || is_root ? 1u : 0u) | (read8(node + 0xB0u) != 0u ? 2u : 0u) |
                 (emitting ? 4u : 0u) | (is_root ? 8u : 0u) | (has_guest_twin ? 16u : 0u);
        row[3] = live_particles;

        // The effect's intensity, `handle+52`. `sub_832323E0(inst, f)` stores it there
        // (0x832323f8 `stfs f31, 0x34(r28)`) and into every element's +408 (0x832324a8), and
        // the spawn routines bake it into each new particle (`sub_82A03D58` 0x82a04090 →
        // `sub_83228588` 0x832285f4, particle+0x48). The breadcrumber drives it every tick.
        // 1.0 is the value an untouched instance carries.
        // The element's alpha/intensity lives at `node + 408` (`+0x198`, written by `SetAlpha`
        // `sub_832323E0` at 0x832324a8 on every Type 0 and Type 9 emitter element, whereas
        // `handle + 52` can remain 0 on pooled spell handles while elements carry non-zero alpha).
        float intensity = 1.0f;
        if (!is_root && (type_id == 0u || type_id == 9u)) {
            const float elem_alpha = readf(node + 408u);
            if (std::isfinite(elem_alpha)) {
                intensity = elem_alpha;
            }
        } else if (plausible(handle)) {
            const float stated = readf(handle + 52u);
            if (std::isfinite(stated)) {
                intensity = stated;
            }
        }

        {
            uint32_t bits;
            std::memcpy(&bits, &intensity, sizeof(bits));
            row[17] = bits;
        }

        // The element's scale, +412: `sub_83232290(inst, s)` (Lua `ParticleEmitter.SetScale`,
        // `ParticleAttacher.SetParticleScale`, the spell-cast action's `ParticleScale`) calls
        // every element's vtable slot 11 and `sub_83232108` reads it back from +412 — the
        // scalar the renderer folds into g_KeyFrameGlobalScales (binding 338). Meaningful on
        // a standard emitter only; the host decides by the node's type through the bank.
        // The fade-out: `KillParticleWithFadeOut` / `FadeParticles` set +192 = +196 =
        // frames (`sub_83228F00`), +196 counts down, and `sub_83228B48` multiplies the
        // element's intensity by +196/+192 (1.0 while +196 < 0, 0 when +192 == 0).
        {
            const float scale = readf(node + 412u);
            const int32_t fade_len = static_cast<int32_t>(read32(node + 192u));
            const int32_t fade_left = static_cast<int32_t>(read32(node + 196u));
            float fade = 1.0f;
            if (fade_left >= 0) {
                fade = fade_len > 0 ? static_cast<float>(fade_left) / static_cast<float>(fade_len) : 0.0f;
                if (fade > 1.0f) {
                    fade = 1.0f;
                }
            }

            uint32_t bits;
            std::memcpy(&bits, &scale, sizeof(bits));
            row[18] = bits;
            std::memcpy(&bits, &fade, sizeof(bits));
            row[19] = bits;
        }

        // WHOSE EFFECT. The handle (the ParticleSystemEntityInstance) keeps at +0x28 a pointer
        // to its emitter component's interface sub-object (`*(PSEI+40) = comp + 12`,
        // sub_82569C28 0x82569cbc; 0 when the emitter is spell-owned or disabled), whose first
        // word is the interface vtable the component's constructor writes (off_820C3158,
        // sub_82569900 0x82569948). The component (12 before it) keeps its owner entity at +4
        // (0x82569928) and, when the effect entity was attached to another, that target's
        // weak-ref box at +0x20 (sub_8256A0F0 0x8256a150). A vortex's orb reports the vortex
        // entity, a fireball's shot effects the shot entity.
        {
            uint32_t owner = 0u;
            uint32_t attached = 0u;
            if (plausible(handle)) {
                const uint32_t iface = read32(handle + 0x28u);
                if (plausible(iface) && iface >= 12u &&
                    read32(iface) == refii::guest::Address(0x820C3158u)) {
                    const uint32_t component = iface - 12u;
                    const uint32_t stated = read32(component + 4u);
                    owner = plausible(stated) ? stated : 0u;
                    const uint32_t box = read32(component + 0x20u);
                    const uint32_t target = plausible(box) ? read32(box) : 0u;
                    attached = plausible(target) ? target : 0u;
                }
            }
            row[20] = owner;
            row[21] = attached;
        }

        const float* translation = world + 12;
        for (int axis = 0; axis < 3; ++axis) {
            uint32_t bits;
            std::memcpy(&bits, translation + axis, sizeof(bits));
            row[4 + axis] = bits;
        }

        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) {
                uint32_t bits;
                std::memcpy(&bits, world + r * 4 + c, sizeof(bits));
                row[7 + r * 3 + c] = bits;
            }
        }

        // Runtime spline override endpoints on Type 5 `CParticleSplineNode` (`vtable 0x820058C8`).
        // Multi-dummy weapon effects (`CAugmentationComponent::Update` -> `sub_8256A508` ->
        // `sub_8256AB08` -> `sub_8322E858`) set the effect root's rotation to identity at
        // `point[0]` ("Weapon.Weapon.Hold") and write the world-space dummy endpoints into the
        // spline node's override vector (`begin` at +0x14C, `end` at +0x150, 16-byte stride) with
        // override flag `*(uint8_t*)(node + 0x158) = 1`. Non-root spline nodes do not use
        // `row[7..15]` (`Rows`) or `row[18]` (`Scale`), so we export 2..3 override world points in
        // `row[7..15]` and the point count in `row[18]`.
        if (!is_root && node_vt == refii::guest::Address(0x820058C8u) &&
            read8(node + 0x158u) != 0u) {
            const uint32_t sp_begin = read32(node + 0x14Cu);
            const uint32_t sp_end = read32(node + 0x150u);
            if (plausible(sp_begin) && sp_end > sp_begin && ((sp_end - sp_begin) & 0xFu) == 0u) {
                const uint32_t sp_count = (sp_end - sp_begin) >> 4;
                if (sp_count >= 2u && sp_count <= 3u) {
                    float pts[9] = {};
                    bool sp_finite = true;
                    for (uint32_t p = 0u; p < sp_count; ++p) {
                        for (uint32_t axis = 0u; axis < 3u; ++axis) {
                            const float v = readf(sp_begin + p * 16u + axis * 4u);
                            if (!std::isfinite(v)) {
                                sp_finite = false;
                            }
                            pts[p * 3u + axis] = v;
                        }
                    }
                    if (sp_finite) {
                        row[2] |= 32u;
                        const float count_f = static_cast<float>(sp_count);
                        uint32_t count_bits;
                        std::memcpy(&count_bits, &count_f, sizeof(count_bits));
                        row[18] = count_bits;
                        for (int i = 0; i < 9; ++i) {
                            uint32_t bits;
                            std::memcpy(&bits, &pts[i], sizeof(bits));
                            row[7 + i] = bits;
                        }
                    }
                }
            }
        }
        // Type 9 `CParticleSplineEmitterNode`: world-space beam/circle spline points live in the
        // vector at `node + 0x1C8..0x1CC` (16-byte stride) gated by `*(uint8_t*)(node + 0x1B0) != 0`
        // (`BeamSubmitMode2` 0x82A060A4..0x82A060DC). Flag 128 (`SplineStated`) tells the host to
        // fetch up to 512 points via `RefiiHostGetFxSpline(node, ...)`; flag 2048 (`TrailReset`)
        // tells the host the beam has no valid baseline yet.
        else if (!is_root && type_id == 9u) {
            const bool sp_active = read8(node + 0x1B0u) != 0u;
            const uint32_t sp_begin = read32(node + 0x1C8u);
            const uint32_t sp_end = read32(node + 0x1CCu);
            bool valid_beam_spline = false;
            if (sp_active && sp_begin >= 0x1000u && sp_end > sp_begin && ((sp_end - sp_begin) & 0xFu) == 0u) {
                const uint32_t sp_count = (sp_end - sp_begin) >> 4;
                if (sp_count >= 2u && sp_count <= 512u &&
                    (uint64_t)sp_begin + (uint64_t)sp_count * 16ull <= 0x100000000ull) {
                    const uint32_t last_off = (sp_count - 1u) * 16u;
                    valid_beam_spline =
                        std::isfinite(readf(sp_begin + 0u)) &&
                        std::isfinite(readf(sp_begin + 4u)) &&
                        std::isfinite(readf(sp_begin + 8u)) &&
                        std::isfinite(readf(sp_begin + last_off + 0u)) &&
                        std::isfinite(readf(sp_begin + last_off + 4u)) &&
                        std::isfinite(readf(sp_begin + last_off + 8u));
                }
            }
            row[2] |= valid_beam_spline ? 128u : 2048u;
        }
        // Current-frame double-trail (kind == 3) and guest-driven single-trail (kind == 2)
        // endpoints on Type 0 standard emitter nodes. Read the simulation ring directly at
        // `d = *(node + 0x168)` (`sys_def = *(node + 0x170)`, `sys_mode = *(sys_def + 0x20)`) so
        // trails at 10..15m (such as max-range magic circles) keep streaming and resetting even
        // when the guest culls off-frustum render twins, falling back to the render twin if needed.
        else if (!is_root) {
            uint32_t kind = 0u;
            uint32_t sim_d = 0u;
            uint32_t twin_n = 0xFFFFu;
            if (type_id == 0u) {
                const uint32_t sys_def = read32(node + 0x170u);
                const uint32_t sys_mode = plausible(sys_def) ? read32(sys_def + 0x20u) : 0u;
                if (sys_mode == 3u || sys_mode == 2u) {
                    kind = sys_mode;
                    sim_d = read32(node + 0x168u);
                }
            }

            if ((!plausible(sim_d) || kind == 0u) && twin_handles != 0u) {
                const uint32_t holder = read32(node + 0x16Cu);
                if (plausible(holder) && read32(holder) == holder_vtable) {
                    const uint32_t proxy = read32(holder + 24u);
                    const uint32_t twin_handle = plausible(proxy) ? read32(proxy + 4u) : twin_handles;
                    const uint32_t twin = twin_handle < twin_handles ? read32(twin_table + twin_handle * 4u) : 0u;
                    if (plausible(twin) && read32(twin + 16u) == twin_handle) {
                        const uint32_t twin_kind = read32(twin + 240u);
                        if (twin_kind == 3u || twin_kind == 2u) {
                            kind = twin_kind;
                            twin_n = read32(twin + 176u);
                            const uint32_t shared = read32(twin + 180u);
                            if (!plausible(sim_d) && plausible(shared)) {
                                sim_d = read32(shared + 4u);
                            }
                        }
                    }
                }
            }

            const auto valid_ring = [&](uint32_t d, uint32_t& ring, uint32_t& head, uint32_t& mask, uint32_t& cnt) {
                if (!plausible(d)) {
                    return false;
                }
                ring = read32(d + 8u);
                const uint32_t ring_end = read32(d + 12u);
                head = read32(d + 20u);
                mask = read32(d + 32u);
                cnt = read32(d + 36u);
                if (ring < 0x1000u || mask == 0u || mask > 4095u || ((mask & (mask + 1u)) != 0u)) {
                    return false;
                }
                const uint64_t expected_end = (uint64_t)ring + (uint64_t)(mask + 1u) * 32ull;
                if (expected_end > 0x100000000ull || (ring_end != 0u && (uint64_t)ring_end != expected_end)) {
                    return false;
                }
                return cnt <= (mask + 1u);
            };

            if (kind == 3u) {
                uint32_t ring = 0u, head = 0u, mask = 0u, cnt = 0u;
                if (!valid_ring(sim_d, ring, head, mask, cnt) || twin_n < 2u || cnt < 2u) {
                    row[2] |= 2048u;
                } else {
                    row[2] |= 4096u;
                    const uint32_t idx = (head + cnt - 1u) & mask;
                    const uint32_t entry = ring + idx * 32u;
                    const float ax = readf(entry + 0u), ay = readf(entry + 4u), az = readf(entry + 8u);
                    const float bx = readf(entry + 16u), by = readf(entry + 20u), bz = readf(entry + 24u);
                    const float dx = ax - bx, dy = ay - by, dz = az - bz;
                    const float span_sq = dx * dx + dy * dy + dz * dz;
                    if (std::isfinite(ax) && std::isfinite(ay) && std::isfinite(az) &&
                        std::isfinite(bx) && std::isfinite(by) && std::isfinite(bz) &&
                        span_sq > 1e-6f && span_sq <= 36.0f) {
                        row[2] |= 512u;
                        const float pts[6] = { ax, ay, az, bx, by, bz };
                        for (int k = 0; k < 6; ++k) {
                            uint32_t bits;
                            std::memcpy(&bits, &pts[k], sizeof(bits));
                            row[7 + k] = bits;
                        }
                        const uint32_t cnt16 = cnt > 0xFFFFu ? 0xFFFFu : cnt;
                        const uint32_t n16 = twin_n != 0xFFFFu ? (twin_n > 0xFFFFu ? 0xFFFFu : twin_n) : cnt16;
                        row[13] = (n16 << 16u) | cnt16;
                        row[14] = (head + cnt) & mask;
                        row[15] = read32(entry + 28u);
                    }
                }
            } else if (kind == 2u) {
                const float loc_sq = world[12] * world[12] + world[13] * world[13] + world[14] * world[14];
                if (loc_sq > 0.0625f) {
                    uint32_t ring = 0u, head = 0u, mask = 0u, cnt = 0u;
                    if (!valid_ring(sim_d, ring, head, mask, cnt) || twin_n < 2u || cnt < 2u) {
                        row[2] |= 2048u;
                    } else {
                        const uint32_t idx = (head + cnt - 1u) & mask;
                        const uint32_t entry = ring + idx * 32u;
                        const float ax = readf(entry + 0u), ay = readf(entry + 4u), az = readf(entry + 8u);
                        if (std::isfinite(ax) && std::isfinite(ay) && std::isfinite(az)) {
                            row[2] |= 1024u;
                            const float pts[3] = { ax, ay, az };
                            for (int k = 0; k < 3; ++k) {
                                uint32_t bits;
                                std::memcpy(&bits, &pts[k], sizeof(bits));
                                row[7 + k] = bits;
                            }
                            row[10] = read32(entry + 20u);
                            row[11] = 0u;
                            row[12] = 0u;
                            const uint32_t cnt16 = cnt > 0xFFFFu ? 0xFFFFu : cnt;
                            const uint32_t n16 = twin_n != 0xFFFFu ? (twin_n > 0xFFFFu ? 0xFFFFu : twin_n) : cnt16;
                            row[13] = (n16 << 16u) | cnt16;
                            row[14] = (head + cnt) & mask;
                            row[15] = 0u;
                        }
                    }
                }
            }
        }

        {
            const float white = 1.0f;
            uint32_t bits;
            std::memcpy(&bits, &white, sizeof(bits));
            for (uint32_t lane = 22u; lane < 26u; ++lane) {
                row[lane] = bits;
            }
            row[26] = 0u;
        }

        ++written;
    }

    return written;
}

/// The guest's live world clock — `GameTimeManager`'s day fraction.
///
/// THE SKY AND THE LIGHTING HAVE TO FOLLOW THIS OR THEY FOLLOW NOTHING. Everything the host
/// draws from the environment theme — sun direction, sun colour, ambient, fog, cloud tint — is
/// selected and blended by a day fraction, and until this existed that fraction was whatever
/// day-set entry happened to be chosen at level load, frozen for the lifetime of the region.
///
/// The object names itself: `sub_82515D98` serialises it under the class tag "GameTimeManager"
/// and passes each field beside its own literal — "Time" at +4, "DayCount" at +8, "TimeStep" at
/// +12. The Lua binding registrar agrees independently, `Timing.GetTimeOfDay` being
/// `sub_8288B918`: `return *(float *)(a1 + 4);`.
///
/// The chain was read out of `sub_8218EAF8`, the day-set theme evaluator, which is the caller
/// that hands the fraction to `sub_8218E2F0`:
///
///     8218eb24  lwz  r9, 0xC(r11)     ; root  -> X
///     8218eb34  lwz  r8, 0x58(r9)     ; X+0x58 -> Y
///     8218eb38  lwz  r7, 4(r8)        ; Y+4    -> W
///     8218eb3c  lwz  r6, 0x14(r7)     ; W+0x14 -> T = GameTimeManager
///     8218eb40  lfs  f31, 4(r6)       ; T+4    = the day fraction
///
/// USING THE SAME CHAIN AS THE THEME SELECTOR IS THE POINT. `sub_821DF670` reaches the same T
/// by the same offsets to aim the sun, so a host that blends its themes on this value
/// reproduces the guest's own selection rather than approximating it.
///
/// THE CHAIN IS RE-WALKED EVERY CALL, NEVER CACHED. `sub_8234F320` stores T into W+0x14 after
/// virtually destroying whatever was there, and W's destructor writes a NULL back at
/// `0x823523D8` — so the whole subtree is torn down and rebuilt across a world re-init, and a
/// cached pointer would be a use-after-free rather than a stale number.
///
/// Returns 0 when the guest is absent or the chain has not been built yet; the outputs are
/// untouched in that case. Every link is checked because the pointer walk crosses objects the
/// guest itself only builds at game-database init.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTimeOfDay(float* out_fraction, int32_t* out_day, int32_t* out_frozen,
                                                     int32_t* out_moon_phase) {
    // Moon phase: `Timing.GetMoonPhase` is `sub_825A1BF0`, `return *(u32*)(a1 + 56)` on this
    // same timing object. -1 means "not read"; values outside 0..7 are refused.
    if (out_moon_phase != nullptr) {
        *out_moon_phase = -1;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    if (root == 0) {
        return 0;
    }

    const uint32_t x = load_be32(root + 0x0Cu);
    if (x == 0) {
        return 0;
    }

    const uint32_t y = load_be32(x + 0x58u);
    if (y == 0) {
        return 0;
    }

    const uint32_t world = load_be32(y + 0x04u);
    if (world == 0) {
        return 0;
    }

    const uint32_t timing = load_be32(world + 0x14u);
    if (timing == 0) {
        return 0;
    }

    // THE VTABLE IS THE IDENTITY CHECK. 0x820BF8A8 is a one-slot vtable with exactly one
    // construction site in the whole image (0x8234F2C4), so nothing else can present it. Without
    // this a half-built chain hands back whatever the allocator left behind, and a garbage day
    // fraction reads as the sun being in the wrong place rather than as a bad pointer.
    if (load_be32(timing + 0x00u) != refii::guest::Address(0x820BF8A8u)) {
        return 0;
    }

    if (out_moon_phase != nullptr) {
        const uint32_t phase = load_be32(timing + 0x38u);
        if (phase <= 7u) {
            *out_moon_phase = static_cast<int32_t>(phase);
        }
    }

    const uint32_t raw_time = load_be32(timing + 0x04u);
    float fraction = 0.0f;
    std::memcpy(&fraction, &raw_time, sizeof(fraction));

    if (!(fraction >= 0.0f) || !(fraction < 1000.0f)) {
        // NaN or nonsense: the field is a plain float written without synchronisation, and a
        // torn or uninitialised read is better refused than propagated into the sun's angle.
        return 0;
    }

    // TRANSIENTLY >= 1.0 BY CONSTRUCTION. `sub_82298460` advances the fraction at 0x82298508 and
    // does not wrap it until 0x82298548, so a read landing in those sixteen instructions sees a
    // value past the end of the day. The guest's own wrap is a subtract of the truncated integer.
    fraction -= std::floor(fraction);

    if (out_fraction != nullptr) {
        *out_fraction = fraction;
    }

    if (out_day != nullptr) {
        *out_day = static_cast<int32_t>(load_be32(timing + 0x08u));
    }

    if (out_frozen != nullptr) {
        // T+0x24 is the count inside the "StopTimeTracker" at T+0x1C, and `sub_82298460` gates
        // the whole advance on it being zero.
        *out_frozen = load_be32(timing + 0x24u) != 0 ? 1 : 0;
    }

    return 1;
}

/// The guest's live precipitation — the four theme floats its rain and snow draws read.
///
/// THE GUEST HAS NO INDOOR TEST. Both draws gate on ONE value against `flt_820992D8`
/// (`3D 4C CC CD` = 0.05): snow on SnowSize (`0x821BDECC lvx128 v0, r6, 0x480` ...
/// `0x821BDF00 fcmpu cr6, f29, f0` / `bgt`), rain on RainDensity (`0x82280420 lvx128 v0, r6,
/// 0x4B0` ... `0x82280450 fcmpu cr6, f30, f0` / `ble` past the draw). No ceiling, portal,
/// raycast or room flag takes part. A building is dry because the painted day set under the
/// camera states no weather, the blend stack collapses and the record holds zeros — so a host
/// that draws weather from the LEVEL's day set alone rains indoors, and the fix is to read the
/// same blended record the guest reads.
///
/// The draws take a per-view render-thread copy of that record; the Lua native
/// `Weather::GetRainDensity` (`sub_8260DE88`) reads the game-side original directly, and its
/// chain is the one walked here:
///
///     8260de94  lwz  r11, dword_83496AB8   ; root
///     8260de98  lwz  r8, 0xC(r11)
///     8260de9c  lwz  r7, 0x58(r8)
///     8260dea0  lwz  r6, 4(r7)             ; World — the same W RefiiHostGetTimeOfDay reaches
///     8260dea4  lwz  r5, 0x50(r6)          ; environment theme manager
///     8260dea8  lwz  r4, 0xC(r5)           ; T, the blended record
///     8260deac  lvx128 v0, r4, 0x4B0       ; RainDensity.x
///
/// Theme values are float4 at a 16-byte stride with the value in .x; the descriptor order
/// SnowSize, SnowFallSpeed, RainSize, RainDensity is slots 71..74, i.e. T+0x480, +0x490,
/// +0x4A0, +0x4B0. The snow draw scales its sprite by the back-buffer width times +0x480
/// (`0x821BED00`) and puts +0x490 in the z of its velocity constant beside the wind
/// (`0x821BED50`), which is what fixes which of the two is the size.
///
/// Re-walked every call, never cached: the world subtree is torn down and rebuilt across a
/// world re-init. Returns 0 with the outputs untouched while the chain is unbuilt or a value
/// is not a sane float, so the caller keeps whatever it last had.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWeather(float* out_snow_size, float* out_snow_fall_speed,
                                                   float* out_rain_size, float* out_rain_density) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    if (root == 0) {
        return 0;
    }

    const uint32_t x = load_be32(root + 0x0Cu);
    if (x == 0) {
        return 0;
    }

    const uint32_t y = load_be32(x + 0x58u);
    if (y == 0) {
        return 0;
    }

    const uint32_t world = load_be32(y + 0x04u);
    if (world == 0) {
        return 0;
    }

    const uint32_t theme_manager = load_be32(world + 0x50u);
    if (theme_manager == 0) {
        return 0;
    }

    const uint32_t blended = load_be32(theme_manager + 0x0Cu);
    if (blended == 0) {
        return 0;
    }

    float values[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    static constexpr uint32_t kSlots[4] = {0x480u, 0x490u, 0x4A0u, 0x4B0u};
    for (int i = 0; i < 4; ++i) {
        const uint32_t raw = load_be32(blended + kSlots[i]);
        std::memcpy(&values[i], &raw, sizeof(values[i]));
        // The theme evaluator lerps authored 0..1-ish values; anything else is a torn read or
        // an unbuilt record, and is refused rather than turned into a blizzard.
        if (!(values[i] >= 0.0f) || !(values[i] <= 100.0f)) {
            return 0;
        }
    }

    if (out_snow_size != nullptr) {
        *out_snow_size = values[0];
    }
    if (out_snow_fall_speed != nullptr) {
        *out_snow_fall_speed = values[1];
    }
    if (out_rain_size != nullptr) {
        *out_rain_size = values[2];
    }
    if (out_rain_density != nullptr) {
        *out_rain_density = values[3];
    }

    return 1;
}

/// The environment-theme STACK — what the guest has pushed over the day set this tick, each
/// theme by name and at the weight the guest is blending it at.
///
/// Every full-screen effect the game has is a theme on this stack: Time Control pushes
/// `EnvThemeSlowTime2` (`sub_827EC460`: `sub_82549640(mgr, name, 0.5, -1.0, 0.5)` then
/// `sub_82549810(mgr, name, 1.0, 0.5)` — in over half a second, held, and `sub_827EBB70` fades
/// it out over the spell's last half second), spell charging pushes `EnvThemeChargingLo/Hi`,
/// the banshee `BansheeAttackHigh`. The guest's own compose (`sub_8218EAF8`) lerps each stated
/// slot of each theme over its base at these weights; the host does the same over ITS base, so
/// nothing changes while the stack is empty and the stated weights alone decide the effect.
///
/// The walk is the manager's intrusive list (`sub_82549640` inserts at `mgr+164`, the tick
/// `sub_822C0E08` iterates it): `S = *(mgr+168)` is the sentinel, `node = *S`, `node = *node`
/// until it returns to S; the record starts at `node+8` — `+0` a refcounted name string
/// (`sub_822651A0` copies the pointer, characters at `*name`), `+4` the theme block, `+8` the
/// current weight, `+12` the target (`sub_8254A260` 0x8254a260: `+8 = 0`, `+12 = target`,
/// `+16 = hold ticks`, `+24 = Δ/tick`, `+28 = fade Δ/tick`). The name's FNV-1 is the theme's
/// GDB record hash (`sub_825483B8` resolves it the same way), which is what the host extracts
/// the theme by. `mgr = *(world+0x50)`, the object RefiiHostGetWeather already walks.
///
/// Writes 3 words per entry — name hash, weight bits, target bits — up to `max_entries`.
/// Returns the entries written; 0 while the world is unbuilt or the list is empty.
static uint32_t ReadThemeList(uint32_t* out_words, uint32_t max_entries, uint32_t sentinel_offset);

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeStack(uint32_t* out_words, uint32_t max_entries) {
    return ReadThemeList(out_words, max_entries, 168u);
}

/// The SCRIPT blend list — `EnvironmentTheme.BlendToEnvironmentTheme(name, weight, seconds)`
/// (`sub_82549B80`: find-or-insert in the list at `mgr+148`, sentinel `*(mgr+152)`), the
/// quest-stated looks: the Cursed Snowglobe's `PastMonochromeNoon`, combat failure, XP, the
/// balverine poison, Westcliff's night. `sub_8218EAF8` folds it as one group after the day set
/// and painted layers and BEFORE the stack: group weight `clamp(mgr+160)` (`sub_822C0E08`'s sum
/// of the node weights), each theme at its own weight. The list is saved with the game
/// (`sub_8254A090`, chunk "ScriptOverride"), so it is read here rather than hooked at the call.
/// Same node layout and output as RefiiHostGetThemeStack.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeBlends(uint32_t* out_words, uint32_t max_entries) {
    return ReadThemeList(out_words, max_entries, 152u);
}

static uint32_t ReadThemeList(uint32_t* out_words, uint32_t max_entries, uint32_t sentinel_offset) {
    if (out_words == nullptr || max_entries == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    if (root == 0u) {
        return 0u;
    }
    const uint32_t x = load_be32(root + 0x0Cu);
    if (x == 0u) {
        return 0u;
    }
    const uint32_t y = load_be32(x + 0x58u);
    if (y == 0u) {
        return 0u;
    }
    const uint32_t world = load_be32(y + 0x04u);
    if (world == 0u) {
        return 0u;
    }
    const uint32_t theme_manager = load_be32(world + 0x50u);
    if (!plausible(theme_manager)) {
        return 0u;
    }

    const uint32_t sentinel = load_be32(theme_manager + sentinel_offset);
    if (!plausible(sentinel)) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t node = load_be32(sentinel);
    // A theme stack deeper than this is a torn read, not a stack.
    for (uint32_t guard = 0; guard < 32u && plausible(node) && node != sentinel; ++guard) {
        const uint32_t record = node + 8u;
        const uint32_t name = load_be32(record);
        const uint32_t characters = plausible(name) ? load_be32(name) : 0u;
        std::string text;
        if (plausible(characters)) {
            for (uint32_t i = 0; i < 96u; ++i) {
                char c = 0;
                CopyGuest(base, characters + i, &c, 1u);
                if (c == 0) {
                    break;
                }
                text.push_back(c);
            }
        }

        const uint32_t weight_bits = load_be32(record + 8u);
        const uint32_t target_bits = load_be32(record + 12u);
        float weight = 0.0f;
        std::memcpy(&weight, &weight_bits, sizeof(weight));
        if (!text.empty() && std::isfinite(weight) && written < max_entries) {
            out_words[written * 3u] = RefiiFnv1(text);
            out_words[(written * 3u) + 1u] = weight_bits;
            out_words[(written * 3u) + 2u] = target_bits;
            ++written;
        }

        node = load_be32(node);
    }

    return written;
}

/// The render-frame fence the LEVEL LOAD waits on — state 7 of `CmdLevelChange_Update`.
///
/// `CmdLevelChange_Update` (0x823D5AA8) is a nine-state polled machine on `cmd+44`, and
/// every one of its gates fails SILENTLY: it returns each tick, logs nothing, and the load
/// simply never advances. State 7 is the one a host with no GPU has to answer for:
///
///     v83 = mode->vtbl[+16](dword_83496AB8);          /*0x823D65C8*/  -> *(*(a1+12)+104)
///     if (!sub_82387B38(*(v83 + 4))) { *(cmd+44) = 8; }
///
///     int sub_82387B38(int a1) {                       // under the render critical section
///       if (*(double *)(a1 + 152) <= *(double *)(a1 + 176) || *(BYTE *)(a1 + 600))
///         return 1;                                    // BUSY
///       return 0;                                      // idle — the load may proceed
///     }
///
/// A submitted-vs-retired pair plus a busy byte. With nothing retiring frames it answers
/// BUSY forever and the level change parks in state 7. This reports the three values so a
/// run can say which of them is holding, instead of the load being invisible.
///
/// Reads the chain rather than calling the guest's virtual: slot +16 is literally
/// `return *(*(a1 + 12) + 104);`. Fills manager, submitted, DEADLINE, busy, and five
/// discriminator words. Returns 1 when the chain resolved.
///
/// `+176` IS NOT "RETIRED". It has two writers only — the ctor, and `sub_82388B48` under the
/// `+56` critical section — and it is a render DEADLINE BARRIER: "a frame has been requested
/// for time T, stay busy until the render thread passes T". `+152` is the render thread's
/// current frame time. So `+176` sitting still while `+152` climbs past it is the NORMAL
/// resting state, not a stall; it means nobody has posted a new deadline.
REFII_HOST_ABI_EXPORT int RefiiHostGetRenderFence(uint32_t* out_manager, double* out_submitted,
                            double* out_retired, uint32_t* out_busy,
                            uint32_t* out_arms) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto read_double = [base](uint32_t at) {
        const uint64_t raw = GuestWord64At(base, at);
        double value = 0.0;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t mode = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }

    const uint32_t owner = read32(mode + 12u);
    if (!plausible(owner)) {
        return 0;
    }

    const uint32_t frame = read32(owner + 104u);
    if (!plausible(frame)) {
        return 0;
    }

    // sub_82387B38's argument is *(v83 + 4), not v83.
    const uint32_t manager = read32(frame + 4u);
    if (!plausible(manager)) {
        return 0;
    }

    if (out_manager != nullptr) *out_manager = manager;
    if (out_submitted != nullptr) *out_submitted = read_double(manager + 152u);
    if (out_retired != nullptr) *out_retired = read_double(manager + 176u);
    if (out_busy != nullptr) *out_busy = GuestByteAt(base, manager + 600u);

    // WHICH ARM is holding the load, if any. Reads only — nothing here writes guest memory.
    //
    // The object is the "3D Engine" (0x260 bytes, ctor sub_823877E8), and sub_82387B38 is its
    // own `IsEnginePreLoading` — the name comes from the guest's Lua registration in
    // sub_82387DC0, not from a guess. So state 7 of the level change is literally
    // "while (IsEnginePreLoading()) wait".
    //
    // `+600` carries NO state of its own: sub_82388380 re-copies `byte_83496E20` into it every
    // render-thread iteration (`lbz r30, byte_83496E20` / `stb r30, 0x258(r31)`). That global
    // has exactly ONE writer, sub_82181B68, reachable only from the 3D Engine thread, and it
    // is decided by three arms — none of which touches a present, flip, fence or render
    // callback, so nothing in the producer depends on the GPU.
    //
    // Which arm is holding takes a discriminator, and these five values are it:
    //   view/vector cursor `engine+116` and `engine+124` — both -1 means nothing is
    //     recomputing the flag and it is a STALE 1 (sub_82388040 sets +124 to -1 during the
    //     level load; only a re-submitted view clears it),
    //   `0x83496E34` in 1..4 means the resource-preload arm is holding,
    //   `0x8349FB5C` non-zero means the streamed-texture-residency arm is holding,
    //   `0x83496E20` should equal +600, and a disagreement means the mirror itself is stale.
    //
    // The third arm walks the scene container and TIMES ITSELF OUT, so it cannot hold this
    // forever and is not worth reporting.
    if (out_arms != nullptr) {
        out_arms[0] = read32(manager + 116u);
        out_arms[1] = read32(manager + 124u);
        out_arms[2] = GuestWordAt(base, refii::guest::Address(0x83496E34u));
        out_arms[3] = GuestWordAt(base, refii::guest::Address(0x8349FB5Cu));
        out_arms[4] = GuestByteAt(base, refii::guest::Address(0x83496E20u));
    }

    return 1;
}

/// "The world is loaded and ready", which is NOT the loading screen coming down.
///
/// A byte at `game + 0x105`, where `game = *(u32*)(0x83496AB8 + 0x0C)`. `CmdLevelChange_Update`
/// (0x823D5AA8) clears it when the real load begins (state 3, 0x823D60C8) and sets it when the
/// nine-state machine finishes (state 9, 0x823D6704); the game object's constructor
/// (`sub_82310508`) starts it at 1 — "ready" is the BOOT state, before any world exists.
///
/// It IS read by the guest, at 0x82276E84 in the game tick `sub_82276D10`, which gates work on
/// `+0x104 && +0x105`. An earlier version of this comment claimed it was publish-only with no
/// reader in the image; that was wrong. Polling is still safe because we only ever READ it —
/// but nothing may ever WRITE it from the host, which "publish-only" invited.
///
/// USE IT AS A RISING EDGE ONLY. It is cleared in state 3 AFTER the blocking load returns,
/// while the entity teardown happened back in state 2 — so its falling edge is far too late to
/// free a scene by, and there is a window where it reads 1 with the old entities already gone.
/// `RefiiHostGetLevelChange` carries the command's own state word for that.
///
/// Distinct from `game + 0x104`, "a level change is pending", and from the Lua `IsLevelLoading`,
/// which compares the front gameflow command's type id to 4 and is therefore FALSE during a
/// load-from-save (type 5).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsReady(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    uint32_t mode = 0;
    mode = __builtin_bswap32(GuestWordAt(base, refii::guest::Address(0x83496AB8u)));
    mode = __builtin_bswap32(mode);
    if (mode < 0x10000u || mode >= 0xC0000000u) {
        return 0;
    }

    const uint32_t game = GuestWordAt(base, mode + 0x0Cu);
    if (game < 0x10000u || game >= 0xC0000000u) {
        return 0;
    }

    return GuestByteAt(base, game + refii::guest::GameField(0x105u)) != 0 ? 1u : 0u;
}

/// The 3D Engine object — the thing state 7's fence is asked about.
///
/// `sub_823881C0` names this thread `"3D Engine"` at 0x823881D8, and the object is 608 bytes
/// (ctor `sub_823877E8`). The chain is a pure memory walk; the guest reaches it through a
/// virtual call whose body `sub_8220C1A0` is just `return *(*(a1 + 12) + 104);`, so nothing has
/// to be CALLED to follow it:
///
///     g      = *(0x83496AB8)
///     Game   = *(g    + 0x0C)
///     P      = *(Game + 0x68)      /* the 8-byte holder, filled at 0x82310CAC */
///     engine = *(P    + 0x04)      /* 823D65CC: lwz r3, 4(r3) off the CALL'S RESULT */
static uint32_t engine_object(const uint8_t* base) {
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return (at >= 0x10000u && at < 0xC0000000u) ? u32(at + offset) : 0u;
    };

    uint32_t at = u32(refii::guest::Address(0x83496AB8u));
    at = step(at, 0x0Cu);
    at = step(at, 0x68u);
    at = step(at, 0x04u);
    return (at >= 0x10000u && at < 0xC0000000u) ? at : 0;
}

/// Arms or releases the render fence state 7 waits on, by writing the engine's `+176`.
///
/// THE FENCE, AND WHY THIS IS FOLLOWING THE CONTRACT RATHER THAN POKING. `sub_82387B38` is
/// bound to Lua by the shipped title as **"IsEnginePreLoading"**, with its blocking twin
/// `sub_82387CE0` as **"WaitForEngineToPreLoad"**. State 7 of `CmdLevelChange_Update` polls it
/// (0x823D65D0) and branches to the default case — which does nothing at all — while it is
/// nonzero. The title is asking its renderer whether the renderer has caught up. Godot IS the
/// renderer, and the honest answer while a region is still being built is "no".
///
/// The predicate is `engine+152 <= engine+176 || engine+600`, and it means "the engine's render
/// clock has not yet passed the last posted hand-off". `+152` is that clock, rewritten by the
/// 3D Engine thread every iteration (0x823885A0), and `+600` mirrors `byte_83496E20` the same
/// way (0x823885A4) — writing either is pointless. **`+176` is the one field with a single
/// writer (`sub_82388B48` @0x82388B80) and a single reader (the fence itself)**, and stamping
/// it then waiting is the guest's OWN idiom: `sub_821987B8` case 9 does exactly that at
/// 0x821988F0 / 0x8219892C.
///
/// POLARITY IS THE ONE THING THAT MUST NOT BE WRONG — 1 means BUSY. Held three ways: the spin
/// is `while (sub_82387B38(a1)) SleepEx(0,1)`, the call site does `bne -> default` on nonzero,
/// and the Lua name is a question whose "yes" is 1. Inverted, this deadlocks the title.
///
/// RE-ASSERTED EVERY TICK, because the world's own Update (`sub_8231D288`) stamps `+176` with
/// the real hand-off time as it drains the pending `.engine_level` — a hold written once would
/// be wiped the first time that ran.
///
/// RELEASE WRITES 0.0, not the current clock: the comparison is `<=`, so writing `+152` itself
/// would leave the fence closed.
static void apply_world_build_hold(const uint8_t* base);

/// ON by default. `REFII_HOLD_GUEST_FOR_WORLD=0` disables it if it ever misbehaves.
///
/// CAN HOLDING THIS WEDGE THE GUEST? No, and the reasoning is worth keeping because the shape
/// of it says otherwise. `sub_821987B8` runs from the main loop at 0x822771F0 every tick and
/// BEFORE the state machine at 0x82277264, and two of its cases spin on this same predicate —
/// case 9 calls `WaitForEngineToPreLoad` (0x8219892C) after RESTAMPING `+176` with the current
/// time at 0x821988F0, then waits for the render clock `+152` to pass that stamp. Since `+152`
/// comes from `sub_82B6FCE8`, which derives from D3D frame sync, that spin looks unbreakable
/// with no GPU.
///
/// IT IS NOT: the 3D Engine thread keeps its clock running headless — the value climbs steadily
/// in `[guest-fence] submitted=` — so the spin drains on its own.
///
/// What remains is bounded and intended: while WE hold, the spin waits for our release, and we
/// release on every path out of a build — success, failure and teardown. The wait is therefore
/// never longer than our own world build, which is the entire point of the mechanism.
static bool world_build_hold_enabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("REFII_HOLD_GUEST_FOR_WORLD");
        return value == nullptr || value[0] != '0';
    }();
    return enabled;
}

static void apply_world_build_hold(const uint8_t* base) {
    if (!world_build_hold_enabled()) {
        return;
    }

    const uint32_t engine = engine_object(base);
    if (engine == 0) {
        return;
    }

    // 1e30 seconds is far beyond any render clock the title will ever reach, and finite — an
    // infinity here would make the guest's own comparison behave differently under fcmpu.
    const double value = refii_bridge_world_build_pending() != 0 ? 1e30 : 0.0;

    uint64_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    bits = __builtin_bswap64(bits);
    StoreGuest(const_cast<uint8_t*>(base), engine + 0xB0u, &bits, sizeof(bits));
}

/// THE ONE SIGNAL THAT COVERS BOTH LOAD PATHS: the world is activated and populated.
///
/// `World + 0xA8`, and it is what `game+0x105` only pretends to be. Set by the activator
/// `sub_823640E0` — `mr r25, r3` then `stb r10(=1), 0xA8(r25)` at **0x8236410C** — and cleared
/// by the world teardown `sub_82364698` at 0x82364ADC, which `World_LoadLevel_Execute` calls
/// through `sub_8231CB90` at the start of every load.
///
/// BOTH PATHS REACH IT, which is the whole point. `sub_82350F80` is the only caller of the
/// activator and has exactly two callers of its own: `CmdLevelChange_Update` state 5
/// (0x823D6210) for travel, and `CmdLoadSaveGame_Update` state 2 (0x823D7920) for a
/// load-from-save. So one poll answers "is the world up" however the guest got there — no
/// per-mechanism special case, and nothing to walk.
///
/// WHY NOT `game+0x105`: it IS written on both paths, but only at the TERMINAL state, behind
/// the render fence and the loading-screen gates (states 4/5 of the save command, 7/8 of the
/// travel one). A Godot-hosted front end may never satisfy those, so it reads false while the
/// world is in fact fully loaded and its entities activated — the load command has finished its
/// work and parked. It also BOOTS AS 1 (game ctor, 0x82310644), so it is only meaningful once
/// it has been seen to go 0.
///
/// WHY NOT `LevelInstance + 0xE0`: it looks ideal — written 1 on both paths right after
/// activation — but it is a CONSUMED one-shot. `sub_8231D288` reads it at 0x8231D2BC and clears
/// it at 0x8231D3D0, conditionally on an entrance string being non-empty. Polling it races the
/// consumer and its lifetime depends on level data.
///
/// OFFSETS, EACH AGAINST ITS OWN BASE, and note this is NOT the object `world_manager()`
/// returns — that one takes `+0x08` then `+0x04` from the same parent, this takes `+0x00`:
///
///     g     = *(0x83496AB8)
///     game  = *(g    + 0x0C)
///     A     = *(game + 0x58)
///     B     = *(A    + 0x04)      /* what sub_82350F80 is called with */
///     World = *(B    + 0x00)      /* what it passes on as sub_823640E0's arg0 */
///     flag  =  (uint8)World[0xA8]
///
/// Every hop is null before `Game_InitDatabases` has run, so each is checked. Returns 0 when
/// the world is down, mid-load, or does not exist yet — all three mean the same thing to a
/// host: do not mount against it.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsActive(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    uint32_t at = u32(refii::guest::Address(0x83496AB8u));
    at = step(at, 0x0Cu);
    at = step(at, 0x58u);
    at = step(at, 0x04u);
    at = step(at, 0x00u);
    if (!plausible(at)) {
        return 0;
    }

    return GuestByteAt(base, at + 0xA8u) != 0 ? 1u : 0u;
}

/// Describes the gameflow command queue as text, for when a walk of it comes back empty.
///
/// PRINT THE QUEUE RATHER THAN GUESS AT IT AGAIN. When `RefiiHostGetLevelChange` finds nothing
/// the question is always the same — wrong chain, wrong link offset, or genuinely no such
/// command — and each answer costs a run to test. Dumping the nodes and their command vtables
/// settles all three at once, which is how the load-from-save path was found to queue no
/// level-change command at all.
///
/// Writes `<count> node(s): vtable@node ...` and never fails loudly — an empty or unreadable
/// queue is itself the answer.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostDescribeGameFlowQueue(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }

    out[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    const uint32_t p = step(u32(refii::guest::Address(0x83496AB8u)), 0x0Cu);
    const uint32_t holder = step(p, 0x80u);
    const uint32_t gameflow = step(holder, 0x04u);

    std::string text;
    char scratch[128];

    std::snprintf(scratch, sizeof(scratch), "p=0x%08X holder=0x%08X gf=0x%08X", p, holder,
                  gameflow);
    text += scratch;

    if (plausible(gameflow)) {
        const uint32_t sentinel = u32(gameflow + 0x04u);
        std::snprintf(scratch, sizeof(scratch), " sentinel=0x%08X", sentinel);
        text += scratch;

        // BOTH LINK DIRECTIONS, because which one walks this ring is exactly what is in doubt.
        // A doubly linked node makes one of them the way forward and the other the way back;
        // printing both costs nothing and settles it in one run instead of one run per guess.
        for (int direction = 0; direction < 2; ++direction) {
            const uint32_t link = direction == 0 ? 0x04u : 0x00u;
            std::snprintf(scratch, sizeof(scratch), " | +%u:", link);
            text += scratch;

            uint32_t node = plausible(sentinel) ? u32(sentinel + link) : 0u;
            for (uint32_t i = 0; i < 8u; ++i) {
                if (!plausible(node) || node == sentinel) {
                    text += node == sentinel ? " <end>" : " <bad>";
                    break;
                }

                const uint32_t command = u32(node + 0x08u);
                std::snprintf(scratch, sizeof(scratch), " [%08X vt=%08X]", command,
                              plausible(command) ? u32(command) : 0u);
                text += scratch;

                if (text.size() + 64u >= capacity) {
                    break;
                }

                node = u32(node + link);
            }
        }
    }

    const size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
    std::memcpy(out, text.data(), copied);
    out[copied] = '\0';
    return 1;
}

/// Holds the guest in its own loading screen while Godot builds the region.
///
/// Set it when a build starts and CLEAR IT WHEN THE REGION MOUNTS — including on the failure
/// path. A hold that is never released parks the guest at state 7 forever with its loading
/// music playing, which looks exactly like a hung load because that is what it is.
///
/// SAFE TO HOLD FOR TENS OF SECONDS, checked rather than hoped: no state of the nine reads the
/// command's start timestamp `cmd+56` and none contains an elapsed-time comparison, an assert
/// or a force-advance; the 3D Engine thread's only cross-thread wait is a one-shot display-init
/// gate; and the guest starts one of five random loading tracks in state 1 and fades it out over
/// 1000 ms in state 9 — an arbitrarily long load is the designed case. Audio keeps running
/// because state 7 is a poll on a thread that keeps ticking and the mixer is not one of the
/// title's own threads.
REFII_HOST_ABI_EXPORT void RefiiHostSetWorldBuildPending(uint32_t pending) {
    refii_bridge_set_world_build_pending(pending != 0u ? 1 : 0);

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base != nullptr) {
        apply_world_build_hold(base);
    }
}

/// THE LEVEL CHANGE ITSELF, read off the command the guest queued for it.
///
/// `RefiiHostWorldIsReady` is a good "the world has settled" latch and a BAD "a load has
/// started" one, and the difference is where a host frees its scene. The entity teardown
/// happens in **state 2** — `sub_823D6798` @0x823D5F9C -> `sub_8231CB90` @0x823D6B1C ->
/// `sub_82364698`, which walks every entity, tests bit 0x08 of `entity+0x90` ("destroy on
/// level change"), calls DestroyEntity and then drains the pending list through
/// `sub_822D46C8` — after which the old entities are unlinked and freed. But `game+0x105` is
/// not cleared until **state 3**, at 0x823D60C8, AFTER the blocking load has already returned.
///
/// So there is a window spanning state 2 and all of state 3 where the ready byte still reads 1
/// and the entities it implies are gone. A host that frees on the falling edge of that byte
/// keeps drawing a region whose guest-side entities no longer exist, and reads freed memory to
/// do it. The command's own state word is the honest signal.
///
/// THE QUEUE WALK. Verified from `World_RequestLevelChange` (0x82350B20), which emits the whole
/// chain inline before it queues:
///
///     v19 = *(p + 0x80)                  /* p = *( *(0x83496AB8) + 0x0C ), same base as above */
///     gf  = *(v19 + 4)                   /* the object GameFlow_QueueCommand is called with */
///     sentinel = *(gf + 4)
///     node     = *(sentinel + 4)         /* empty when node == sentinel */
///     cmd      = *(node + 8)
///
/// NOTE THE BASE: the gameflow lives at `p + 0x80`, NOT at `*(0x83496AB8) + 0x80`. One extra
/// hop, and quoting the offset against the wrong base is how this project has shipped a
/// plausible-looking wrong answer before.
///
/// COMMAND FIELDS, from the ctor `sub_823D5620` decompiled in full rather than taken on report:
///
///     *(cmd + 0x00) = &vtbl_CmdLevelChange (0x820AECE8)   <- the identity test
///      cmd + 0x14   = world name     Rep*   /*0x823D5664*/
///      cmd + 0x18   = level name     Rep*   /*0x823D5670*/
///      cmd + 0x1C   = entrance name  Rep*   /*0x823D567C*/
///      cmd + 0x24   = travel type           /*0x823D56B0*/  1 new game, 2 door, 5 teleporter,
///                                                           3/4 fast travel, 16 reload in place
///      cmd + 0x2C   = state, starts at 1    /*0x823D56BC*/  1..9, never 0
///      cmd + 0x32   = fullLoad, starts at 1 /*0x823D56C4*/
///
/// `cmd+0x1C` is the entrance and not a guess: state 5 copies it straight into the live level
/// at 0x823D61EC — `sub_822651A0(*(u32*)(level+4) + 176, cmd + 28)` — and Level+176 is the slot
/// the level ctor seeds with "PlayerStart".
///
/// FULLLOAD IS LOAD-BEARING FOR THE HOST. When `cmd+0x32` is 0 the guest does NOT tear anything
/// down: state 3 takes the `sub_823170C8` branch instead of the blocking load, and
/// `sub_82364698` is not reachable from it at all. That is a reposition inside the RESIDENT
/// level, so a host must leave its scene standing — freeing it there would rebuild a region the
/// guest never unloaded.
///
/// We only ever read a vtable pointer and a few words, so this is polling, not participation.
/// Returns 1 when a level change is at the head of the queue, 0 otherwise.
///
/// Forward-declared because the shared string reader is defined with the other level accessors
/// further down, and this belongs beside `RefiiHostWorldIsReady` — the call it exists to correct.
static bool read_guest_name(const uint8_t* base, uint32_t global, char* out, uint32_t capacity);

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLevelChange(char* world, char* level, char* entrance, uint32_t capacity,
                                 uint32_t* out_state, uint32_t* out_travel_type,
                                 uint32_t* out_full_load) {
    if (out_state != nullptr) *out_state = 0u;
    if (out_travel_type != nullptr) *out_travel_type = 0u;
    if (out_full_load != nullptr) *out_full_load = 0u;

    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    const uint32_t mode = u32(refii::guest::Address(0x83496AB8u));
    const uint32_t p = step(mode, 0x0Cu);
    const uint32_t gameflow = step(step(p, 0x80u), 0x04u);
    if (!plausible(gameflow)) {
        return 0;
    }

    const uint32_t sentinel = u32(gameflow + 0x04u);
    if (!plausible(sentinel)) {
        return 0;
    }

    // WALK THE QUEUE — the level change is not always at the head.
    //
    // Checking only the first node reported "no command in flight" while a level change was
    // very much in flight, and the host then waited for a state that could never arrive.
    // `World_RequestLevelChange` itself queues a SECOND command in front of it: at 0x82350C94
    // it allocates a 24-byte object with vtable `off_820AECA0` and queues that (0x82350CC8)
    // before constructing and queueing the level change (0x82350CF8 / 0x82350D08). Whatever
    // sits at the head is therefore not the command we are looking for.
    //
    // The list is circular through the sentinel, so a node equal to it ends the walk. The step
    // cap is a corruption guard, not a queue-length assumption: a torn `next` pointer would
    // otherwise spin here forever, inside a call the host makes every frame.
    constexpr uint32_t kMaxQueueWalk = 64u;

    uint32_t command = 0;
    uint32_t node = u32(sentinel + 0x04u);
    for (uint32_t step_count = 0; step_count < kMaxQueueWalk; ++step_count) {
        if (!plausible(node) || node == sentinel) {
            break;
        }

        // ONLY CmdLevelChange. 0x820AED0C IS NOT A SECOND FLAVOUR OF IT.
        //
        // The queue dump shows a load-from-save carrying `[vt=820AED0C] [vt=8200247C]
        // [vt=82002914]` and no CmdLevelChange at all, and it is tempting to accept 0x820AED0C
        // too since it sits in the same contiguous run of command vtables. It is NOT the same
        // shape: `Game_InitDatabases` allocates that object with `li r3, 0xC` at 0x823114C8 —
        // **twelve bytes**, holding a vtable at +0, `sub_823DA258` at +4 and one word at +8.
        // Reading `+0x2C` for a state or `+0x14` for a destination off it is thirty bytes past
        // the end of the allocation, and would return whatever the allocator had there.
        //
        // An adjacent vtable is not evidence of a shared layout. The initial load simply has no
        // level-change command to find, and the caller's own fallbacks cover that.
        const uint32_t candidate = u32(node + 0x08u);
        if (plausible(candidate) && u32(candidate) == refii::guest::Address(0x820AECE8u)) {
            command = candidate;
            break;
        }

        // THROUGH +4, which is the link the guest itself follows. `GameFlow_QueueCommand`
        // reaches its node as `*(*(gf + 4) + 4)` and ends on `node == sentinel`, so +4 is the
        // pointer that walks this ring; +0 would be the other direction of a doubly-linked
        // node and is not what any reader in the image uses. The ring is circular either way,
        // so following the guest's own link visits every command.
        node = u32(node + 0x04u);
    }

    if (command == 0) {
        return 0;  // no level change anywhere in the queue
    }

    if (out_state != nullptr) *out_state = u32(command + 0x2Cu);
    if (out_travel_type != nullptr) *out_travel_type = u32(command + 0x24u);
    if (out_full_load != nullptr) *out_full_load = GuestByteAt(base, command + 0x32u) != 0 ? 1u : 0u;

    read_guest_name(base, command + 0x14u, world, capacity);
    read_guest_name(base, command + 0x1Cu, entrance, capacity);
    read_guest_name(base, command + 0x18u, level, capacity);
    return 1;
}

/// How far the guest has got through starting a game, as four globals it maintains itself.
///
/// `Game_InitDatabases` (0x823108B8) is where a New Game actually begins, and it is silent:
/// it neither logs nor sets any progress flag until its very last statement. These four
/// bracket it:
///
///   out[0] `g_GDBManager`   0x83496DA0 — the game-database manager. The front end's Leave
///          NULLS it (`GDBManager_UnloadFilesByTagAndRebuild` then `g_GDBManager = 0` at
///          0x82319BC8) and Game_InitDatabases rebuilds it. Zero after the handover means
///          the databases have been torn down and not yet come back.
///   out[1] `byte_83496A5E`  — set 1 at 0x82311648, the LAST statement of the function.
///   out[2] `dword_83496AB8` — the game mode, built by sub_823130D8.
///   out[3] `dword_83496AC0` — the front-end mode; its own destructor zeroes it at
///          0x8231AA54, so 0 here means the front end is genuinely gone rather than hidden.
///
/// Together they say which side of the handover we are on and whether the databases came
/// back — without which "the guest went quiet" cannot be told apart from "the guest is
/// loading".
REFII_HOST_ABI_EXPORT void RefiiHostGetInitMarkers(uint32_t* out) {
    if (out == nullptr) {
        return;
    }

    for (int i = 0; i < 4; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    out[0] = GuestWordAt(base, refii::guest::Address(0x83496DA0u));
    out[1] = GuestByteAt(base, refii::guest::Address(0x83496A5Eu));
    out[2] = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    out[3] = GuestWordAt(base, refii::guest::Address(0x83496AC0u));
}

/// Every LIVE entity's world transform and model, for Godot to draw.
///
/// This is the plan's phase 1 — the state feed — and it is a PURE MEMORY READ. No guest
/// function is called, nothing is written, and it is safe to run from Godot's thread while
/// the guest simulates: a torn read costs one frame of one entity, never a crash.
///
/// THE LIST. `0x83496B28` holds a POINTER to the manager, not the header — that is the first
/// trap, and reading it as the header walks garbage. The header is the 12 bytes at mgr+0x08:
/// front +0x08, back +0x0C, live count +0x10. The node is embedded at entity+0x08 as
/// {next, prev, owner}, so the owner back-pointer is node+0x08. Verified against the guest's
/// own walk, sub_8238D3A0: `lwz r10, dword_83496B28` / `lwz r11, 8(r10)` / `lwz r11, 8(r6)`.
///
/// WHICH ENTITIES COUNT. `entity+0x90 & 0x40` is the LIVE bit — set at construction
/// (sub_8238B320 stores 0xCC) and cleared only by the despawn sub_8238D828 — and it is the
/// game's own universal validity guard, tested at 3714 sites against one or two for every
/// other bit. Membership is not drawability: the renderable (component 3) carries SetAsDrawable
/// at +197 and an alpha pair at +112/+116, stated to Godot as the appearance words below — the
/// walk reports the entity either way. The second half of the filter is `entity+0x7C != 0`,
/// the same thing despawn gates its deregistration on.
///
/// THE TRANSFORM. entity+0x7C is the placement component ("EC-2"), whose slot 0x40 returns a
/// position and 0x44 an orientation, both sret — caller's buffer in r3, `this` in r4, 16 bytes
/// written (sub_8222CD80 is the whole idiom: `li r11,0x20; lvx128 v0,r4,r11; stvx128 v0,r0,r3`).
/// We do NOT call them. There are exactly twelve EC-2 vtables and between them only four
/// position bodies and three orientation bodies, so the read is a switch on the vtable
/// pointer — a `.rdata` constant, stable across runs.
///
/// An UNKNOWN vtable is reported, never guessed. Only three delegate classes were enumerated
/// and the writer of EC2+0x20 was never found, so the set cannot be proven closed, and
/// defaulting to some plausible offset would put entities at silently wrong positions.
/// Unknown means skipped and counted.
///
/// THE MODEL. Carried by a graphic-appearance component, not by the entity: id 4 static mesh
/// at +0x18, id 6 animated at +0x20, ids 10 and 11 at +0x14. The value is a resource-name
/// hash, written once at load. Id 5 (multiple-mesh) holds a vector and is skipped.
///
/// Fills `stride` dwords per entity; returns how many were written.
///
/// `out_unknown_vtables` receives {vtable, entity count} PAIRS and the number of pairs comes
/// back in `out_unknown_vtable_count`. The count is explicit rather than a zero terminator
/// because zero is a value the walk can legitimately record — a placement component whose
/// vtable word reads 0 is the under-construction case, and terminating on it buries every
/// later entry. The per-vtable tally is what makes the list actionable: one address standing
/// for 300 entities is a different decision from thirty standing for a dozen each.
/// EVERY RIG ANIMATES. The guest's per-frame animation loop `sub_8220D1B8` skips an entity
/// whose visibility predicate `sub_822142E8(comp3, radius)` fails, and that predicate is a
/// cone test against the guest's own 4:3 camera (`sub_82294468`, `g_WidescreenMode` = 0),
/// refreshed every fourth frame. Godot draws a wider view, so characters at its edges stood
/// in their init pose — the skeleton reference copied LOCAL at component init — and walked
/// about in it. The predicate's first line is `if (*(BYTE*)(comp3 + 202)) return 1;`
/// (0x82214308): the shipped `SetAsAlwaysInView` script property. Set on every live entity
/// that has the component; the guest then evaluates and converts each frame.
uint32_t g_always_in_view_set = 0;
static void KeepAnimatedInView(uint8_t* base, uint32_t entity) {
    constexpr uint8_t kVisibilityId = 3u;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (((read32(entity + 0x24u) >> kVisibilityId) & 1u) == 0u) {
        return;
    }

    // `EntityComponent` checks the presence bitmask first and re-checks the id after the
    // accelerator hop, so a table being rebuilt on another thread cannot hand back a
    // different component.
    const uint32_t component = EntityComponent(base, entity, kVisibilityId);

    if (!plausible(component)) {
        return;
    }

    // The radius at +40 is a finite positive float on this component (ctor default 10000);
    // anything else is not the component and is left alone.
    const uint32_t radius_bits = read32(component + 40u);
    float radius;
    std::memcpy(&radius, &radius_bits, sizeof(radius));
    if (!(radius > 0.0f) || !std::isfinite(radius)) {
        return;
    }

    if (read8(component + 202u) != 0u) {
        return;
    }

    const uint8_t one = 1u;
    StoreGuest(base, component + 202u, &one, sizeof(one));
    ++g_always_in_view_set;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntities(uint32_t* out, uint32_t max_entities, uint32_t* out_unknown,
                              uint32_t* out_unknown_vtables, uint32_t max_unknown_vtables,
                              uint32_t* out_unknown_vtable_count) {
    static uint32_t walk_serial = 0u;
    ++walk_serial;
    // Words 0..11 identity and placement, 12..18 the guest's previous transform, 19 its
    // interpolation enable, 20 the highlight RGBA, 21..28 the stated appearance. See the row
    // write below.
    constexpr uint32_t kEntityStride = 21u + kAppearanceWords;
    constexpr uint16_t kNoDirect = 0xFFFFu;

    /// The twelve EC-2 vtables, and how each states its placement.
    /// `pos`/`ori` are byte offsets into the component; kNoDirect means "not a direct read".
    struct Ec2Layout {
        uint32_t vtable;
        uint16_t pos;
        uint16_t ori;
        bool hybrid;    // sub_82213A08: cached at +0x70 unless it defers to the physics body
        bool delegate;  // sub_8220DDB0: the placement lives on the object at +0x20
    };

    const Ec2Layout kEc2Layouts[] = {
    {refii::guest::Address(0x82006F88u), 0x20u, kNoDirect, false, false},  // orientation is a global
    {refii::guest::Address(0x82007258u), 0x20u, 0x30u, false, false},
    {refii::guest::Address(0x820E6048u), 0x20u, 0x30u, false, false},
    {refii::guest::Address(0x820CA190u), 0x40u, 0x30u, false, false},
    {refii::guest::Address(0x82007038u), 0x70u, kNoDirect, true, false},
    {refii::guest::Address(0x820071A0u), 0x70u, kNoDirect, true, false},
    {refii::guest::Address(0x820C4B90u), 0x70u, kNoDirect, true, false},
    {refii::guest::Address(0x820070F0u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x82007308u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x820073B8u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x820C6E58u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x820CB140u), kNoDirect, kNoDirect, false, true},  // the abstract base
};

    /// The delegate targets — the physics/proxy objects an EC-2 hands its placement to.
    ///
    /// The EC-2 getters do NOT call these at slot 0x40/0x44. `sub_8220DDB0` loads the object
    /// from EC2+0x20 and dispatches slot 0x0C for position; `sub_8220B940` dispatches slot
    /// 0x10 for orientation. A delegate vtable read as if it answered at 0x40 lands in the
    /// middle of a real vtable and names no class at all.
    ///
    /// `hop` is a CHAIN, not a special case, because the guest's is: a getter that is not a
    /// plain load holds a pointer at some offset and asks THAT object instead, and the object
    /// it names can do the same again. `0x82006A40` hops through +0x38 and dispatches on what
    /// it finds; `0x82005B80` hops through +0x2C and reads the vectors straight out.
    ///
    /// `dispatch` says which kind: true means look the object at `hop` up in this table again,
    /// false means read `pos`/`ori` out of it directly.
    ///
    /// A NULL at a hop is not a failure — the getters fall back to `flt_82099484`, which is
    /// 0.0, so the object genuinely is at the origin.
    static constexpr uint16_t kNoHop = 0xFFFFu;

    struct DelegateLayout {
        uint32_t vtable;
        uint16_t pos;
        uint16_t ori;
        uint16_t hop;
        bool dispatch;
    };

    const DelegateLayout kDelegateLayouts[] = {
        // slot 0x0C = sub_8255AFE0 `li r11,0x50`, slot 0x10 = sub_828CA2A0 `ld 0x60/0x68`.
        {refii::guest::Address(0x82006B30u), 0x50u, 0x60u, kNoHop, false},
        // slot 0x0C = sub_8222A4B0 `li r11,0x40`, slot 0x10 = sub_822082F8 `ld 0x50/0x58`.
        // 90 of the 388 unplaceable entities in BWSSlums were this class.
        {refii::guest::Address(0x820069C8u), 0x40u, 0x50u, kNoHop, false},
        // slot 0x0C = sub_8220B9E0 `lwz r4,0x38(r4)` then slot 0x14; slot 0x10 = sub_82232B70,
        // same hop then slot 0x18. 298 of the 388 were this one.
        {refii::guest::Address(0x82006A40u), 0u, 0u, 0x38u, true},
        // slot 0x14 = sub_8257A608 `li r11,0xF0`, slot 0x18 = sub_8257A618 `ld 0x100/0x108`.
        // Reached from 0x82006A40's hop and from the hybrid's +0x38.
        {refii::guest::Address(0x82005460u), 0xF0u, 0x100u, kNoHop, false},
        // slot 0x14 = sub_8220B9C8 `lwz r11,0x2C(r4); li r10,0x110`, slot 0x18 = sub_82232B38,
        // same hop then +0x150. The other thing 0x82006A40's +0x38 can hold — and what all
        // 298 of them actually held.
        {refii::guest::Address(0x82005B80u), 0x110u, 0x150u, 0x2Cu, false},
    };

    /// The delegate layout for a vtable, or null when it is not one we know.
    const auto find_delegate = [&](uint32_t vtable) -> const DelegateLayout* {
        for (const DelegateLayout& candidate : kDelegateLayouts) {
            if (candidate.vtable == vtable) {
                return &candidate;
            }
        }
        return nullptr;
    };

    /// The graphic-appearance components, and where each keeps its model hash.
    struct ModelSlot {
        uint8_t id;
        uint16_t offset;
    };

    static constexpr ModelSlot kModelSlots[] = {
        {4u, 0x18u},   // static mesh
        {6u, 0x20u},   // animated mesh
        {10u, 0x14u},  // portal
        {11u, 0x14u},  // screen-facing sprite
    };

    // Cleared up front so every early return below leaves the caller with "nothing unknown"
    // rather than whatever the last call wrote.
    if (out_unknown != nullptr) {
        *out_unknown = 0u;
    }

    if (out_unknown_vtable_count != nullptr) {
        *out_unknown_vtable_count = 0u;
    }

    if (out == nullptr || max_entities == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    // Guest pointers are big-endian and the address space is 32-bit; anything outside it is a
    // torn read rather than an object, so every hop is bounds-checked.
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read_vector = [&](uint32_t at, uint32_t* dst, int lanes) {
        uint32_t raw[4];
        CopyGuest(base, at, raw, (std::size_t)lanes * 4u);
        for (int i = 0; i < lanes; ++i) {
            dst[i] = __builtin_bswap32(raw[i]);
        }
    };

    /// Reads four floats and accepts them ONLY if they are a unit quaternion.
    ///
    /// A ROTATION QUATERNION IS UNIT BY DEFINITION, and that is the test — not "some lane is
    /// non-zero", which is what the orientation fallback used to ask. That question passes for
    /// any field that merely has bits in it, and the field at `placement+0xE0` is not always a
    /// quaternion: measured live in BWSMarket, every composite character came back
    /// `(0, 0, z, w)` with x and y EXACTLY zero and z/w astronomically large or zero —
    /// `(0, 0, -8.98e34, 0)`, `(0, 0, 3.23e25, -3.57e17)`, `(0, 0, 19169.12, -0)`. The leading
    /// pair of zeros is the giveaway: that is a 16-byte read of something whose first eight
    /// bytes are not part of a rotation at all.
    ///
    /// Downstream that garbage is invisible rather than loud. `|q|` of infinity or zero trips
    /// the host's degenerate guard and yields identity, while `(0,0,19169,0)` normalises to a
    /// clean 180° — so the characters simply stand in one fixed direction and nothing reports a
    /// fault. Rejecting here lets the NEXT source have its turn instead.
    const auto unit_quaternion_at = [&](uint32_t at, uint32_t* dst) -> bool {
        uint32_t bits[4];
        read_vector(at, bits, 4);
        float q[4];
        for (int i = 0; i < 4; ++i) {
            std::memcpy(&q[i], &bits[i], sizeof(q[i]));
            if (!std::isfinite(q[i])) {
                return false;
            }
        }

        const float length = (q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]);
        if (!(length > 0.98f && length < 1.02f)) {
            return false;
        }

        std::memcpy(dst, bits, sizeof(bits));
        return true;
    };

    /// Walks a delegate chain to the object that actually holds the vectors.
    ///
    /// Returns false when the chain reaches a class this table does not know, leaving the
    /// offending vtable in `unrecognised` so the caller can NAME it rather than guess an
    /// offset. `source` comes back 0 when the chain bottomed out on the guest's own null
    /// fallback — that is the origin, and a real answer.
    const auto follow_delegate = [&](uint32_t object, uint32_t& source, uint16_t& pos,
                                     uint16_t& ori, uint32_t& unrecognised) -> bool {
        // Bounded. The longest real chain is body -> +0x38 -> +0x2C; a cycle in guest memory
        // must not become a hang in a read that runs every frame.
        for (int depth = 0; depth < 4; ++depth) {
            const DelegateLayout* layout = find_delegate(read32(object));
            if (layout == nullptr) {
                unrecognised = read32(object);
                return false;
            }

            if (layout->hop == kNoHop) {
                source = object;
                pos = layout->pos;
                ori = layout->ori;
                return true;
            }

            const uint32_t next = read32(object + layout->hop);
            if (!plausible(next)) {
                source = 0u;  // the guest's own zero-vector fallback
                pos = kNoDirect;
                ori = kNoDirect;
                return true;
            }

            if (!layout->dispatch) {
                source = next;
                pos = layout->pos;
                ori = layout->ori;
                return true;
            }

            object = next;
        }

        unrecognised = read32(object);
        return false;
    };

    const uint32_t manager = GuestWordAt(base, refii::guest::Address(0x83496B28u));
    if (!plausible(manager)) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t unknown = 0u;
    uint32_t unknown_vtable_count = 0u;

    // Naming the vtables we do not know is the whole point of counting them: it turns "27% of
    // entities are unplaceable" into a short list of addresses to look up and add.
    //
    // `max_unknown_vtables` is a pair capacity, not a word capacity.
    const auto note_unknown = [&](uint32_t vtable) {
        ++unknown;
        if (out_unknown_vtables == nullptr) {
            return;
        }
        for (uint32_t i = 0; i < unknown_vtable_count; ++i) {
            if (out_unknown_vtables[i * 2u] == vtable) {
                ++out_unknown_vtables[(i * 2u) + 1u];
                return;  // distinct only — the pair carries the tally
            }
        }
        if (unknown_vtable_count < max_unknown_vtables) {
            out_unknown_vtables[unknown_vtable_count * 2u] = vtable;
            out_unknown_vtables[(unknown_vtable_count * 2u) + 1u] = 1u;
            ++unknown_vtable_count;
        }
    };

    uint32_t node = read32(manager + 8u);

    // The live count bounds the walk, so a corrupt link cannot spin forever.
    const uint32_t counted = read32(manager + 0x10u);
    const uint32_t guard = counted > 0u && counted < 65536u ? counted + 16u : 65536u;

    for (uint32_t steps = 0u; plausible(node) && written < max_entities && steps < guard;
         ++steps) {
        uint32_t list_node[3];
        CopyGuest(base, node, list_node, sizeof(list_node));
        const uint32_t entity = __builtin_bswap32(list_node[2]);
        node = __builtin_bswap32(list_node[0]);

        if (!plausible(entity)) {
            break;
        }

        const EntityFields fields(GuestReader{base}, entity);
        if ((fields.Byte(0x90u) & 0x40u) == 0u) {
            continue;  // not LIVE
        }

        // Every thirtieth walk: the byte sticks once set, and a spawn waits a second at most.
        if ((walk_serial % 30u) == 0u) {
            KeepAnimatedInView(base, entity);
        }

        const uint32_t placement = fields.Word(0x7Cu);
        if (!plausible(placement)) {
            continue;  // no placement component == not drawable
        }

        const uint32_t vtable = read32(placement);
        const Ec2Layout* layout = nullptr;
        for (const Ec2Layout& candidate : kEc2Layouts) {
            if (candidate.vtable == vtable) {
                layout = &candidate;
                break;
            }
        }

        if (layout == nullptr) {
            note_unknown(vtable);  // loudly, not guessed
            continue;
        }

        uint32_t position[4] = {0u, 0u, 0u, 0u};
        uint32_t orientation[4] = {0u, 0u, 0u, 0x3F800000u};  // identity (x,y,z,w)
        bool resolved = false;

        uint32_t source = placement;
        uint16_t position_offset = layout->pos;
        uint16_t orientation_offset = layout->ori;

        /// A CHARACTER'S FACING, out of its Havok shape phantom.
        ///
        /// THIS IS WHY EVERY HUMAN IN THE STREET FACED THE SAME WAY. The character placement
        /// classes (`CECPhysicsSimulationCharacterControlled` 0x820071A0 and
        /// `...CharacterNavigator` 0x82007038) do not store an orientation. Their getter
        /// `sub_82211110` hops `comp+0x20` -> `+0x38` and dispatches slot 0x18 on what it
        /// finds, which is a proxy with vtable 0x82005720 — a class this table did not know, so
        /// the chain walk failed and every character got the identity fallback, every frame.
        ///
        /// Position hid the bug. `sub_82213A08` caches the position at `comp+0x70` and falls
        /// back to it, so positions were right and people walked around correctly; there is no
        /// equivalent cache for orientation, so a missed delegate is total.
        ///
        /// Verified in-line, not taken from a report — the proxy's two getters differ only in
        /// the offset they read out of the SAME object:
        ///
        ///   sub_82210F88 (slot 0x18, orientation): lwz r11, 0x2C(r4) / addi r4, r11, 0xA0
        ///   sub_82213A80 (slot 0x14, position):    lwz r11, 0x2C(r4) / li r10, 0xD0
        ///
        /// So `proxy+0x2C` is an `hkpShapePhantom` and its `hkTransform` starts at `+0xA0`:
        /// three column-major `float4` basis vectors (world X, Y, Z) then the translation at
        /// `+0xD0`. There is NO stored quaternion — the guest derives one per call.
        ///
        /// Offsets are measured from the PROXY (`controller+0x38`), not the placement component.
        /// A character's position, out of the same phantom — `sub_82213A80`, the proxy's
        /// slot 0x14:
        ///
        ///   lwz r11, 0x2C(r4) / li r10, 0xD0 / lvx128 v0, r11, r10 / stvx128 v0, r0, r3
        ///
        /// This is the translation lane of the same `hkTransform` whose basis the orientation
        /// read above takes from `+0xA0`.
        const auto read_phantom_position = [&](uint32_t proxy, uint32_t* out) -> bool {
            if (!plausible(proxy) || read32(proxy) != refii::guest::Address(0x82005720u)) {
                return false;
            }

            const uint32_t phantom = read32(proxy + 0x2Cu);
            if (!plausible(phantom)) {
                return false;
            }

            uint32_t lanes[3];
            read_vector(phantom + 0xD0u, lanes, 3);
            for (int i = 0; i < 3; ++i) {
                float value;
                std::memcpy(&value, &lanes[i], sizeof(value));
                if (!std::isfinite(value)) {
                    return false;
                }
            }

            for (int i = 0; i < 3; ++i) {
                out[i] = lanes[i];
            }

            return true;
        };

        const auto read_phantom_quaternion = [&](uint32_t proxy, uint32_t* out) -> bool {
            if (!plausible(proxy) || read32(proxy) != refii::guest::Address(0x82005720u)) {
                return false;
            }

            const uint32_t phantom = read32(proxy + 0x2Cu);
            if (!plausible(phantom)) {
                return false;
            }

            // Column-major: element (row, col) is float [col * 4 + row].
            uint32_t raw[12];
            CopyGuest(base, phantom + 0xA0u, raw, sizeof(raw));
            float m[12];
            for (int i = 0; i < 12; ++i) {
                const uint32_t bits = __builtin_bswap32(raw[i]);
                std::memcpy(&m[i], &bits, sizeof(m[i]));
                if (!std::isfinite(m[i])) {
                    return false;
                }
            }

            const float m00 = m[0], m10 = m[1], m20 = m[2];
            const float m01 = m[4], m11 = m[5], m21 = m[6];
            const float m02 = m[8], m12 = m[9], m22 = m[10];

            // Shepperd's method — the largest-denominator branch, so it never divides by a
            // value near zero. The guest's own extraction (`quaternionFromRotatation`
            // @0x82D56728) takes `x = m21 - m12`, `y = m02 - m20`, `z = m10 - m01`, which is
            // this indexing exactly; it is only reproduced rather than called because any
            // correct extraction yields the same ROTATION, and q and -q are the same rotation.
            float x, y, z, w;
            const float trace = m00 + m11 + m22;
            if (trace > 0.0f) {
                const float s = std::sqrt(trace + 1.0f) * 2.0f;
                w = 0.25f * s;
                x = (m21 - m12) / s;
                y = (m02 - m20) / s;
                z = (m10 - m01) / s;
            } else if (m00 > m11 && m00 > m22) {
                const float s = std::sqrt(1.0f + m00 - m11 - m22) * 2.0f;
                w = (m21 - m12) / s;
                x = 0.25f * s;
                y = (m01 + m10) / s;
                z = (m02 + m20) / s;
            } else if (m11 > m22) {
                const float s = std::sqrt(1.0f + m11 - m00 - m22) * 2.0f;
                w = (m02 - m20) / s;
                x = (m01 + m10) / s;
                y = 0.25f * s;
                z = (m12 + m21) / s;
            } else {
                const float s = std::sqrt(1.0f + m22 - m00 - m11) * 2.0f;
                w = (m10 - m01) / s;
                x = (m02 + m20) / s;
                y = (m12 + m21) / s;
                z = 0.25f * s;
            }

            if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) ||
                !std::isfinite(w)) {
                return false;
            }

            // AND IT HAS TO BE UNIT. `isfinite` is not enough: a garbage basis is still made of
            // finite floats, and Shepperd's turns one into a finite non-rotation that then
            // silently becomes a bogus facing. The extraction of a real rotation matrix is unit
            // by construction, so this only rejects input that was never a rotation.
            const float length = (x * x) + (y * y) + (z * z) + (w * w);
            if (!(length > 0.98f && length < 1.02f)) {
                return false;
            }

            // HOST ORDER, LIKE EVERY OTHER LANE — this used to byte-swap "back to the guest's
            // byte order, because everything downstream byte-swaps", and nothing downstream
            // does. `read32` has already swapped on the way in, the array is initialised to a
            // host-order `0x3F800000`, and the C# side reads each word straight through
            // `BitConverter.Int32BitsToSingle`. The extra swap was the whole facing bug.
            //
            // It hid because of what a character's quaternion looks like. Facing is yaw-only
            // about Z, so the real value is `(0, 0, sin, cos)` — and `bswap(0) == 0`, so the two
            // zero lanes survived the corruption untouched. That is why every sample read
            // `(0, 0, junk, junk)` with x and y EXACTLY zero: not a wrong offset, not a missing
            // delegate, just the two lanes that a byte swap cannot damage sitting next to the
            // two it destroys. `0.7f = 0x3F333333` comes back as `0x3333333F`, about 4.2e7.
            const float lanes[4] = {x, y, z, w};
            for (int i = 0; i < 4; ++i) {
                std::memcpy(&out[i], &lanes[i], sizeof(out[i]));
            }

            return true;
        };

        if (layout->hybrid) {
            // The two halves gate DIFFERENTLY and must be asked separately.
            //
            // sub_82213A08 (position) defers to the sub-object only when the component has
            // NOT latched +0xA4; otherwise the cached vector at +0x70 wins. sub_82211110
            // (orientation) has no +0xA4 test at all — it delegates whenever the chain is
            // there, and falls back to identity, never to +0x70.
            //
            // Tying them to one branch returns identity for every latched entity, which looks
            // exactly like "the orientation offset is wrong" and is really the wrong gate.
            const uint32_t body = read32(placement + 0x20u);
            const uint32_t sub = plausible(body) ? read32(body + 0x38u) : 0u;

            uint32_t chained = 0u;
            uint16_t chained_pos = kNoDirect;
            uint16_t chained_ori = kNoDirect;
            uint32_t unrecognised = 0u;

            // THE CHARACTER PROXY FIRST. `sub` here is what `sub_82211110` dispatches slot 0x18
            // on, and for a character that is the Havok proxy — which holds a transform, not a
            // quaternion, so the generic delegate walk cannot express it and falls through. That
            // failure is silent and total: no orientation, identity forever, every human in the
            // street facing the same way while walking around perfectly correctly.
            if (read_phantom_quaternion(sub, orientation)) {
                orientation_offset = kNoDirect;

                // The position from the same phantom, on the guest's own gate.
                //
                // `sub_82213A08` reads `comp+0x70` only when `comp+0xA4` is non-zero or the
                // chain is missing; otherwise it dispatches the proxy's slot 0x14, the phantom
                // translation. `+0xA4`'s only writer is placement slot 35, from the network
                // paths (a replicated ghost); locally simulated characters read 0.
                //
                // `sub_82211F18`, the placement's per-tick update, writes `+0x70` on its
                // grounded branch only; once the physics component's byte at +104 is set it
                // takes the ballistic branch (`sub_82277B40` into
                // `hkpCharacterProxy::integrate`) and `+0x70` is no longer written.
                if (read8(placement + 0xA4u) == 0u && read_phantom_position(sub, position)) {
                    position_offset = kNoDirect;
                    resolved = true;
                }
            } else if (unit_quaternion_at(placement + 0xE0u, orientation)) {
                // THE COMPONENT'S OWN CACHE, second and only if it has ever been written.
                //
                // `sub_825989D8` (vtable +0x74, SetOrientation) stores the quaternion at
                // comp+0xE0 as well as pushing it to the physics proxy, so it is the guest's
                // own record of the facing. It is NOT per-frame — the constructor
                // `sub_825967B8` leaves it all zeros rather than identity, and only an explicit
                // set ever writes it — so it is a fallback, never the primary. All-zero is the
                // "never written" state and is not a rotation; taking it would be worse than
                // identity, hence the guard.
                read_vector(placement + 0xE0u, orientation, 4);
                orientation_offset = kNoDirect;
            } else if (plausible(sub) &&
                follow_delegate(sub, chained, chained_pos, chained_ori, unrecognised) &&
                chained != 0u && chained_ori != kNoDirect) {
                // Orientation always follows the chain when it exists — but still only if what
                // is there is a rotation. Same invariant as the two sources above; a chain that
                // resolves to the wrong field is no more usable than one that fails to resolve.
                if (unit_quaternion_at(chained + chained_ori, orientation)) {
                    orientation_offset = kNoDirect;
                }

                if (read8(placement + 0xA4u) == 0u) {
                    source = chained;
                    position_offset = chained_pos;
                }
            }
        } else if (layout->delegate) {
            const uint32_t body = read32(placement + 0x20u);
            if (!plausible(body)) {
                // A null body is the guest's own zero-vector/identity case, a real answer.
                resolved = true;
            } else {
                uint32_t chained = 0u;
                uint16_t chained_pos = kNoDirect;
                uint16_t chained_ori = kNoDirect;
                uint32_t unrecognised = 0u;

                if (!follow_delegate(body, chained, chained_pos, chained_ori, unrecognised)) {
                    note_unknown(unrecognised);  // named, never guessed
                    continue;
                }

                if (chained == 0u) {
                    resolved = true;  // the chain bottomed out at the origin
                } else {
                    source = chained;
                    position_offset = chained_pos;
                    orientation_offset = chained_ori;
                }
            }
        }

        if (!resolved && position_offset != kNoDirect) {
            read_vector(source + position_offset, position, 3);
            resolved = true;
        }

        if (!resolved) {
            note_unknown(vtable);
            continue;
        }

        // THE DIRECT READ, AND THE ONE THAT WAS STILL LETTING GARBAGE THROUGH.
        //
        // The three sources above now each check that what they found is a rotation, but this
        // path is taken by every layout that names an orientation offset outright — and it was
        // reading four floats and trusting them. Measured after the other three were guarded:
        // characters still arrived as `(0, 0, -3.43e21, 0)`, 142,800 of them refused downstream,
        // because THIS is where theirs came from.
        //
        // Leaving `orientation` at the identity it was initialised to is the honest outcome: a
        // field that does not hold a unit quaternion is not an orientation, whatever the layout
        // table says its offset is.
        if (orientation_offset != kNoDirect &&
            !unit_quaternion_at(source + orientation_offset, orientation)) {
            orientation[0] = 0u;
            orientation[1] = 0u;
            orientation[2] = 0u;
            orientation[3] = 0x3F800000u;  // identity (x,y,z,w), guest byte order
        }

        // The model hash, off whichever graphic-appearance component the entity carries.
        uint32_t model = 0u;
        for (const ModelSlot& slot : kModelSlots) {
            const uint32_t component = EntityComponentOf(base, fields, slot.id);
            if (plausible(component)) {
                model = read32(component + slot.offset);
                if (model != 0u) {
                    break;
                }
            }
        }

        uint32_t* row = out + (written * kEntityStride);
        row[0] = position[0];
        row[1] = position[1];
        row[2] = position[2];
        row[3] = orientation[0];
        row[4] = orientation[1];
        row[5] = orientation[2];
        row[6] = orientation[3];
        row[7] = model;
        row[8] = entity;
        row[9] = fields.Word(0x14u);  // UniqueID, high
        row[10] = fields.Word(0x18u); // UniqueID, low
        row[11] = fields.Word(0x1Cu); // GDB record hash

        // The pair the guest itself interpolates.
        //
        // Component key 3 is the guest's renderable, and the first act of every game tick
        // latches its transform there (`sub_821C8FB0` -> `sub_82231A28` -> `sub_8222B2E0`):
        // position to `obj+0xA0`, the orientation quaternion to `obj+0xB0`. The tick then moves
        // everything, so the provider returns the new value and `obj+0xA0` holds the previous
        // one. `sub_822112D8` draws `prev*(1-alpha) + live*alpha`, slerping the rotation.
        //
        // `obj+0xC9` is an enable, not a suppress:
        //
        //     8221138c  lbz    r29, 0xC9(r31)
        //     82211394  cmplwi cr6, r29, 0
        //     822113a0  beq    cr6, loc_822113F4   ; zero -> skip the blend entirely
        //
        // Set to 1 in the constructor (`82571f60`) and cleared only by the Havok rigid-body
        // component's init (`82970a0c`): "interpolate unless physics owns this".
        //
        // Lanes are sent as `read32` returns them (already swapped); the C# side reads each word
        // through `Int32BitsToSingle`. Same id as `kVisibilityId`, which is function-scoped.
        constexpr uint8_t kRenderableId = 3u;
        const uint32_t renderable = EntityComponentOf(base, fields, kRenderableId);
        row[12] = 0u;
        row[13] = 0u;
        row[14] = 0u;
        row[15] = 0u;
        row[16] = 0u;
        row[17] = 0u;
        row[18] = 0x3F800000u;  // identity quaternion w
        row[19] = 0u;           // no renderable -> no pair -> do not interpolate
        row[20] = 0x000000FFu;  // no renderable -> the guest's OFF colour, {0,0,0,255}

        // The appearance, as constructed (`sub_82571DF8`) unless a setter, a load or the
        // definition (vf8, +192) stated otherwise: alpha, camera alpha and scale 1.0, tint
        // 1,1,1,1, drawable, unmasked, depth writes as the material's.
        uint32_t* appearance = row + 21;
        appearance[0] = appearance[1] = appearance[2] = 0x3F800000u;
        appearance[3] = appearance[4] = appearance[5] = appearance[6] = 0x3F800000u;
        appearance[kAppearanceFlags] = kAppearanceDrawable;
        if (plausible(renderable)) {
            // +0x60 through +0xC9 in one read, for the pair, its enable and the highlight below.
            uint8_t state[0xCAu - 0x60u];
            CopyGuest(base, renderable + 0x60u, state, sizeof(state));
            const auto state_word = [&state](uint32_t offset) {
                uint32_t value;
                std::memcpy(&value, state + (offset - 0x60u), sizeof(value));
                return __builtin_bswap32(value);
            };
            row[12] = state_word(0xA0u);
            row[13] = state_word(0xA4u);
            row[14] = state_word(0xA8u);
            row[15] = state_word(0xB0u);
            row[16] = state_word(0xB4u);
            row[17] = state_word(0xB8u);
            row[18] = state_word(0xBCu);
            row[19] = state[0xC9u - 0x60u] != 0u ? 1u : 0u;

            // The targeting highlight. The colour lives on the renderable (component 3) as RGBA
            // at +96..99, written by `sub_825738F0` and submitted as a shader constant by
            // `sub_82572C18` only when it differs from `dword_8331B158` = {0,0,0,255}. The off
            // state is that colour, not a flag: the clear path `sub_82429BB0` writes
            // `byte_83319B68` = {0,0,0,255} and `+144`'s 0x20 bit stays set after the first
            // write, so the host compares the bytes.
            //
            // The colour stated to the setter (hooked in `target_highlight.cpp`, past the
            // component-159 redirect) wins; the component bytes are the fallback, and are what
            // a component the setter has never named reads back as.
            const uint32_t on_component = state_word(0x60u);
            const uint32_t said = refii::vfs::StatedHighlightFor(renderable);
            row[20] = said != 0u ? said : on_component;

            // What the guest's appearance setters stated for this renderable — every death fade
            // (DeathType 5/6/7 ramp SetAlpha), the corpse tint, hidden carry slots, SetScale.
            refii::vfs::StatedAppearanceFor(renderable, appearance);
            const uint32_t creature = EntityComponentOf(base, fields, 20u);
            if (refii::hostcore::IsDeadCreature(refii::hostcore::GuestReader{base}, creature)) {
                appearance[kAppearanceFlags] |= refii::hostcore::kAppearanceDeadCreature;
                if (refii::vfs::CreatureLeftCorpse(creature)) {
                    appearance[kAppearanceFlags] |= refii::hostcore::kAppearanceGuestCorpse;
                }
            }
        }

        ++written;
    }

    if (out_unknown != nullptr) {
        *out_unknown = unknown;
    }

    if (out_unknown_vtable_count != nullptr) {
        *out_unknown_vtable_count = unknown_vtable_count;
    }

    return written;
}

/// The COMPONENTS one entity carries, as {id, pointer} pairs.
///
/// Which components an entity carries is a live-memory fact, and no decompile answers it. A
/// character whose component 6 holds `*(comp6 + 0x20) == 0` has no model hash there and can
/// only be drawn through the parts list on `CECGraphicAppearanceMorph` (component id 17) —
/// see RefiiHostGetEntityMorphParts — so which of the two applies has to be measured first.
///
/// This reads no offset INSIDE any component, only the list, so it depends on no layout
/// beyond the walk itself.
///
/// The walk is the entity's own component vector at `entity+0x48`..`entity+0x4C`, 8-byte
/// `{id, pointer}` pairs — the same walk `RefiiHostGetEntities` falls back to when the
/// accelerator at `entity+0x8C` is not plausible.
///
/// Writes `count * 2` words: id then pointer. Returns the number of PAIRS written, which is
/// capped at `max_pairs`; 0 means the entity has no readable component list.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityComponents(uint32_t entity, uint32_t* out_pairs,
                                      uint32_t max_pairs) {
    if (out_pairs == nullptr || max_pairs == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    const uint32_t components = read32(entity + 0x48u);
    const uint32_t components_end = read32(entity + 0x4Cu);
    if (!plausible(components) || components_end <= components) {
        return 0u;
    }

    // Bounded against a torn read the same way the entity walk is: a component vector that
    // claims to be enormous is a bad pointer pair, not an entity with thousands of parts.
    const uint32_t span = components_end - components;
    if (span > 0x4000u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = components; at + 8u <= components_end && written < max_pairs;
         at += 8u) {
        out_pairs[written * 2u] = read32(at);
        out_pairs[(written * 2u) + 1u] = read32(at + 4u);
        ++written;
    }

    return written;
}

/// How many words RefiiHostGetEntityLocomotion writes.
#define REFII_LOCOMOTION_WORDS 7u

/// How many words RefiiHostGetEntityAnimState writes per clip leaf.
#define REFII_ANIM_LEAF_WORDS 12u

/// THE BYTES OF ONE BONE MASK NODE — one per LIVE skeleton bone, 0..255, the weight the
/// overlay applies to that bone (`sub_82B7E3F8` writes `int(w*255+0.5)`, the bool setter 255
/// or 0; `sub_822A57D0` divides by 255 into the pose buffer's row2.W). Node layout
/// (`sub_82761CE8`): +28 skeleton id, +36 begin, +40 end of the byte vector. Returns the byte
/// count written (0 for a node that is not a mask or whose vector is not readable) and, when
/// asked, the skeleton id the mask was built against.
/// WHAT ANY ENTITY WEARS — RefiiHostGetWornItems for an entity other than the hero. The hider
/// the guest sends its mesh instance (`sub_82229BE0`) is built at commit time from the GDB, not
/// stored: for every record of the appearance-modifier component (id 15, the same vector the
/// hero walk reads) it merges (`sub_824C13A8`) the item's `ClustersCovered` property — or
/// `OverrideFemaleClustersCovered` when the gender component (id 12) holds 1 at +12, or the
/// `...Open...` variants when byte +61 of the component is set and the item's
/// `BodyAreasCovered` carries 0x800 — into the set of body cluster names its draw-list builder
/// (`sub_82274CE0`) skips. The host takes the worn hashes from here and the cluster names from
/// its own copy of the GDB. Writes 2 words per item: the GDB hash and a flags word (bit 0
/// female, bit 1 the component's open byte). Returns the item count; 0 on a torn walk.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityWornItems(uint32_t entity, uint32_t* out_words,
                                                           uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < 2u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t appearance = EntityComponent(base, entity, 15u);
    if (appearance == 0u) {
        return 0u;
    }
    uint32_t flags = 0u;
    const uint32_t gender = EntityComponent(base, entity, 12u);
    if (gender != 0u && GuestWordAt(base, gender + 12u) == 1u) {
        flags |= 1u;
    }
    if (GuestByteAt(base, appearance + 61u) != 0u) {
        flags |= 2u;
    }
    const uint32_t begin = GuestWordAt(base, appearance + 16u);
    const uint32_t end = GuestWordAt(base, appearance + 20u);
    if (!plausible(begin) || !plausible(end) || end <= begin) {
        return 0u;
    }
    constexpr uint32_t kStride = 24u;
    const uint32_t span = end - begin;
    if ((span % kStride) != 0u || (span / kStride) > 256u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written + 2u <= word_capacity; at += kStride) {
        const uint32_t item = GuestWordAt(base, at);
        if (!plausible(item)) {
            continue;
        }
        const uint32_t hash = GuestWordAt(base, item + 4u);
        if (hash == 0u) {
            continue;
        }
        out_words[written++] = hash;
        out_words[written++] = flags;
    }
    if (GuestWordAt(base, appearance + 16u) != begin || GuestWordAt(base, appearance + 20u) != end) {
        return 0u;  // a push reallocated mid-walk
    }
    return written / 2u;
}

/// THE DYE ON EACH WORN ITEM, per item instance. The dye is the item's type-2 instance record
/// (`sub_823A1E18` = `sub_82764B30(item, 2)`: the extras vector at item+28..+32, stride 8,
/// elem+0 the object, matched here by its vtable `CInventoryInstanceDataDyed` 0x820C7E58, whose
/// slot 1 `sub_828EDC88` answers 2). Its words as `CInventoryInstanceDataDyed_vf5` saves them:
/// +4 Colour1 (bytes +0 blue, +1 green, +2 red, +3 alpha), +8 Brightness1, +12 Colour2,
/// +16 Brightness2. A slot is dyed by the guest's own test `sub_826CAFA8`: brightness != 0 or
/// colour != `dword_8331B158` (0x000000FF, opaque black, what the scrub `sub_826CAE08` restores).
/// Walks component 15's worn vector in the order RefiiHostGetEntityWornItems does. Writes
/// REFII_WORN_DYE_WORDS words per worn item: record hash, flags (bit 0 slot 1 dyed, bit 1 slot 2
/// dyed), colour 1, brightness 1 bits, colour 2, brightness 2 bits — a colour word packs the
/// bytes from the low end in memory order (blue | green << 8 | red << 16 | alpha << 24).
/// Returns the item count; 0 on a torn walk.
#define REFII_WORN_DYE_WORDS 6u
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityWornDyes(uint32_t entity, uint32_t* out_words,
                                                          uint32_t word_capacity) {
    // `dword_8331B158` = 0x000000FF, bytes 00 00 00 FF, packed below as 0xFF000000.
    constexpr uint32_t kUndyed = 0xFF000000u;
    constexpr uint32_t kMaxExtras = 16u;
    if (out_words == nullptr || word_capacity < REFII_WORN_DYE_WORDS) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t appearance = EntityComponent(base, entity, 15u);
    if (appearance == 0u) {
        return 0u;
    }
    const uint32_t dyed_vtable = refii::guest::Address(0x820C7E58u);
    const uint32_t begin = GuestWordAt(base, appearance + 16u);
    const uint32_t end = GuestWordAt(base, appearance + 20u);
    if (!plausible(begin) || !plausible(end) || end <= begin) {
        return 0u;
    }
    constexpr uint32_t kStride = 24u;
    const uint32_t span = end - begin;
    if ((span % kStride) != 0u || (span / kStride) > 256u) {
        return 0u;
    }
    // A colour word packs the four bytes in memory order from the low byte up:
    // blue | green << 8 | red << 16 | alpha << 24.
    const auto colour_of = [base](uint32_t at) {
        return static_cast<uint32_t>(GuestByteAt(base, at)) |
               (static_cast<uint32_t>(GuestByteAt(base, at + 1u)) << 8) |
               (static_cast<uint32_t>(GuestByteAt(base, at + 2u)) << 16) |
               (static_cast<uint32_t>(GuestByteAt(base, at + 3u)) << 24);
    };
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written + REFII_WORN_DYE_WORDS <= word_capacity;
         at += kStride) {
        const uint32_t item = GuestWordAt(base, at);
        if (!plausible(item)) {
            continue;
        }
        const uint32_t hash = GuestWordAt(base, item + 4u);
        if (hash == 0u) {
            continue;
        }
        uint32_t dye = 0u;
        const uint32_t extras = GuestWordAt(base, item + 28u);
        const uint32_t extras_end = GuestWordAt(base, item + 32u);
        if (plausible(extras) && extras_end >= extras) {
            const uint32_t count = std::min((extras_end - extras) >> 3, kMaxExtras);
            for (uint32_t i = 0u; i < count && dye == 0u; ++i) {
                const uint32_t record = GuestWordAt(base, extras + 8u * i);
                if (plausible(record) && GuestWordAt(base, record) == dyed_vtable) {
                    dye = record;
                }
            }
        }
        uint32_t flags = 0u;
        uint32_t colour1 = kUndyed;
        uint32_t colour2 = kUndyed;
        uint32_t bright1 = 0u;
        uint32_t bright2 = 0u;
        if (dye != 0u) {
            colour1 = colour_of(dye + 4u);
            bright1 = GuestWordAt(base, dye + 8u);
            colour2 = colour_of(dye + 12u);
            bright2 = GuestWordAt(base, dye + 16u);
            // The test is a float compare with 0.0: +0 and -0 both read as undyed.
            if ((bright1 & 0x7FFFFFFFu) != 0u || colour1 != kUndyed) flags |= 1u;
            if ((bright2 & 0x7FFFFFFFu) != 0u || colour2 != kUndyed) flags |= 2u;
        }
        out_words[written++] = hash;
        out_words[written++] = flags;
        out_words[written++] = colour1;
        out_words[written++] = bright1;
        out_words[written++] = colour2;
        out_words[written++] = bright2;
    }
    if (GuestWordAt(base, appearance + 16u) != begin || GuestWordAt(base, appearance + 20u) != end) {
        return 0u;  // a push reallocated mid-walk
    }
    return written / REFII_WORN_DYE_WORDS;
}

/// THE SCARS ON AN ENTITY, as `CECMorph` (component 116) holds them: the vector at +20 (begin
/// +24, end +28) of 12-byte records the save names in `CECMorph_vf9` — +0 BodyArea (a mask),
/// +4 TextureMorphRecordID, +8 Permanent (byte). `sub_825B8420` draws each with the record's
/// `MorphTexture`/`ScarTextureSpec`/`ScarTextureBump` when +8 is set, the `...Fresh` ones when
/// not. Three words per scar: area, record, permanent (0/1). Returns the scar count; 0 on a torn
/// read or an entity with no morph component.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityScars(uint32_t entity, uint32_t* out_words,
                                                       uint32_t word_capacity) {
    constexpr uint32_t kStride = 12u;
    if (out_words == nullptr || word_capacity < 3u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t morph = EntityComponent(base, entity, 116u);
    if (morph == 0u) {
        return 0u;
    }
    const uint32_t begin = GuestWordAt(base, morph + 24u);
    const uint32_t end = GuestWordAt(base, morph + 28u);
    if (begin == end) {
        return 0u;
    }
    if (!plausible(begin) || end < begin || ((end - begin) % kStride) != 0u ||
        (end - begin) / kStride > 64u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written + 3u <= word_capacity; at += kStride) {
        out_words[written++] = GuestWordAt(base, at);
        out_words[written++] = GuestWordAt(base, at + 4u);
        out_words[written++] = GuestByteAt(base, at + 8u) != 0u ? 1u : 0u;
    }
    if (GuestWordAt(base, morph + 24u) != begin || GuestWordAt(base, morph + 28u) != end) {
        return 0u;  // a push reallocated mid-read
    }
    return written / 3u;
}

/// AN ENTITY'S GENDER, as `CECGender` (component 12) states it: the enum at +12 that
/// `CECGender_vf9` saves as "Gender" and the hider `sub_824C13A8` tests (`lwz r11, 0xC(r10);
/// cmpwi cr6, r11, 1` at 0x824C152C, 1 = female). Returns the enum (1 female, 2 male), or 0 when
/// the entity has no gender component.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityGender(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }
    const uint32_t gender = EntityComponent(base, entity, 12u);
    return gender != 0u ? GuestWordAt(base, gender + 12u) : 0u;
}

/// THE POST-FOLD AIM AND LOOK. After evaluating any animation node (`sub_822DC980`, the tail
/// past the type switch) the guest rewrites two bone chains: when the skeleton's aim flag
/// (+57) is set and the node carries a record at +8, each of the 5 bones listed at
/// skeleton+60 is blended (`sub_822D9738`) from its folded rotation toward a per-bone target
/// quaternion in the record (16 bytes each from +16) by the record's weight (+4) in the
/// record's mode (+0); when the look flag (+58) is set and the node carries a record at +12,
/// the same for the 6 bones at skeleton+80. The targets are the guest's IK solve — the head
/// turning to the hero, the arm aiming a gun — stated per tick; the host takes them as
/// data and applies the same blend after its own fold. The skeleton object is comp6+148.
///
/// Out words: [0] aim flag, [1] look flag, [2..6] aim bones, [7..12] look bones (guest
/// indices), then per op: [record uid, kind (0 aim, 1 look), mode, weight bits, record weight
/// bits, then 4 words per bone (x y z w bits) for 5 or 6 bones]. Returns the op count; the
/// header is always written when the entity has a skeleton.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimPostOps(uint32_t entity, uint32_t* out_words,
                                                       uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < 13u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read32 = [&](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&](uint32_t at) { return GuestByteAt(base, at); };
    const auto readf = [&](uint32_t at) {
        const uint32_t bits = read32(at);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(float));
        return value;
    };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t mesh = EntityComponent(base, entity, 6u);
    const uint32_t controller = EntityComponent(base, entity, 19u);
    if (mesh == 0u || controller == 0u) {
        return 0u;
    }
    const uint32_t skeleton = read32(mesh + 148u);
    if (!plausible(skeleton)) {
        return 0u;
    }
    out_words[0] = read8(skeleton + 57u);
    out_words[1] = read8(skeleton + 58u);
    for (uint32_t i = 0; i < 5u; ++i) {
        out_words[2u + i] = read32(skeleton + 60u + 4u * i);
    }
    for (uint32_t i = 0; i < 6u; ++i) {
        out_words[7u + i] = read32(skeleton + 80u + 4u * i);
    }
    uint32_t written = 13u;
    uint32_t ops = 0u;
    uint32_t record_uid = 0u;
    uint32_t record_weight_bits = 0u;
    const auto emit = [&](uint32_t kind, uint32_t record) {
        const uint32_t bones = kind == 0u ? 5u : 6u;
        const uint32_t words = 5u + 4u * bones;
        if (!plausible(record) || written + words > word_capacity) {
            return;
        }
        out_words[written++] = record_uid;
        out_words[written++] = kind;
        out_words[written++] = read32(record);
        out_words[written++] = read32(record + 4u);
        out_words[written++] = record_weight_bits;
        for (uint32_t b = 0; b < bones; ++b) {
            for (uint32_t k = 0; k < 4u; ++k) {
                out_words[written++] = read32(record + 16u + 16u * b + 4u * k);
            }
        }
        ++ops;
    };
    // Every node of every playing record's tree, in the shape the leaf export walks.
    uint32_t stack[64];
    const auto walk_tree = [&](uint32_t root) {
        int depth = 0;
        if (plausible(root)) {
            stack[depth++] = root;
        }
        int steps = 0;
        while (depth > 0 && ++steps < 512) {
            const uint32_t node = stack[--depth];
            if (!plausible(node)) {
                continue;
            }
            if (out_words[0] != 0u) {
                emit(0u, read32(node + 8u));
            }
            if (out_words[1] != 0u) {
                emit(1u, read32(node + 12u));
            }
            const auto push = [&](uint32_t child) {
                if (depth < 64 && plausible(child)) {
                    stack[depth++] = child;
                }
            };
            const uint32_t node_type = read32(node + 20u);
            switch (node_type) {
                case 1u:
                case 6u: {
                    float t = readf(node + 32u);
                    if (!std::isfinite(t)) {
                        t = 0.0f;
                    }
                    t = std::clamp(t, 0.0f, 1.0f);
                    if (node_type == 6u) {
                        t = t * t * (3.0f - 2.0f * t);
                    }
                    if (t <= 0.0001f) {
                        push(read32(node + 24u));
                    } else if (t >= 0.9999f) {
                        push(read32(node + 28u));
                    } else {
                        push(read32(node + 24u));
                        push(read32(node + 28u));
                    }
                    break;
                }
                case 2u: {
                    const uint32_t begin = read32(node + 28u);
                    const uint32_t end = read32(node + 32u);
                    if (plausible(begin) && end >= begin && end - begin <= 4u * 64u) {
                        for (uint32_t at = begin; at + 4u <= end; at += 4u) {
                            const uint32_t child = read32(at);
                            if (plausible(child) && readf(child + 16u) > 1e-5f) {
                                push(child);
                            }
                        }
                    }
                    break;
                }
                case 3u:
                    push(read32(node + 28u));
                    push(read32(node + 24u));
                    break;
                case 4u:
                    push(read32(node + 24u));
                    break;
                case 5u:
                    push(read32(node + 28u));
                    break;
                default:
                    break;
            }
        }
    };
    // The record lists at comp19+96 (primaries) and +108 (overlays): std::list nodes, record
    // at node+8, tree root at record+36, weight at +20, uid at +32, state 2 = finished.
    const auto walk_list = [&](uint32_t list) {
        const uint32_t sentinel = read32(list + 4u);
        const uint32_t size = read32(list + 8u);
        if (!plausible(sentinel) || size == 0u || size > 64u) {
            return;
        }
        uint32_t node = read32(sentinel);
        for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
            const uint32_t record = read32(node + 8u);
            node = read32(node);
            if (!plausible(record) || read32(record + 28u) == 2u) {
                continue;
            }
            record_uid = read32(record + 32u);
            record_weight_bits = read32(record + 20u);
            walk_tree(read32(record + 36u));
        }
    };
    const uint32_t root = read32(controller + 156u);
    if (plausible(root)) {
        record_uid = 0u;
        record_weight_bits = 0x3F800000u;
        walk_tree(root);
    }
    walk_list(controller + 96u);
    walk_list(controller + 108u);
    return ops;
}

/// WHAT EACH BUILT PART HIDES. The composite builder (`sub_8220DE88`, 0x8220E828..E8C0)
/// hides, on every part, the union of the cluster sets of every OTHER part whose sort layer is
/// strictly greater — a coat over a shirt, never a coat over itself. Both live on the
/// GraphicAppearanceMorph component's built vector (id 17, +0x38..+0x3C, 24-byte records):
/// model hash +0, an MSVC `std::set<LHString>` at +4 (head +8, size +12; nodes left/parent/
/// right/value/colour/isnil, the value an LHString handle whose object holds chars +0 and
/// length +4), the `ClusterSortLayer` +16 and the body-part byte +20. The guest built the
/// sets from the GDB with the wearer's gender and open state already applied
/// (`sub_8217EDE0`), so the host takes them as stated rather than re-deriving them.
/// Writes one line per part into out_text: `hash layer body name;name;...` (hash hex,
/// layer signed decimal, body 0/1), newline-separated. Returns the part count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartHides(uint32_t entity, char* out_text,
                                                           uint32_t text_capacity) {
    if (out_text == nullptr || text_capacity < 2u) {
        return 0u;
    }
    out_text[0] = '\0';
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read32 = [&](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&](uint32_t at) { return GuestByteAt(base, at); };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t morph = EntityComponent(base, entity, 17u);
    if (morph == 0u) {
        return 0u;
    }
    const uint32_t begin = read32(morph + 0x38u);
    const uint32_t end = read32(morph + 0x3Cu);
    if (!plausible(begin) || end <= begin || ((end - begin) % 24u) != 0u || (end - begin) > 24u * 64u) {
        return 0u;
    }
    uint32_t used = 0u;
    const auto put = [&](const char* text, uint32_t length) -> bool {
        if (used + length + 1u >= text_capacity) {
            return false;
        }
        std::memcpy(out_text + used, text, length);
        used += length;
        out_text[used] = '\0';
        return true;
    };
    uint32_t parts = 0u;
    for (uint32_t record = begin; record + 24u <= end; record += 24u) {
        char head_text[48];
        const int head_length = std::snprintf(head_text, sizeof(head_text), "%08X %d %u", read32(record),
                                              static_cast<int32_t>(read32(record + 16u)),
                                              read8(record + 20u) != 0u ? 1u : 0u);
        if (head_length <= 0 || !put(head_text, static_cast<uint32_t>(head_length))) {
            break;
        }
        const uint32_t set_head = read32(record + 8u);
        const uint32_t set_size = read32(record + 12u);
        if (plausible(set_head) && set_size != 0u && set_size <= 64u) {
            uint32_t stack[64];
            uint32_t depth = 0u;
            uint32_t budget = 256u;
            const uint32_t root = read32(set_head + 4u);
            if (plausible(root) && root != set_head) {
                stack[depth++] = root;
            }
            bool first = true;
            while (depth != 0u && budget-- != 0u) {
                const uint32_t node = stack[--depth];
                if (!plausible(node) || node == set_head) {
                    continue;
                }
                if (read8(node + 17u) == 0u) {
                    const uint32_t handle = read32(node + 12u);
                    if (plausible(handle)) {
                        const uint32_t chars = read32(handle);
                        const uint32_t length = read32(handle + 4u);
                        if (plausible(chars) && length != 0u && length < 96u) {
                            char name[96];
                            uint32_t n = 0u;
                            for (; n < length; ++n) {
                                const uint8_t c = read8(chars + n);
                                if (c < 0x20u || c > 0x7Eu) {
                                    break;
                                }
                                name[n] = static_cast<char>(c);
                            }
                            if (n == length) {
                                put(first ? " " : ";", 1u);
                                put(name, n);
                                first = false;
                            }
                        }
                    }
                }
                const uint32_t left = read32(node);
                const uint32_t right = read32(node + 8u);
                if (depth + 2u <= 64u) {
                    if (plausible(left) && left != set_head) stack[depth++] = left;
                    if (plausible(right) && right != set_head) stack[depth++] = right;
                }
            }
        }
        put("\n", 1u);
        ++parts;
    }
    return parts;
}

/// THE LIGHTING ZONE an entity's geometry is drawn in: a light lights a draw only when either
/// zone is 0 or both are equal (`sub_82AB6360`, 0x82AB6428-0x82AB644C). A CECBuilding's own
/// static geometry is in the zone of its definition hash, `entity+0x1C & 0x7FFFFFFF`, when
/// `entity+0x90 & 0x40` (StaticMesh AddToScene `sub_822370A8`, 0x82237260-0x822372B4), else 1;
/// everything else is in its renderable's zone at `+0xD0`, which `sub_822D8AA8` keeps by
/// `LightZoneMode` (+0xCC): the building under it, the owner's building, 1, or 0 for all.
/// Returns 1 (the mesh constructor's zone, `sub_82AA2658` 0x82AA26D8) for an entity with no
/// renderable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLightZone(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 1u;
    }
    constexpr uint32_t kBuilding = 67u;
    if (((GuestWordAt(base, entity + 0x24u + (kBuilding / 32u) * 4u) >> (kBuilding % 32u)) & 1u) != 0u) {
        const uint32_t building = EntityComponent(base, entity, kBuilding);
        if (building != 0u && (GuestWordAt(base, building + 0x1Cu) & (0x200u | 0x002u)) != 0u) {
            return 1u;
        }
        return (GuestByteAt(base, entity + 0x90u) & 0x40u) != 0u
            ? GuestWordAt(base, entity + 0x1Cu) & 0x7FFFFFFFu
            : 1u;
    }
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    return renderable != 0u ? GuestWordAt(base, renderable + 0xD0u) : 1u;
}

/// THE ENTITY'S OWN LIGHTING ZONE, the renderable's `+0xD0` with no CECBuilding override: what
/// a light hosted on the entity lights from. `CECGraphicAppearanceLight` builds its light
/// descriptors from the renderable's zone word (`sub_825C2108` 0x825C23A4: `*(a3 + 52)` of the
/// renderable's zone block) and re-sends it through `sub_825C2060`, forcing 0 only for its own
/// IgnoreZoning byte (+126, 0x825C2074) — never the static-mesh definition-hash zone that
/// RefiiHostGetEntityLightZone reports for a CECBuilding's own geometry. Returns 1 for an entity
/// with no renderable; the IgnoreZoning answer stays with the light's record on the host side,
/// as for RefiiHostGetEntityLightZone.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityOwnLightZone(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 1u;
    }
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    return renderable != 0u ? GuestWordAt(base, renderable + 0xD0u) : 1u;
}

/// WHETHER THE GUEST POSES A RIG THIS TICK: 2, not stated. Fable 2's view gate is
/// `sub_822142E8(GA, GA+0x28)` (`bl` 0x8220D4A4 in `sub_8220D1B8`; disc `sub_82214420` from
/// `sub_8220D2F0`), GA = component 3: GA+0xCA -> yes (0x82214308) before anything else; else
/// GA+0x68 < GA+0x28² and, with byte_833194A9 and a proxy at GA+0x18, engine+0x70 - proxy+8
/// (engine = *(*(*(*(0x83496AB8)+0xC)+0x68)+4), vtable 0x82001CCC slot 4 `sub_8220C1A0`) > 30 ->
/// no, < 4 -> yes; then GA+0xCB. GA+0x68/+0xCB are refreshed only past the +0xCA return
/// (`sub_822141A8` 0x82214324). KeepAnimatedInView (above) writes GA+0xCA = 1 on every live
/// entity with the component, so the gate answers our own write and its view terms are frozen:
/// reading it back would state the bridge, not the guest. Fable 3 answers from its gate
/// (Fable3/src/hostbridge/src/pose_gate_feed.cpp).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPoseGate(uint32_t) {
    return 2u;
}

/// THE GUEST'S CLOTH: not stated (0xFFFFFFFF), so the shell keeps its own solver for Fable 2.
/// Fable 2's binary has the publish Fable 3's bridge states (Fable3/src/hooks/src/cloth_hooks.cpp):
/// the cloth draw publishes each sim item through `sub_8229F0B8(item)` — item+532 the published byte,
/// a wait on the item's event at item+536 (`sub_821969C8`, 0x8229F0E8), the fade lerp below
/// item+528 = 1.0 between item+56 and item+60 (0x8229F144), then the normals and the vertex buffer.
/// Stating it here would replace the shell's solver for Fable 2 and change what it draws, which
/// is the user's call, not this export's.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityClothPieces(uint32_t, uint32_t*, uint32_t) {
    return 0xFFFFFFFFu;
}

/// Fable 2 states no cloth vertices (RefiiHostGetEntityClothPieces).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClothPieceVertices(uint32_t, uint32_t, float*, uint32_t,
                                                              double* out_frame) {
    if (out_frame != nullptr) {
        *out_frame = 0.0;
    }
    return 0u;
}

/// WHERE THE GUEST'S POSE PUTS A RIG: the sphere around its published bones, for the shell's
/// view test when the title states no pose gate. The guest's own view term is not it:
/// `sub_82239D98` (refresh 0x822142CC) tests the placement's physics body, placement+0x7C slot
/// 0x60 (`sub_828D4110`: `lwz r3,0x20(r3)`; `sub_828FD4A0`: none), its shape copied by
/// `sub_821F2630` (centre +0x30, radius +0x40), else the placement position as a point (slot
/// 0x40, `sub_821DC0C8`). Both follow the placement, and a cutscene prop's placement is parked at
/// the cutscene origin while its clip carries the bones metres away.
///
/// The bones are the published block (`sub_821F56F8` at 0x8220DBF0 after `sub_82204FF0`,
/// model space after `sub_821F57D0`): holder `*(comp6+0x24)`, block `*holder`, tag block+8 ==
/// skel+0x14 (skel = comp6+0x94), 48-byte elements at `*(block+0x10)` with the translation at
/// +0x10, count `(skel+0x30 - skel+0x2C) >> 3` — as RefiiHostReadEntityRig reads them. The
/// guest poses every rig with live clips (KeepAnimatedInView above), so the block is current.
///
/// `out` = centre xyz in the rig's MODEL space (the space the shell draws the skeleton in) and
/// the radius in metres. The model origin is always inside, so the sphere covers the origin the
/// shell tested before. Returns 1 when stated, 0 with no published pose.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityViewSphere(uint32_t entity, float* out) {
    constexpr uint8_t kAnimatedMeshId = 6u;
    constexpr uint32_t kMaxBones = 256u;
    constexpr float kMaxReach = 1000.0f;

    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || out == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }

    const auto plausible = [](uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; };
    const uint32_t component = EntityComponent(base, entity, kAnimatedMeshId);
    const uint32_t skeleton = component != 0u ? GuestWordAt(base, component + 0x94u) : 0u;
    const uint32_t holder = component != 0u ? GuestWordAt(base, component + 0x24u) : 0u;
    if (!plausible(skeleton) || !plausible(holder)) {
        return 0u;
    }

    const uint32_t published = GuestWordAt(base, holder);
    if (!plausible(published) || GuestWordAt(base, published + 8u) != GuestWordAt(base, skeleton + 0x14u)) {
        return 0u;
    }

    const uint32_t buffer = GuestWordAt(base, published + 0x10u);
    const uint32_t table_begin = GuestWordAt(base, skeleton + 0x2Cu);
    const uint32_t table_finish = GuestWordAt(base, skeleton + 0x30u);
    if (!plausible(buffer) || table_finish <= table_begin) {
        return 0u;
    }

    const uint32_t bones = std::min((table_finish - table_begin) >> 3, kMaxBones);
    static thread_local uint8_t block[kMaxBones * 48u];
    if (!CopyGuest(base, buffer, block, (size_t)bones * 48u) || GuestWordAt(base, holder) != published) {
        return 0u;  // swapped during the copy; the next call reads the new block
    }

    // Entries past `bones` are never read: no 3 KB of zeroes per rig per guest frame.
    float points[kMaxBones + 1u][3];
    points[0][0] = points[0][1] = points[0][2] = 0.0f;
    float low[3] = {0.0f, 0.0f, 0.0f};
    float high[3] = {0.0f, 0.0f, 0.0f};
    for (uint32_t i = 0u; i < bones; ++i) {
        for (uint32_t axis = 0u; axis < 3u; ++axis) {
            uint32_t bits = 0u;
            std::memcpy(&bits, block + i * 48u + 0x10u + axis * 4u, sizeof(bits));
            bits = __builtin_bswap32(bits);
            float value = 0.0f;
            std::memcpy(&value, &bits, sizeof(value));
            if (!std::isfinite(value) || std::fabs(value) > kMaxReach) {
                return 0u;  // not a model-space pose
            }

            points[i + 1u][axis] = value;
            low[axis] = std::min(low[axis], value);
            high[axis] = std::max(high[axis], value);
        }
    }

    const float centre[3] = {(low[0] + high[0]) * 0.5f, (low[1] + high[1]) * 0.5f, (low[2] + high[2]) * 0.5f};
    float radius_sq = 0.0f;
    for (uint32_t i = 0u; i <= bones; ++i) {
        const float dx = points[i][0] - centre[0];
        const float dy = points[i][1] - centre[1];
        const float dz = points[i][2] - centre[2];
        radius_sq = std::max(radius_sq, dx * dx + dy * dy + dz * dz);
    }

    out[0] = centre[0];
    out[1] = centre[1];
    out[2] = centre[2];
    out[3] = std::sqrt(radius_sq);
    return 1u;
}

/// THE STATED SHADOW CASTING. The renderable (component 3) holds its definition's flag word at
/// +0x8C (`sub_8257CB20`, `sub_828E7798`, `sub_8293AA70`: 0x40 = ShadowCaster off, 0x80 =
/// DontCastDistantShadows; `sub_825720A8`: 0x8000 EnableLightZoneShadowCulling) and
/// DisableBackFaceCullingForShadows at +0xC1 (`sub_825720A8` 0x825721CC), which the static-mesh
/// descriptor adds as 0x100 (`sub_822370A8`). The twin init turns the word into the cascade mask
/// (`sub_82AA3C78`: near unless 0x40, far unless 0x40 or 0x80) and the 0x100 into twin+0x110, which
/// the cascade pass draws with culling off (`sub_8221EE60` 0x8221EF10).
/// Returns the word with 0x100 for a two-sided caster, or 0xFFFFFFFF without the component.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityCasterFlags(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0xFFFFFFFFu;
    }
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    if (renderable == 0u) {
        return 0xFFFFFFFFu;
    }
    uint32_t flags = GuestWordAt(base, renderable + 0x8Cu) & ~0x100u;
    if (GuestByteAt(base, renderable + 0xC1u) != 0u) {
        flags |= 0x100u;
    }
    return flags;
}

/// THE STATED DRAW DISTANCE. The renderable (component 3) holds, from its GDB definition,
/// `MaxDrawDistance` times the class multiplier at +0x28 and `DrawDistanceFadeRangeFactor` at
/// +0x24 (`sub_82572438` @0x825724cc / @0x825724e0, the first call of the loader
/// `sub_825720A8`). The multipliers (`flt_83319464..70`) are script-settable, so the product is
/// read here, never recomputed. The draw gathers (`sub_82219AB8` animated, `sub_8221E5F0`
/// rigid) fade the instance out from D·(1 − factor) to D and cut its shadow hard at D
/// (0x82219b28: shadow pass, `d > obj+0x124` returns).
/// Returns 1 and writes both when the entity carries the component, else 0.
REFII_HOST_ABI_EXPORT int RefiiHostGetEntityDrawRange(uint32_t entity, float* out_distance,
                                                      float* out_fade_factor) {
    if (out_distance == nullptr || out_fade_factor == nullptr) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    if (renderable == 0u) {
        return 0;
    }
    const auto readf = [base](uint32_t at) -> float {
        const uint32_t raw = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };
    *out_fade_factor = readf(renderable + 0x24u);
    *out_distance = readf(renderable + 0x28u);
    return 1;
}

/// THE STATED CLOTH DISTANCE. The renderable (component 3) holds its definition's
/// `MaxClothDistance` at +0x2C (TU1 `sub_82572438` @0x825724f4, disc `sub_825470E0` @0x8254719c).
/// The twin is built from the renderable's ten floats at +0x24: `addi r10, r30, 0x24`
/// (TU1 0x822372d4 in `sub_822370A8`, disc 0x82237314) into `sub_82A442E8`, whose loop
/// (0x82a44350) copies them to descriptor+0x3C; the animated gather copies the same ten
/// (0x82209938, `sub_82209480`). The twin's init (`sub_82AA2A68`) hands descriptor+0x3C to
/// vtable+0x60, `sub_82AA2B30` (disc `sub_82A985E8`), which stores block[1] at twin+0x124 and
/// block[2] at twin+0x128. The cloth gate `sub_821E34B0` (disc `sub_821E3070`) loads twin+0x128
/// (`lfs f31, 0x128(r30)`, r30 = proxy+0xC), takes the distance from the twin's translation
/// (twin+0x150) to the view's eye (view+0x50) and steps only below it (`fcmpu f30, f31` / `bge`).
/// A twin whose renderable never states one keeps the constructor's `flt_83317D74` (20).
/// Returns 1 and writes the distance when the entity carries the component, else 0.
REFII_HOST_ABI_EXPORT int RefiiHostGetEntityClothDistance(uint32_t entity, float* out_distance) {
    if (out_distance == nullptr) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    if (renderable == 0u) {
        return 0;
    }
    const uint32_t raw = GuestWordAt(base, renderable + 0x2Cu);
    std::memcpy(out_distance, &raw, sizeof(*out_distance));
    return 1;
}

/// How many words RefiiHostGetBoneMorph writes per entry: bone, offset xyz, scale xyz.
#define REFII_BONE_MORPH_WORDS 7u

/// THE BODY'S PROPORTIONS. After the fold the guest runs `sub_822BF9B8(pose, skel, tier, rec)`
/// with the entity's combined skeletal morph record — the smart pointer at comp6+152 — whose
/// entries (48 bytes: int bone @0, float4 offset @16, float4 scale @32; vector at rec+20..+24,
/// bone count at rec+12 which `sub_822BF9B8` requires to equal the skeleton's parent table;
/// sorted by bone, only bones that differ from identity) add a translation offset and a scale
/// per bone: `t += off / parent.scale` (bone 1 with rec+36 > 0 scales x/y and adds off.z), then
/// `s.xyz *= sc`. The record is rebuilt by the GraphicAppearanceMorph component's commit
/// (`sub_825B9060`) from the GDB SkeletalMorphs weighted by the live strengths — fat, thin,
/// strength, skill, will, morality, age — so it changes as the character does; it starts as
/// the model's bind record (comp6+160) and is never null for a skinned model. The guest's
/// model-space compose carries no scale (`sub_821F57D0` composes quat and translation only),
/// so a bone's scale is ITS OWN, not inherited; `sub_821E5AC0` pre-multiplies each child's
/// translation by its parent's scale instead. Words per entry: [0] bone index in the guest
/// skeleton, [1..3] offset xyz bits, [4..6] scale xyz bits. Returns the entry count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorph(uint32_t entity, uint32_t* out_words,
                                                     uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < REFII_BONE_MORPH_WORDS) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xE0000000u;
    };
    const auto read32 = [&](uint32_t address) { return GuestWordAt(base, address); };
    if (!plausible(entity)) {
        return 0u;
    }
    // Component 6 by the entity's own lookup: presence bit, then the accelerator, then the
    // vector — the same walk RefiiHostGetEntityAnimState makes for component 19.
    constexpr uint32_t kMeshId = 6u;
    if (((read32(entity + 0x24u) >> kMeshId) & 1u) == 0u) {
        return 0u;
    }
    const uint32_t mesh = EntityComponent(base, entity, kMeshId);
    if (!plausible(mesh)) {
        return 0u;
    }
    const uint32_t record = read32(mesh + 152u);
    if (!plausible(record)) {
        return 0u;
    }
    const uint32_t entries = read32(record + 20u);
    const uint32_t entries_end = read32(record + 24u);
    if (!plausible(entries) || entries_end < entries || (entries_end - entries) > 48u * 512u) {
        return 0u;
    }
    const uint32_t max_entries = word_capacity / REFII_BONE_MORPH_WORDS;
    uint32_t written = 0u;
    for (uint32_t at = entries; at + 48u <= entries_end && written < max_entries; at += 48u) {
        uint32_t* out = out_words + (written * REFII_BONE_MORPH_WORDS);
        out[0] = read32(at + 0u);
        out[1] = read32(at + 16u);
        out[2] = read32(at + 20u);
        out[3] = read32(at + 24u);
        out[4] = read32(at + 32u);
        out[5] = read32(at + 36u);
        out[6] = read32(at + 40u);
        ++written;
    }
    return written;
}

/// The morph record's height factor `h` (rec+36, float bits), 0 when there is none. With
/// h > 0 the guest rewrites bone 1 as t = (t.x*h, t.y*h, t.z + off.z) and skips the parent-scale
/// divide (`sub_822BF9B8`, 0x822bfa4c..0x822bfa9c). Written by `sub_82B7D9B0` for a model with
/// a RetargetSkeletonFile, zero otherwise.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorphHeight(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr || entity < 0x10000u || entity >= 0xE0000000u) {
        return 0u;
    }
    constexpr uint32_t kMeshId = 6u;
    if (((GuestWordAt(base, entity + 0x24u) >> kMeshId) & 1u) == 0u) {
        return 0u;
    }
    const uint32_t mesh = EntityComponent(base, entity, kMeshId);
    if (mesh < 0x10000u || mesh >= 0xE0000000u) {
        return 0u;
    }
    const uint32_t record = GuestWordAt(base, mesh + 152u);
    if (record < 0x10000u || record >= 0xE0000000u) {
        return 0u;
    }
    return GuestWordAt(base, record + 36u);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimMask(uint32_t node, uint8_t* out_bytes, uint32_t capacity,
                                                    uint32_t* out_skeleton_id) {
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = 0u;
    }
    if (out_bytes == nullptr || capacity == 0u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xE0000000u;
    };
    if (!plausible(node) || GuestWordAt(base, node + 20u) != 4u) {
        return 0u;
    }
    const uint32_t begin = GuestWordAt(base, node + 36u);
    const uint32_t end = GuestWordAt(base, node + 40u);
    if (!plausible(begin) || end < begin || end - begin > 1024u) {
        return 0u;
    }
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = GuestWordAt(base, node + 28u);
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written < capacity; ++at) {
        out_bytes[written++] = GuestByteAt(base, at);
    }
    return written;
}

/// The guest's STATED animation for one rig — the inputs to its own blend tree, so the host
/// can play the clips itself and own animation the way it owns the UI.
///
/// The guest does not sample through Havok: component 19 (the animation controller) holds a
/// std::list of LAYER RECORDS at `comp19+96` (sentinel at
/// +100, size at +104; node{next +0, record +8}); a second list at +108/+112/+116 carries
/// masked overlay tracks. Every offset below was decompiled for this export:
///   record (`sub_821B7BA0`): +8 priority (128 = full override), +12 track, +16 mask,
///     +20 weight, +24 previous weight, +28 state (0 live, 1 finishing, 2 dead), +36 node
///   node (`sub_822DC980`): +16 weight, +20 type;
///     0 clip leaf: +24 clip hash (rex_lhResourceName hash of the lower-cased name, the
///       animation TOC's key), +28 previous time, +32 current time (seconds), +36 loop byte
///     1 crossfade: +24 A, +28 B, +32 t          6 crossfade with per-child alphas +36/+40
///     2 weighted set: vector +28..+32 of children, each weighted by child+16
///     3 additive: +28 base, +24 additive layer (x child+16)   5 multiply: +28 base, +24 mult
///     4 bone permute: +24 child
///   top record (`sub_822013A8`): highest priority whose state != 2.
/// The fold the guest applies (`sub_82211660`): a priority-128 record overrides everything;
/// otherwise every live record contributes by its weight. This export FLATTENS each record's
/// tree to its clip leaves with the product of the weights along the path, tagging additive
/// leaves, and leaves the blend to the host.
///
/// TWO LISTS, TWO ROLES (`sub_82211660`, `sub_82232BF8`, `sub_8220CB18`): list 1 at +96 holds
/// PRIMARIES — one wins by priority (ties: first in the list), and the guest crossfades it
/// against the highest-weighted other primary by the winner's own weight; every bone the
/// winner's clip lacks is the skeleton REST pose (`sub_822351B0`, skel+144). List 2 at +108
/// holds OVERLAYS, sorted by track then priority, each gated by `(record+16 & top.priority)`
/// (+16 is a PRIORITY BITMASK, not a bone mask) and composited PER BONE through the type-4
/// mask node inside its own tree: `wb = mask[bone]/255 * weight`, rotation slerp, translation
/// and scale lerp, a zero byte leaving the base bone untouched. Folding an overlay as a
/// primary snaps every bone its clip does not cover to rest — the villager walking in a T pose
/// while waving.
///
/// Out words, REFII_ANIM_LEAF_WORDS (12) per leaf: [0] clip hash, [1] current time bits,
/// [2] previous time bits, [3] weight bits, [4] flags (bit0 loop, bit1 additive, bit2 OVERLAY,
/// bits 8..15 record priority, bits 16..23 track), [5] record uid, [6] blend-in seconds
/// bits, [7] blend-out seconds bits (the record's ramp objects at +40/+48: `+4` is the step
/// per 15 Hz tick, 1/(seconds·15); 0 when absent), [8] the nearest mask node above the leaf
/// (0 = none; read it with RefiiHostGetAnimMask), [9] the record's priority bitmask,
/// [10] the record's weight_previous bits, [11] its weight_current bits (1.0 for the +156
/// tree, which is not a record).
/// Returns the leaf count (0 = no controller or nothing playing). `out_flags`, when given:
/// bit0 the guest's bone-count LOD tier (`comp19+148 == 3`: within the LOD radius and recently
/// visible), bit1 has component 97 (dog paw/look IK, a procedural layer), bit2 the constant
/// `comp19+576` byte (always 1, carries nothing; kept so the numbering holds), bit3 physics owns
/// the pose (ragdoll mode 3 or 4). The replicated tree at +156 IS walked: it is the root when
/// the guest's own choice (`sub_82208D70`) picks it over the layer lists.

/// Why a rig had no clip leaves, split by cause — see RefiiHostGetEntityAnimState.
std::atomic<uint64_t> RefiiAnimNoComponentBit{0};
std::atomic<uint64_t> RefiiAnimComponentUnresolved{0};
std::atomic<uint64_t> RefiiAnimControllerFound{0};
std::atomic<uint64_t> RefiiAnimEmptyList{0};

/// Whether the guest's physics owns this entity's bones this tick: the ragdoll object reached as
/// `sub_8220A700` reaches it (entity+0x7C -> slot 23 -> `sub_8220A610`), enabled, in mode 3 or 4.
///
/// Modes 3 and 4 only — the readback's own test, at both of its sites:
///     8220ab88  lwz r11, 0x20(r?) / cmpwi 4 / cmpwi 3      ; sub_8220A700 gates the copy
///     8257dc24  lwz r11, 0x20(r29); cmpwi cr6, r11, 4 / 3  ; sub_8257DBC0 gates it again
/// Mode 2 ("SetKeyframed") is the opposite direction: the branch at 0x822053A0 that takes 2
/// and 3 together pushes the animation into the bodies (`sub_8227D920` / `sub_828C8D18`) and
/// never writes a bone back.
///
/// The host cannot call a guest virtual; slot 23 is `sub_828D4110` = `lwz r3,0x20(r3); blr`,
/// so `*(P+0x20)` is the same read. The vtable test on the ragdoll (0x820067F8, from its
/// constructor) is stricter than the guest's slot-2 predicate and fails closed.
static bool EntityPhysicsOwnsPose(const uint8_t* base, uint32_t entity) {
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const uint32_t kRagdollVtable = refii::guest::Address(0x820067F8u);
    if (!plausible(entity)) {
        return false;
    }

    const uint32_t navigator = GuestWordAt(base, entity + 0x7Cu);
    const uint32_t proxy = plausible(navigator) ? GuestWordAt(base, navigator + 0x20u) : 0u;
    if (!plausible(proxy)) {
        return false;
    }

    const uint32_t begin = GuestWordAt(base, proxy + 8u);
    const uint32_t end = GuestWordAt(base, proxy + 12u);
    if (!plausible(begin) || end < begin || (end - begin) > 8u * 64u || ((end - begin) % 8u) != 0u) {
        return false;
    }

    for (uint32_t at = begin; at < end; at += 8u) {
        const uint32_t ragdoll = GuestWordAt(base, at);
        if (!plausible(ragdoll) || GuestWordAt(base, ragdoll) != kRagdollVtable) {
            continue;
        }

        const uint32_t mode = GuestWordAt(base, ragdoll + 0x20u);
        return GuestByteAt(base, ragdoll + 0x24u) != 0u && (mode == 3u || mode == 4u);
    }

    return false;
}

/// Rigs posed by a linked animation (component 191) rather than their own controller; run total.
std::atomic<uint64_t> RefiiAnimLinked{0};

/// THE CLIP A LINKED ANIMATION PLAYS — component 191, as `sub_8253B608` evaluates it — written as
/// one anim-state leaf (REFII_ANIM_LEAF_WORDS words), or 0 when the guest would pose nothing.
/// See the caller for the chain.
static uint32_t EntityLinkedAnimLeaf(const uint8_t* base, uint32_t entity, uint32_t* out) {
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto readf = [read32](uint32_t at) -> float {
        const uint32_t raw = read32(at);
        float value;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    const uint32_t linked = EntityComponent(base, entity, 191u);
    if (!plausible(linked)) {
        return 0u;
    }

    // `sub_82173E38(comp191+12)`: the control block at +16, whose first word is the entity.
    const uint32_t control = read32(linked + 16u);
    const uint32_t source = plausible(control) ? read32(control) : 0u;
    if (!plausible(source) || (GuestByteAt(base, source + 144u) & 0x40u) == 0u) {
        return 0u;
    }

    const uint32_t controller = EntityComponent(base, source, 19u);
    if (!plausible(controller)) {
        return 0u;
    }

    // `sub_82232070`: type 0 is the leaf; 1 takes +28 at t >= 0.5 else +24; 3 and 4 take +24;
    // anything else has no dominant leaf.
    const auto dominant = [&](uint32_t node) -> uint32_t {
        for (int depth = 0; depth < 32 && plausible(node); ++depth) {
            switch (read32(node + 20u)) {
                case 0u:
                    return node;
                case 1u:
                    node = read32(node + (readf(node + 32u) >= 0.5f ? 28u : 24u));
                    break;
                case 3u:
                case 4u:
                    node = read32(node + 24u);
                    break;
                default:
                    return 0u;
            }
        }
        return 0u;
    };

    uint32_t leaf = 0u;
    bool from_tree = false;
    float time = 0.0f;
    float previous = 0.0f;
    const uint32_t tree = read32(controller + 156u);
    if (plausible(tree)) {
        leaf = dominant(tree);
        if (leaf == 0u) {
            return 0u;
        }
        // `LHTiming_GetTime() - comp19+164 + leaf+32` (0x8253b7ec..0x8253b800): seconds since the
        // tree's receipt, run on from the leaf's current time.
        const auto read_double = [&](uint32_t at) {
            const uint64_t bits = (static_cast<uint64_t>(read32(at)) << 32) | read32(at + 4u);
            double value = 0.0;
            std::memcpy(&value, &bits, sizeof(value));
            return value;
        };
        const double time_base = read_double(refii::guest::Address(0x834970A0u));
        const double frequency = read_double(refii::guest::Address(0x834970A8u));
        if (!std::isfinite(frequency) || frequency <= 0.0 || !std::isfinite(time_base)) {
            return 0u;
        }
        const double now = (static_cast<double>(refii_bridge_rex_query_guest_tick_count()) - time_base) / frequency;
        const float current = readf(leaf + 32u);
        float step = current - readf(leaf + 28u);
        if (!(step > 0.0f) || !std::isfinite(step)) {
            step = 1.0f / 15.0f;
        }
        time = static_cast<float>(now - readf(controller + 164u)) + current;
        previous = time - step;
        from_tree = true;
    } else {
        // `sub_822013A8`: the live record (state +28 != 2) of highest priority (+8).
        const uint32_t sentinel = read32(controller + 100u);
        const uint32_t size = read32(controller + 104u);
        if (!plausible(sentinel) || size == 0u || size > 64u) {
            return 0u;
        }
        uint32_t winner = 0u;
        int32_t best = -1;
        uint32_t node = read32(sentinel);
        for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
            const uint32_t record = read32(node + 8u);
            node = read32(node);
            if (!plausible(record) || read32(record + 28u) == 2u) {
                continue;
            }
            const int32_t priority = static_cast<int32_t>(read32(record + 8u));
            if (priority >= best) {
                best = priority;
                winner = record;
            }
        }
        leaf = winner != 0u ? dominant(read32(winner + 36u)) : 0u;
        if (leaf == 0u) {
            return 0u;
        }
        time = readf(leaf + 32u);
        previous = readf(leaf + 28u);
    }

    if (!std::isfinite(time) || !std::isfinite(previous)) {
        return 0u;
    }

    // comp191's map (`std::map` head at +28): lower_bound on node+12, then an exact match.
    const uint32_t source_key = read32(leaf + 24u);
    const uint32_t head = read32(linked + 28u);
    if (!plausible(head)) {
        return 0u;
    }
    uint32_t found = head;
    uint32_t at = read32(head + 4u);
    for (int steps = 0; steps < 64 && plausible(at) && GuestByteAt(base, at + 21u) == 0u; ++steps) {
        if (read32(at + 12u) >= source_key) {
            found = at;
            at = read32(at);
        } else {
            at = read32(at + 8u);
        }
    }
    if (found == head || source_key < read32(found + 12u)) {
        return 0u;
    }
    const uint32_t key = read32(found + 16u);
    if (key == 0u) {
        return 0u;
    }

    const float one = 1.0f;
    uint32_t one_bits;
    std::memcpy(&one_bits, &one, sizeof(one_bits));
    std::memcpy(&out[1], &time, sizeof(out[1]));
    std::memcpy(&out[2], &previous, sizeof(out[2]));
    out[0] = key;
    out[3] = one_bits;
    // Looping (`stb 1` into the stack node's +36), and flagged as a network extrapolation when the
    // time ran on from a receipt, so the host does not read a stalled pair as a cut.
    // Flag 16 marks this as the sole winner leaf.
    out[4] = 1u | (from_tree ? 8u : 0u) | 16u;
    // The leaf's identity: the linked component, never a record uid of the rig.
    out[5] = linked;
    out[6] = 0u;
    out[7] = 0u;
    out[8] = 0u;
    out[9] = 0xFFFFFFFFu;
    out[10] = one_bits;
    out[11] = one_bits;
    return 1u;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityAnimState(uint32_t entity, uint32_t* out_words, uint32_t word_capacity,
                                                            uint32_t* out_flags) {
    if (out_flags != nullptr) {
        *out_flags = 0u;
    }
    if (out_words == nullptr || word_capacity < REFII_ANIM_LEAF_WORDS) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    // The same bound `EntityComponent` enforces (below 0xC0000000), so the walk-stats split
    // agrees with the fetch.
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto readf = [read32](uint32_t at) -> float {
        const uint32_t raw = read32(at);
        float value;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };
    if (!plausible(entity)) {
        return 0u;
    }
    const auto component = [&](uint32_t id) -> uint32_t {
        const uint32_t word = read32(entity + 0x24u + ((id / 32u) * 4u));
        if (((word >> (id % 32u)) & 1u) == 0u) {
            return 0u;
        }
        return EntityComponent(base, entity, id);
    };

    // Why a rig has no clips, counted by cause: no component-19 bit, component unresolved, or
    // controller found (with `RefiiAnimEmptyList` for an empty leaf list).
    const uint32_t present_word = read32(entity + 0x24u);
    if (((present_word >> 19u) & 1u) == 0u) {
        // A LINKED ANIMATION. With no component 19, `sub_8220D1B8` takes `beq loc_8220D98C`
        // (0x8220d904) and poses the rig from component 191 instead (entity+0x38 bit 31, id 0xBF
        // at 0x8220d9f4; `sub_8253B608` called at 0x8220da9c, its return is the pose).
        // `sub_8253B608`: the source entity is the weak ref at comp191+12/+16 (`sub_82173E38`:
        // `**(comp191+16)`), live only with entity+144 bit 0x40; its component 19's dominant leaf
        // is `sub_82231FD0(comp19+156)` when a tree is installed — time `leaf+32 +
        // (LHTiming_GetTime() - comp19+164)` — else `sub_82201518`: the winning record
        // (`sub_822013A8`) reduced by `sub_82232070` — time `leaf+32`. The leaf's key is looked up
        // in comp191's map (head at +28; node +12 key, +16 value, +21 nil) and the VALUE is the
        // clip played, one type-0 node at weight 1, looping, `tPrev = tCur = t`, into comp191+36.
        // Stated here as one leaf; the previous time is the source's own, so the host has the
        // rate.
        const uint32_t linked = EntityLinkedAnimLeaf(base, entity, out_words);
        if (linked != 0u) {
            if (out_flags != nullptr && EntityPhysicsOwnsPose(base, entity)) {
                *out_flags = 8u;
            }
            RefiiAnimLinked.fetch_add(1u, std::memory_order_relaxed);
            return linked;
        }
        RefiiAnimNoComponentBit.fetch_add(1u, std::memory_order_relaxed);
        return 0u;
    }

    const uint32_t controller = component(19u);
    if (!plausible(controller)) {
        RefiiAnimComponentUnresolved.fetch_add(1u, std::memory_order_relaxed);
        return 0u;
    }

    RefiiAnimControllerFound.fetch_add(1u, std::memory_order_relaxed);
    // FLAG 1 IS THE GUEST'S BONE-COUNT TIER, NOT AN ACTIVE FLAG. `comp19+148` is 1 or 3:
    // `sub_821F2688` sets 3 when the rig is within its LOD radius (`comp6+220`, 10 m by
    // default) and recently visible, else 1, and `sub_8220D1B8` (0x8220D684) then SKIPS the
    // guest's own pose update for a tier-1 non-hero rig — its list at +96 stays live and is
    // read here regardless. The 360 froze far rigs; we hold the clips and need not.
    uint32_t flags = 0u;
    if (read32(controller + 148u) == 3u) {
        flags |= 1u;
    }
    // Flag 4 carries no information and is kept only so the bit numbering does not move: the
    // constructor `sub_82562CC8` writes 1 to `comp19+576` on every controller
    // (`stb r25, 0x240(r31)`) and its only other accessors are a plain getter and setter.
    // Do not gate anything on it.
    if (read8(controller + 576u) != 0u) {
        flags |= 4u;
    }
    // FLAG 8: THE POSE IS PHYSICS'S, NOT THE LIST'S. Havok writes the finished pose into
    // comp6+44 and a host that keeps folding clips over it blows the body apart — an
    // electrocuted bandit coming to pieces.
    //
    // THIS DID NOT USE TO READ THE RAGDOLL AT ALL. It tested bit 2 of the spatial component's
    // word at comp3+144, and that was wrong twice over. First the width: `sub_82204FF0` loads
    // it with `lwz r11, 0x90(r10)` at 0x8220525C, a 32-BIT WORD, so on the big-endian guest
    // value-bit 2 lives at byte +147 and a raw byte read of +144 takes the MSB and can never
    // see it — measured, the fallback counter sat at exactly 0 over 444,813 clip-driven
    // frames. Second, and worse, that bit is not a physics flag: the branch at 0x82205268
    // skips the pose update when it is CLEAR, so it means "pose this skeleton this frame", an
    // ENABLE that is true for nearly every animated rig. Correcting only the width would have
    // handed every character to the pose feed and stopped all authored animation.
    //
    // The real state is the ragdoll object's mode, and only 3 and 4 hand the bones to physics
    // — see `EntityPhysicsOwnsPose` for the chain and the readback's own test.
    if (EntityPhysicsOwnsPose(base, entity)) {
        flags |= 8u;
    }
    if (out_flags != nullptr) {
        *out_flags = flags;
    }

    const uint32_t max_leaves = word_capacity / REFII_ANIM_LEAF_WORDS;
    uint32_t leaves = 0u;
    uint32_t ramp_in_bits = 0u;
    uint32_t ramp_out_bits = 0u;
    // The record's stated weight pair (+0x18 previous, +0x14 current), carried unscaled onto
    // every leaf of that record: the guest's crossfade factor uses the record's own values,
    // while the tree factors below scale the leaf's share.
    uint32_t record_prev_bits = 0u;
    uint32_t record_cur_bits = 0u;
    const auto ramp_seconds = [&](uint32_t ramp) -> uint32_t {
        if (!plausible(ramp)) {
            return 0u;
        }
        const float step = readf(ramp + 4u);
        float seconds = step > 1e-6f && std::isfinite(step) ? 1.0f / (step * 15.0f) : 0.0f;
        if (!(seconds > 0.0f) || seconds > 5.0f) {
            seconds = 0.0f;
        }
        uint32_t bits;
        std::memcpy(&bits, &seconds, sizeof(bits));
        return bits;
    };

    // Flatten one tree, depth-first, bounded against a torn read.
    struct Frame {
        uint32_t node;
        float weight;
        bool additive;
        uint32_t mask;  // the nearest type-4 mask node above this one, 0 when none
    };
    Frame stack[64];
    bool overlay_list = false;
    uint32_t priority_mask = 0u;
    // A REPLICA'S CLIP TIME IS A WALL-CLOCK EXTRAPOLATION, NOT THE NODE'S PAIR. The tree at
    // +156 arrives from the host (`sub_825657C8`, net messages 174/175) with each leaf's
    // `+28`/`+32` as the host's last graph step, and those words change only when the next
    // packet lands — every 100 ms for a nearby rig whose clips changed, 500 ms when they did
    // not. The guest's own sampler does not read them as a per-tick pair: `sub_82563B78`
    // (0x82563c04..0x82563c48) computes
    //     alpha = (((double)mftb - dbl_834970A0) / dbl_834970A8 - comp19+164) * 15.0
    // and samples `lerp(+28, +32, alpha)`, so the clip runs on in 1/15 s steps of the guest's
    // clock between packets (`+164` is the receipt time, `LHTiming_GetTime` at receipt). Read
    // as a pair, the leaf stood still for 100 ms and jumped — every rig but the local hero on
    // a client at ten frames a second. Stated here as the guest samples it: the current time
    // at alpha and the previous one a whole alpha step back. The animator folds a pair once
    // per GAME tick (`Engine_TickClock.GuestGameTick`, 15 Hz, `dword_83319650`) and alpha
    // advances by exactly 1.0 per game tick, so the previous value must be one graph step
    // back for consecutive folds to join; half a step read as a re-time every tick and cut
    // the timeline on each (run 2026-10-07 20:31: the joiner's town slid without animating).
    // A looping pair that wrapped (`+32 < +28`) keeps the guest's own step, 1/15 s.
    bool net_extrapolate = false;
    float net_alpha = 0.0f;
    const auto flatten = [&](uint32_t root, float weight, uint32_t priority, uint32_t track, uint32_t uid,
                             bool is_winner = false, bool is_runner = false) {
        int depth = 0;
        stack[depth++] = Frame{root, weight, false, 0u};
        int steps = 0;
        while (depth > 0 && leaves < max_leaves && ++steps < 512) {
            const Frame frame = stack[--depth];
            if (!plausible(frame.node)) {
                continue;
            }
            const uint32_t type = read32(frame.node + 20u);
            const auto push = [&](uint32_t child, float w, bool add, uint32_t mask) {
                if (depth < 64 && plausible(child)) {
                    stack[depth++] = Frame{child, w, add, mask};
                }
            };
            switch (type) {
                case 0u: {
                    uint32_t* out = out_words + (leaves * REFII_ANIM_LEAF_WORDS);
                    out[0] = read32(frame.node + 24u);
                    out[1] = read32(frame.node + 32u);
                    out[2] = read32(frame.node + 28u);
                    if (net_extrapolate) {
                        const float prev = readf(frame.node + 28u);
                        const float cur = readf(frame.node + 32u);
                        float step = cur - prev;
                        if (!(step > 0.0f) || !std::isfinite(step)) {
                            step = 1.0f / 15.0f;
                        }
                        const float sampled = cur + step * (net_alpha - 1.0f);
                        const float before = sampled - step;
                        if (std::isfinite(sampled) && std::isfinite(before)) {
                            std::memcpy(&out[1], &sampled, sizeof(out[1]));
                            std::memcpy(&out[2], &before, sizeof(out[2]));
                        }
                    }
                    uint32_t weight_bits;
                    std::memcpy(&weight_bits, &frame.weight, sizeof(weight_bits));
                    out[3] = weight_bits;
                    // Bit 3: the times are the packet-alpha extrapolation above, whose
                    // continuity tick to tick is NOT stated (the receipt is an arrival time, and
                    // this read lands anywhere in the frame); the animator keeps its own timeline
                    // for such a clip and converges on these values.
                    // Bits 4 & 5 (16 & 32): explicit winner and runner flags from the guest's pick.
                    out[4] = (read8(frame.node + 36u) != 0u ? 1u : 0u) | (frame.additive ? 2u : 0u) |
                             (overlay_list ? 4u : 0u) | (net_extrapolate ? 8u : 0u) |
                             (is_winner ? 16u : 0u) | (is_runner ? 32u : 0u) |
                             ((priority & 0xFFu) << 8) | ((track & 0xFFu) << 16);
                    out[5] = uid;
                    out[6] = ramp_in_bits;
                    out[7] = ramp_out_bits;
                    out[8] = frame.mask;
                    out[9] = priority_mask;
                    out[10] = record_prev_bits;
                    out[11] = record_cur_bits;
                    ++leaves;
                    break;
                }
                case 1u:
                case 6u: {
                    float t = readf(frame.node + 32u);
                    if (!std::isfinite(t)) {
                        t = 0.0f;
                    }
                    t = std::clamp(t, 0.0f, 1.0f);
                    if (type == 6u) {
                        t = t * t * (3.0f - 2.0f * t);
                    }
                    // Match `Engine_Animation_EvalBlendNode` (`0x822DC980` lines 484..519):
                    // when `min(t, 1 - t) * weight <= 0.0001f`, the guest prunes the zero-weight
                    // branch and evaluates only `node + 28` (`t >= 0.5f`) or `node + 24` (`t < 0.5f`).
                    // Emitting dead `weight = 0` leaves after a footstep crossfade reaches `t = 1.0`
                    // kept stale footstep channels and slot indices alive on the host.
                    if (std::min(t, 1.0f - t) * frame.weight <= 0.0001f) {
                        if (t >= 0.5f) {
                            push(read32(frame.node + 28u), frame.weight, frame.additive, frame.mask);
                        } else {
                            push(read32(frame.node + 24u), frame.weight, frame.additive, frame.mask);
                        }
                    } else {
                        push(read32(frame.node + 24u), frame.weight * (1.0f - t), frame.additive, frame.mask);
                        push(read32(frame.node + 28u), frame.weight * t, frame.additive, frame.mask);
                    }
                    break;
                }
                case 2u: {
                    const uint32_t begin = read32(frame.node + 28u);
                    const uint32_t end = read32(frame.node + 32u);
                    if (!plausible(begin) || end < begin || end - begin > 4u * 64u) {
                        break;
                    }
                    float total = 0.0f;
                    for (uint32_t at = begin; at + 4u <= end; at += 4u) {
                        const uint32_t child = read32(at);
                        if (plausible(child)) {
                            total += readf(child + 16u);
                        }
                    }
                    for (uint32_t at = begin; at + 4u <= end; at += 4u) {
                        const uint32_t child = read32(at);
                        if (plausible(child)) {
                            const float w = readf(child + 16u);
                            const float cw = total > 1e-6f ? frame.weight * (w / total) : 0.0f;
                            if (cw > 1e-5f) {
                                push(child, cw, frame.additive, frame.mask);
                            }
                        }
                    }
                    break;
                }
                case 3u: {
                    const uint32_t base_child = read32(frame.node + 28u);
                    const uint32_t layer = read32(frame.node + 24u);
                    push(base_child, frame.weight, frame.additive, frame.mask);
                    if (plausible(layer)) {
                        const bool same_clip_leaf =
                            base_child == layer ||
                            (plausible(base_child) && read32(base_child + 20u) == 0u &&
                             read32(layer + 20u) == 0u &&
                             read32(base_child + 24u) == read32(layer + 24u));
                        float node_w = readf(frame.node + 16u);
                        if (!std::isfinite(node_w)) {
                            node_w = 1.0f;
                        }
                        node_w = std::clamp(node_w, 0.0f, 1.0f);
                        float layer_w = readf(layer + 16u);
                        if (!std::isfinite(layer_w)) {
                            layer_w = 1.0f;
                        }
                        layer_w = std::clamp(layer_w, 0.0f, 1.0f);
                        const float effective_w = frame.weight * node_w * layer_w;
                        if (!same_clip_leaf && effective_w > 1e-4f) {
                            push(layer, effective_w, true, frame.mask);
                        }
                    }
                    break;
                }
                case 4u:
                    // THE MASK NODE (`sub_82761CE8`): +24 child, +28 skeleton id, +36..+40 one
                    // byte per live bone. Its child's leaves carry this node so the host can
                    // fetch the bytes (RefiiHostGetAnimMask) and composite per bone.
                    push(read32(frame.node + 24u), frame.weight, frame.additive, frame.node);
                    break;
                case 5u:
                    // `Engine_Animation_EvalBlendNode` (`0x822DC980` at `0x822DD404`):
                    // Type-5 is the Delta Subtraction node (`pose(+28) (-) ref(+24)` via `0x82B72F50`),
                    // which sets `*out_is_additive = 1`.
                    push(read32(frame.node + 28u), frame.weight, true, frame.mask);
                    break;
                default:
                    break;
            }
        }
    };

    const auto walk_list = [&](uint32_t list, bool overlay) {
        const uint32_t sentinel = read32(list + 4u);
        const uint32_t size = read32(list + 8u);
        if (!plausible(sentinel) || size == 0u || size > 64u) {
            return;
        }

        // Walk the guest linked list once and cache valid record pointers on the stack so
        // winner/runner selection and flattening never re-traverse guest list nodes.
        uint32_t records[64];
        uint32_t record_count = 0u;
        uint32_t node = read32(sentinel);
        for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
            const uint32_t record = read32(node + 8u);
            node = read32(node);
            if (plausible(record)) {
                records[record_count++] = record;
            }
        }
        if (record_count == 0u) {
            return;
        }

        // One primary wins: the guest's own pick.
        //
        // `sub_822013A8` walks this list, skips records whose state (+28) is 2, and keeps the
        // highest priority (+8) with a strict `>` (0x82201464), returning that single record.
        // It walks backward (`lwz r8, 4(r10)` at 0x82201434 takes _Prev), so the record nearest
        // the tail wins a tie; `>=` on a forward walk is the same pick. `sub_82211660` folds only
        // that record (`sub_82229960(a2, &out, v7, alpha)`), or installs its tree outright when
        // its priority is 128.
        //
        // The runner-up is also exported: the guest crossfades the winner against it by the
        // winner's weight.
        uint32_t winner = 0u;
        int32_t winner_priority = -1;
        uint32_t runner_up = 0u;
        float runner_up_weight = 0.0f;
        if (!overlay) {
            for (uint32_t i = 0u; i < record_count; ++i) {
                const uint32_t record = records[i];
                if (read32(record + 28u) == 2u) {
                    continue;
                }
                const int32_t priority = static_cast<int32_t>(read32(record + 8u));
                if (priority >= winner_priority) {
                    winner_priority = priority;
                    winner = record;
                }
            }

            // The runner-up must clear 1e-4 to exist at all (0x82238070..0x82238074); below that
            // `sub_82237F90` emits nothing and the winner is used alone.
            if (winner != 0u && record_count > 1u) {
                for (uint32_t i = 0u; i < record_count; ++i) {
                    const uint32_t record = records[i];
                    // No state filter here, matching the guest: the runner-up scan `sub_82237F90`
                    // (0x82237FD8..0x82238048) excludes only the winner's node and never tests +0x1C.
                    // It ranks by +0x14 alone, forward with a strict `>`, so the first record wins
                    // equal weights.
                    if (record == winner) {
                        continue;
                    }
                    const float other = readf(record + 20u);
                    if (other > runner_up_weight) {
                        runner_up_weight = other;
                        runner_up = record;
                    }
                }
                if (runner_up_weight <= 1e-4f) {
                    runner_up = 0u;
                }
            }
        }

        for (uint32_t i = 0u; i < record_count; ++i) {
            const uint32_t record = records[i];
            if (!overlay && record != winner && record != runner_up) {
                continue;
            }
            const uint32_t state = read32(record + 28u);
            if (state == 2u && (!overlay ? record != runner_up : false)) {
                continue;
            }
            const uint32_t exported_prio = read32(record + 8u);
            const uint32_t rec_track = read32(record + 12u);
            const float weight = readf(record + 20u);
            if (weight <= 1e-4f) {
                if (!overlay && record == winner) {
                    // Retain winner even if cur weight is 0.0f on its opening tick,
                    // so the host registers the channel and folds runner_up -> winner.
                } else {
                    continue;
                }
            }
            record_prev_bits = read32(record + 24u);
            record_cur_bits = read32(record + 20u);
            ramp_in_bits = ramp_seconds(read32(record + 40u));
            ramp_out_bits = ramp_seconds(read32(record + 48u));
            overlay_list = overlay;
            priority_mask = read32(record + 16u);

            const bool is_winner = !overlay && record == winner;
            const bool is_runner = !overlay && record == runner_up;
            flatten(read32(record + 36u), weight <= 1e-4f ? 1e-4f : weight, exported_prio, rec_track, read32(record + 32u),
                    is_winner, is_runner);
        }
    };

    // THE GUEST'S OWN CHOICE OF ROOT (`sub_82208D70` 0x82208DB4): the tree at +156 plays when
    // it exists and either the layer list is empty or its highest priority is below 128;
    // otherwise the layer lists play. Same node types either way.
    const uint32_t root = read32(controller + 156u);
    const bool list_empty = read32(controller + 104u) == 0u;
    const bool root_wins = plausible(root) &&
                           (list_empty || static_cast<int32_t>(read32(controller + 604u)) < 128);
    if (root_wins) {
        ramp_in_bits = 0u;
        ramp_out_bits = 0u;
        overlay_list = false;
        priority_mask = 0xFFFFFFFFu;
        // The +156 tree is not a record and has no weight pair; it plays whole.
        record_prev_bits = 0x3F800000u;
        record_cur_bits = 0x3F800000u;
        // The guest's own alpha for this tree (see the note above `flatten`). `mftb` in
        // recompiled code is the payload's tick count, refii_bridge_rex_query_guest_tick_count —
        // the VIRTUAL tick under the driven time base, never the SDK's real
        // `Clock::QueryGuestTickCount`, which runs ahead by however far the world lags real
        // time (17 s on a joiner, 2026-10-20: every replica rig stated clip times far ahead and
        // jumping, so bodies slid under poses held on random frames). The two doubles are
        // LHTiming's base and frequency, stored high word first. A packet landing mid-walk re-bases +164 and
        // swaps the tree, so a walk whose receipt moved is done once more with the new one.
        const auto read_double = [&](uint32_t at) {
            const uint64_t bits = (static_cast<uint64_t>(read32(at)) << 32) | read32(at + 4u);
            double out = 0.0;
            std::memcpy(&out, &bits, sizeof(out));
            return out;
        };
        const double time_base = read_double(refii::guest::Address(0x834970A0u));
        const double frequency = read_double(refii::guest::Address(0x834970A8u));
        for (int attempt = 0; attempt < 2; ++attempt) {
            leaves = 0u;
            net_extrapolate = false;
            const float receipt = readf(controller + 164u);
            if (std::isfinite(frequency) && frequency > 0.0 && std::isfinite(time_base) &&
                std::isfinite(receipt)) {
                const double now = static_cast<double>(refii_bridge_rex_query_guest_tick_count());
                const double alpha = ((now - time_base) / frequency - receipt) * 15.0;
                // Bounded against a stale receipt: a minute of extrapolation is a tree the
                // host stopped sending, not a clip to run on.
                if (std::isfinite(alpha) && alpha > -1.0 && alpha < 15.0 * 60.0) {
                    net_extrapolate = true;
                    net_alpha = static_cast<float>(alpha);
                }
            }
            flatten(read32(controller + 156u), 1.0f, 0u, 0u, 0u, true, false);
            if (readf(controller + 164u) == receipt) {
                break;
            }
        }
        // The overlay list's records are LOCAL per-tick pairs; only the +156 tree is sampled
        // by the packet alpha.
        net_extrapolate = false;
        walk_list(controller + 108u, true);
        return leaves;
    }

    walk_list(controller + 96u, false);
    walk_list(controller + 108u, true);
    if (leaves == 0u) {
        RefiiAnimEmptyList.fetch_add(1u, std::memory_order_relaxed);
    }

    return leaves;
}

/// Why rigs are not clip-driven, split by cause. Four counters: the entity has no component-19
/// bit (and no linked animation), the bit is set but the component does not resolve, the
/// controller was found, and the controller was found but its lists flattened to nothing; a
/// fifth, when the capacity allows, counts rigs posed by a linked animation (component 191).
///
/// Separates "the guest has nothing to play" from "this walk misses the controller".
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimWalkStats(uint32_t* out_words, uint32_t capacity) {
    if (out_words == nullptr || capacity < 4u) {
        return 0u;
    }

    out_words[0] = static_cast<uint32_t>(RefiiAnimNoComponentBit.load(std::memory_order_relaxed));
    out_words[1] = static_cast<uint32_t>(RefiiAnimComponentUnresolved.load(std::memory_order_relaxed));
    out_words[2] = static_cast<uint32_t>(RefiiAnimControllerFound.load(std::memory_order_relaxed));
    out_words[3] = static_cast<uint32_t>(RefiiAnimEmptyList.load(std::memory_order_relaxed));
    if (capacity < 5u) {
        return 4u;
    }
    // A fifth word for a caller that asks: rigs a linked animation (component 191) posed.
    out_words[4] = static_cast<uint32_t>(RefiiAnimLinked.load(std::memory_order_relaxed));
    return 5u;
}

/// How many words RefiiHostGetAttachments writes per attachment record.
#define REFII_ATTACHMENT_WORDS 4u

/// How many bytes of dummy name it writes per record.
#define REFII_ATTACHMENT_NAME 64u

/// EVERY LIVE ATTACHMENT THE GUEST HAS STATED — which entity hangs on which entity's dummy.
///
/// A weapon, a hat carried in the hand, a prop: each is an ENTITY of its own, and the guest
/// re-derives its world placement every frame the parent moves (the attachment pass
/// `sub_822025E0`, after the animation pass in `sub_82278AE0`; composed by `sub_8250F5F0`,
/// written into the child's placement through vt+0x84 or vt+0x70/+0x74). That placement is
/// computed from the GUEST's bone pose. Godot now
/// plays the animation itself, so a child placed from the guest's pose swims against the body
/// it is supposed to be attached to — the weapons "bounce around and do not match the
/// character". The host has to hang the child off ITS OWN bone instead, and for that it needs
/// the guest's STATEMENT of the attachment rather than its result.
///
/// The statement lives on the PARENT, component 22 `CECObjectAttachment` (registered by
/// `sub_8265B680`, made by `sub_82649AA8`; records built by `sub_8250DF18` on the attach path
/// `sub_8250E9F8`):
///     att+4          the owner entity
///     att+16         the list sentinel; att+20 the count. Nodes are {next +0, record +8}.
///     record+4       handle slot; `*slot` is the CHILD entity
///     record+8       LHString dummy name on the PARENT, e.g. `Character.Carry.SheathWeaponBack`
///     record+12      dummy index — the ordinal among equally-named dummies
///     record+144     ticket, the id `CECDependantEntity` on the child joins back with
///
/// The dummy is a record in the parent model's own `.mdl.gmd` attachment table, which Godot
/// already parses; the child's own offset (record+0x50) comes from RefiiHostGetAttachmentGrips.
///
/// Words per record: [0] child entity, [1] parent entity, [2] dummy index, [3] ticket.
/// `out_names` takes REFII_ATTACHMENT_NAME bytes per record, NUL-padded. Returns the record
/// count, capped at `max_records`.
/// The attachment walk the attachment exports share: every live entity's component 22 records,
/// gated as the guest gates its own update, visited as (parent, record, child, slot) where slot
/// counts the visits so far. Stops at `max_records`. Returns the count visited.
extern "C++" {
template <typename Visit>
static uint32_t ForEachStatedAttachment(uint8_t* base, uint32_t max_records, Visit&& visit) {
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t manager = GuestWordAt(base, refii::guest::Address(0x83496B28u));
    if (!plausible(manager)) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t node = read32(manager + 8u);
    const uint32_t counted = read32(manager + 0x10u);
    const uint32_t guard = counted > 0u && counted < 65536u ? counted + 16u : 65536u;

    for (uint32_t steps = 0u; plausible(node) && written < max_records && steps < guard;
         ++steps) {
        uint32_t list_node[3];
        CopyGuest(base, node, list_node, sizeof(list_node));
        const uint32_t entity = __builtin_bswap32(list_node[2]);
        node = __builtin_bswap32(list_node[0]);

        if (!plausible(entity)) {
            break;
        }

        const EntityFields fields(GuestReader{base}, entity);
        if ((fields.Byte(0x90u) & 0x40u) == 0u) {
            continue;  // not LIVE
        }

        // The entity's component by id — the accelerator byte table at +0x8C when it is there,
        // the sorted {id, pointer} pairs at +0x48..+0x4C otherwise. Same walk the entity feed uses.
        const uint32_t attachment = EntityComponentOf(base, fields, 22u);
        if (!plausible(attachment)) {
            continue;
        }

        const uint32_t sentinel = read32(attachment + 16u);
        if (!plausible(sentinel)) {
            continue;
        }

        uint32_t link = read32(sentinel);
        for (uint32_t depth = 0u; depth < 64u && written < max_records; ++depth) {
            if (link == sentinel || !plausible(link)) {
                break;
            }

            const uint32_t record = read32(link + 8u);
            link = read32(link);
            if (!plausible(record)) {
                continue;
            }

            const uint32_t slot = read32(record + 4u);
            const uint32_t child = plausible(slot) ? read32(slot) : 0u;
            if (!plausible(child)) {
                continue;  // the handle has been released — the record is on its way out
            }

            // The guest's own gate: its attachment update places a record only for a child
            // whose live bit is set, so a child that fails it is not being placed by it.
            if ((read8(child + 144u) & 0x40u) == 0u) {
                continue;
            }

            visit(entity, record, child, written);
            ++written;
        }
    }

    return written;
}
}  // extern "C++"

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachments(uint32_t* out_words, uint32_t max_records,
                                                       char* out_names) {
    if (out_words == nullptr || max_records == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    return ForEachStatedAttachment(base, max_records,
        [&](uint32_t entity, uint32_t record, uint32_t child, uint32_t written) {
            uint32_t* words = out_words + (written * REFII_ATTACHMENT_WORDS);
            words[0] = child;
            words[1] = entity;
            words[2] = GuestWordAt(base, record + 12u);
            words[3] = GuestWordAt(base, record + 144u);

            if (out_names != nullptr) {
                char* slotName = out_names + (written * REFII_ATTACHMENT_NAME);
                std::memset(slotName, 0, REFII_ATTACHMENT_NAME);
                const std::string name = ReadGuestString(base, record + 8u);
                const std::size_t copied =
                    name.size() < (REFII_ATTACHMENT_NAME - 1u) ? name.size()
                                                               : (REFII_ATTACHMENT_NAME - 1u);
                std::memcpy(slotName, name.data(), copied);
            }
        });
}

/// How many words RefiiHostGetAttachmentGrips writes per record.
#define REFII_ATTACHMENT_GRIP_WORDS 18u

/// THE OFFSET EACH ATTACHED CHILD HANGS AT, AS THE GUEST STATES IT — so the host composes the
/// child's placement instead of solving for it from reads taken at different instants (a solve
/// that re-ran about once a second across a town and snapped a weapon each time).
///
/// The guest places a child in `sub_8250F5F0(att, out, record, W)`:
///     8250f60c  bl   sub_822374D0          ; D = the parent dummy, model space, live pose
///     8250f614  addi r4, r31, 0x50         ; A = record+0x50
///     8250f61c  bl   sub_8221ACC0          ; T = A . D   (row vectors: A applies first)
///     8250f62c  bl   sub_8221ACC0          ; out = T . W  (W = the parent's world placement)
/// record+0x50 is the rigid inverse (`sub_82260FA0`, transpose and negated row 3) of the CHILD's
/// own grip dummy at record+0x10, copied in by the attach path (`sub_8250DF18`). It is constant
/// for the attachment's life. The dummy D is the parent gmd entry's local, times the gmd node
/// locals up to the first live bone (`sub_8223D148`), times that bone's model-space pose — all of
/// which the host has from its own `.mdl.gmd` and its own pose.
///
/// Words per record, in the same order as RefiiHostGetAttachments: [0] child, [1] ticket,
/// [2..17] record+0x50 as 16 floats, row-major, row vectors, translation in row 3. Additive: a
/// host that does not find this export solves as before.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachmentGrips(uint32_t* out_words, uint32_t max_records) {
    if (out_words == nullptr || max_records == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    return ForEachStatedAttachment(base, max_records,
        [&](uint32_t, uint32_t record, uint32_t child, uint32_t written) {
            uint32_t* words = out_words + (written * REFII_ATTACHMENT_GRIP_WORDS);
            words[0] = child;
            words[1] = GuestWordAt(base, record + 144u);
            for (uint32_t i = 0u; i < 16u; ++i) {
                words[2u + i] = GuestWordAt(base, record + 0x50u + (i * 4u));
            }
        });
}

/// The locomotion state a character is IN, by name, plus the gates that state was chosen under.
///
/// WHY BY NAME AND NOT BY INDEX. The guest's machine keeps its current state as an object with
/// an LHString name, and the name is the only stable identity it has: the 240-byte definition
/// records are heap-allocated per run and the registry is a std::map keyed by that same string.
/// Godot already reads the 41 state names out of the shipped script, so a name joins the two
/// sides exactly and a wrong read cannot masquerade as a different valid state — it comes back
/// as a string that matches nothing.
///
/// THE MACHINE IS AN ENTITY MODE. There is no locomotion component. Modes hang off component
/// 21 in a circular list and the locomotion machine is simply the one whose `GetModeType()`
/// returns 2, 3 or 4 (walk / strafe / aiming). Those three cannot be told apart by layout, so
/// they are identified by VTABLE — the only RTTI a stripped binary offers — and each is
/// verified to be a movement-interface mode: all three share `GetModeInterface()`
/// (`0x82EA63A8`, returns 1) at vtable slot +8, while slot +4 returns 2, 3 and 4 respectively.
///
/// WHAT IS NOT HERE. The velocity and control magnitudes the state machine actually tests are
/// built into a ~316-byte STACK temporary each tick by `sub_8228D058` and are gone by the time
/// any host can look, so there is nothing resident to read. An earlier report of a persistent
/// velocity ring on the machine did not survive reading that function and is not implemented
/// rather than being implemented on faith. `is child` is likewise absent: it needs a guest CALL
/// (`sub_821C2A18`), and this bridge only ever reads memory.
///
/// Every offset below was decompiled for this change, not taken on report:
///   entity+0x24 bit 21, entity+0x8C accelerator, entity+0x48..0x4C  (sub_821E5B28)
///   mgr+0x18 head, node{next+0, mode+8}, vtable+4 = GetModeType     (sub_821FF5C8)
///   machine+0x04 entity, machine+0x14 current state                 (sub_822CCE10)
///   state+0x24 name, state+0x2C dwell seconds                       (sub_822CCE10)
///   name+0x00 chars, name+0x04 length                               (LHString CBasicString ==)
///   comp99+16 stance, comp99+40 combat, comp132 confinement list    (sub_8228D058)
///
/// Writes REFII_LOCOMOTION_WORDS words: machine, state, modeType, dwell (float bits), stance,
/// combatState, confinementMask. Returns the words written, or 0 when the entity carries no
/// locomotion mode — which most entities do not, so 0 is the ordinary answer, not a failure.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLocomotion(uint32_t entity, uint32_t* out_words, uint32_t word_count,
                                      char* out_name, uint32_t name_capacity) {
    if (out_words == nullptr || word_count < REFII_LOCOMOTION_WORDS) {
        return 0u;
    }

    for (uint32_t i = 0u; i < REFII_LOCOMOTION_WORDS; ++i) {
        out_words[i] = 0u;
    }
    if (out_name != nullptr && name_capacity != 0u) {
        out_name[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };

    if (!plausible(entity)) {
        return 0u;
    }

    // entity -> component, by the id->slot accelerator with the sorted vector as the fallback.
    const auto component = [&](uint32_t id) -> uint32_t {
        return EntityComponent(base, entity, id);
    };

    const uint32_t manager = component(21u);
    if (!plausible(manager)) {
        return 0u;
    }

    // The mode list is circular and `mgr+0x18` holds the SENTINEL, so the walk starts at its
    // next and stops when it comes back round. The step cap is against a torn read, not a
    // real bound: no entity carries anywhere near this many modes.
    const uint32_t sentinel = read32(manager + 0x18u);
    if (!plausible(sentinel)) {
        return 0u;
    }

    uint32_t machine = 0u;
    uint32_t mode_type = 0u;

    uint32_t node = read32(sentinel);
    for (uint32_t step = 0u; step < 64u && plausible(node) && node != sentinel; ++step) {
        const uint32_t mode = read32(node + 8u);
        if (plausible(mode)) {
            switch (refii::guest::ToTu1(read32(mode))) {
                case 0x82006070u: machine = mode; mode_type = 2u; break;
                case 0x820DA468u: machine = mode; mode_type = 3u; break;
                case 0x820DA4C8u: machine = mode; mode_type = 4u; break;
                default: break;
            }
        }

        if (machine != 0u) {
            break;
        }

        node = read32(node);
    }

    if (machine == 0u) {
        return 0u;
    }

    const uint32_t state = read32(machine + 0x14u);

    out_words[0] = machine;
    out_words[1] = state;
    out_words[2] = mode_type;

    if (plausible(state)) {
        out_words[3] = read32(state + 0x2Cu);  // dwell seconds, float bits

        const uint32_t name = read32(state + 0x24u);
        if (plausible(name) && out_name != nullptr && name_capacity != 0u) {
            const uint32_t chars = read32(name);
            const uint32_t length = read32(name + 4u);
            if (plausible(chars) && length < 256u) {
                uint32_t copy = length;
                if (copy > name_capacity - 1u) {
                    copy = name_capacity - 1u;
                }

                for (uint32_t i = 0u; i < copy; ++i) {
                    out_name[i] = (char)read8(chars + i);
                }

                out_name[copy] = '\0';
            }
        }
    }

    // Stance and combat default to 1 — the wildcard on both — when the entity has no stance
    // component, which is what sub_8228D058 leaves in the context before its own branch.
    uint32_t stance = 1u;
    uint32_t combat = 1u;

    const uint32_t stance_component = component(99u);
    if (plausible(stance_component)) {
        stance = read32(stance_component + 16u);
        combat = (read32(stance_component + 40u) != 0u) ? 2u : 1u;
    }

    // The guest forces a neutral stance to REGULAR before testing, so a state declaring
    // Stance=1 matches everything and no runtime stance is ever 1.
    if (stance == 1u) {
        stance = 2u;
    }

    out_words[4] = stance;
    out_words[5] = combat;

    // Confinement is the OR of every ACTIVE contact's mask, not a single field.
    uint32_t confinement = 0u;
    const uint32_t confinement_component = component(132u);
    if (plausible(confinement_component)) {
        const uint32_t begin = read32(confinement_component + 16u);
        const uint32_t end = read32(confinement_component + 20u);
        if (plausible(begin) && end > begin && (end - begin) <= 0x1000u) {
            for (uint32_t at = begin; at + 8u <= end; at += 8u) {
                const uint32_t contact = read32(at);
                if (plausible(contact) && read8(contact + 112u) != 0u) {
                    confinement |= read32(contact + 128u);
                }
            }
        }
    }

    out_words[6] = confinement;
    return REFII_LOCOMOTION_WORDS;
}

/// The model hashes a COMPOSITE character is assembled from.
///
/// WHY THIS EXISTS. 29 of BWSSlums's 138 characters have a live 160-bone skeleton and a model
/// hash of exactly zero at comp6+0x20, so nothing draws them — they are people missing from
/// the street. `sub_82229BE0` proves zero is terminal on component 6:
///
///   82229ee4  lwz    r11, 0x14(r28)     ; r28 = comp6+12, so this is comp6+0x20
///   82229ef0  ble    cr6, loc_8222A3B8  ; hash == 0 -> branch past the ENTIRE mesh block
///
/// Their geometry is on `CECGraphicAppearanceMorph`, component id 17 — confirmed, not assumed:
/// `sub_8263C078` sets `v13[1] = 17` beside the class-name string in the 264-class registry,
/// and all 29 were measured to carry id 17.
///
/// TWO LISTS, AND WE READ THE ONE THAT IS ACTUALLY FILLED. Verified in `sub_8217FA50`:
///
///   comp17+0x28..0x2C  stride 4   the SOURCE hashes, written by the property loader
///   comp17+0x38..0x3C  stride 24  the BUILT part records, +0 = the same hash
///
/// `sub_8217FA50` maps the first to the second — `(a1[11] - a1[10]) >> 2` walks the source and
/// `sub_824C6388` pushes 24-byte records — and it is reached from `sub_8220DE88`, the per-frame
/// DRAW virtual. Our guest never draws, so the built list may never be populated at all, while
/// the source list is filled at construction from HeadModel / TorsoModel / LegsModel /
/// *Accessories / AdditionalModelFiles on the CharacterCreationRecord.
///
/// So the source list is what is returned, and the built count comes back separately rather
/// than being assumed either way. If `out_built` is non-zero the draw path does run here and
/// the 24-byte records are available too; if it is zero, this export is the only way in.
///
/// All offsets are measured from the COMPONENT POINTER. Writes up to `max_hashes` model
/// resource-name hashes and returns how many were written.
/// The pause-menu gate, as the guest's own calls state it (hooks/gui_stated_screens.cpp): the
/// screen its setter says is up (**0 is the pause root**, 31 none), the modal latch and id its
/// openers set (latch 1 idle, 2 MessageBox, 3 QueryBox, 4 the world map's stop-travelling menu box,
/// 5 the numeric adjuster) and the tab its tab strip was last shown on. The openers put the screen
/// back, so the screen id is the UNDERLYING screen whatever box is up. Returns 0 while the in-game
/// screen set is not up.
REFII_HOST_ABI_EXPORT int RefiiHostGetPauseState(uint32_t* out_screen_id, uint32_t* out_modal_id,
                                                 uint32_t* out_modal_mode,
                                                 uint32_t* out_current_tab) {
    if (out_screen_id != nullptr) *out_screen_id = 0xFFFFFFFFu;
    if (out_modal_id != nullptr) *out_modal_id = 0u;
    if (out_modal_mode != nullptr) *out_modal_mode = 1u;
    if (out_current_tab != nullptr) *out_current_tab = 0u;

    uint32_t mode = 1u;
    uint32_t id = 0u;
    uint32_t tab = 0u;
    if (refii_bridge_stated_pause(&mode, &id, &tab) == 0u) return 0;

    const uint32_t screen = refii_bridge_stated_screen_now();
    if (out_screen_id != nullptr) *out_screen_id = screen == 0xFFFFFFFFu ? 31u : screen;
    if (out_modal_id != nullptr) *out_modal_id = id;
    if (out_modal_mode != nullptr) *out_modal_mode = mode;
    if (out_current_tab != nullptr) *out_current_tab = tab;
    return 1;
}

/// Which pause tabs the guest itself would offer, as its own predicate `sub_826B83D0` answered
/// when the pump called it on the GUI tick. There is no availability mask in the image, and the
/// tab push (`sub_826B89E0`) never re-checks the predicate.
///
/// Returns a bitmask with bit N = tab N (2..8); bits 0 and 1 are unused. Fails open to
/// all-available only when the pump has not sampled yet (guest not up, or the pause book not
/// yet opened).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPauseTabsAvailable(void) {
    constexpr uint32_t kAll = 0x1FCu;  // bits 2..8

    const uint32_t sampled = refii::vfs::PauseTabsMask();
    return sampled != 0u ? sampled : kAll;
}

/// The subtitles setting: 1 on, 0 off, -1 not sampled yet (front end not up, or too early).
REFII_HOST_ABI_EXPORT int RefiiHostGetSubtitles(void) {
    return refii::vfs::FrontEndSubtitles();
}

/// One captured menu row's IDENTITY — the half of a list that never touches an element.
///
/// Captured, not read: `g_Menu:CreateMenuItem` builds a pure Lua table the host cannot reach,
/// but `sub_826CE8D0` is handed the name, event type and 32-bit id in registers.
/// `sub_826CE110` (the `DeleteAllSpawnedMenuItems` wrapper) clears the capture — the one list
/// boundary the in-game and front-end populations share. The label arrives separately, through
/// the element tree, on the pool button's `Icon.Title`.
///
/// A row is committed through `RefiiHostPostFrontEndAction("MenuEvent:<name>:<type>:<id>")` —
/// the same `GUI:PushButtonEvent` the shipped `SelectMenuItem` would have made.
///
/// Returns the TOTAL row count regardless of `index`, so a caller sizes its loop from the first
/// call. `out_generation` is bumped on every spawn: equal generations mean the same list.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRow(uint32_t index, char* out_name,
                                                   uint32_t name_capacity, char* out_parent,
                                                   uint32_t parent_capacity, uint32_t* out_id,
                                                   int32_t* out_event_type,
                                                   uint64_t* out_generation,
                                                   int32_t* out_rating) {
    return refii::vfs::MenuRows(index, out_name, name_capacity, out_parent, parent_capacity,
                                out_id, out_event_type, out_generation, out_rating);
}


/// The last debug-call / debug-lua result the tick pump recorded — see guest_tick_pump.cpp's
/// drain. The sequence number says whether a posted call has finished yet.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDebugCallResult(uint64_t* out_seq, uint32_t* out_addr,
                                                           uint64_t* out_r3, char* out_note,
                                                           uint32_t note_capacity) {
    return refii::vfs::DebugCallResult(out_seq, out_addr, out_r3, out_note, note_capacity);
}


extern "C++" {
namespace remaster {

REXCVAR_DEFINE_BOOL(fx_weapon_augment_always_active, true, "Gameplay",
                    "Keep weapon augmentation particle effects active and playing continuously while moving (walking/running).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

PPCFunc* g_original_CAugmentation_MovementCheck = nullptr;

void Hook_CAugmentation_MovementCheck(PPCContext& ctx, uint8_t* base) {
    if (!REXCVAR_GET(fx_weapon_augment_always_active)) {
        if (g_original_CAugmentation_MovementCheck != nullptr) {
            g_original_CAugmentation_MovementCheck(ctx, base);
        }
        return;
    }

    const uint32_t comp = ctx.r3.u32;
    if (comp < 0x10000u || comp >= 0xC0000000u) {
        ctx.r3.u32 = 0;
        return;
    }

    const uint32_t weapon = GuestWordAt(base, comp + 4u);
    if (weapon < 0x10000u || weapon >= 0xC0000000u) {
        ctx.r3.u32 = 0;
        return;
    }

    // Check weapon entity flag: bit 13 at +52 (sub_825A0098: (*(_DWORD *)(weapon + 52) >> 13) & 1)
    const uint32_t flags = GuestWordAt(base, weapon + 52u);
    if (((flags >> 13) & 1u) != 0u) {
        ctx.r3.u32 = 0;
        return;
    }

    // Weapon is equipped and active: keep the effect alive continuously during movement
    ctx.r3.u32 = 1u;
}

void InstallWeaponAugmentationHook() {
    rex::Runtime* runtime = rex::Runtime::instance();
    if (runtime == nullptr || runtime->function_dispatcher() == nullptr) {
        return;
    }

    const uint32_t addr = refii::guest::Address(0x825A0098u);
    PPCFunc* original = runtime->function_dispatcher()->GetFunction(addr);
    if (original != nullptr && g_original_CAugmentation_MovementCheck == nullptr) {
        g_original_CAugmentation_MovementCheck = original;
        runtime->function_dispatcher()->SetFunction(addr, Hook_CAugmentation_MovementCheck);
    }
}
}  // namespace remaster
}  // extern "C++"

REFII_HOST_ABI_EXPORT uint32_t RefiiHostInstallWeaponAugmentHook(void) {
    remaster::InstallWeaponAugmentationHook();
    return remaster::g_original_CAugmentation_MovementCheck != nullptr ? 1u : 0u;
}

/// The SCREEN the captured rows belong to, or 0xFFFFFFFF when no in-game screen states one.
///
/// The capture is one list, so a template sourced `capture:Root` used to take whatever was
/// spawned last, whoever spawned it — the front end's `New Game` and `Continue` came up on the
/// real-estate screen above its own rows. This is the key that was missing: the screen the
/// guest's setter stated, taken by the payload at the boundary that clears the list. Compare it
/// with the screen a template is bound to and reject a population that belongs to another.
/// One captured row's ITEM HALF (ABI 270): its String3 icon (SpawnButton's r6), the mark the guest stated
/// for it (`SetItemEmphasisByIndex` inside the row builder `sub_826CE230`, `SetItemEmphasis` by name;
/// 0..3), and five words {flags, record, item, count, category} — flags kMenuRowItem (the row stands for an
/// inventory item: the one `sub_826CE230` was handed, hooks/item_rows_stated.cpp, or the one its ticket was
/// filed against), kMenuRowViewed (that item's `+0x14` byte), kMenuRowCounted (count, category and viewed
/// are the builder's). Same total count as RefiiHostGetMenuRow.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRowItem(uint32_t index, char* out_icon, uint32_t icon_capacity,
                                                       int32_t* out_emphasis, uint32_t* out_words,
                                                       uint32_t word_capacity) {
    return refii::vfs::MenuRowItem(index, out_icon, icon_capacity, out_emphasis, out_words, word_capacity);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuScreenId(void) {
    return refii::vfs::MenuScreenId();
}

/// One captured row's DISPLAY LABEL — String2, the localised text with its decorations,
/// decoded from the wide staging string at capture. Returns the same total count as
/// RefiiHostGetMenuRow, so the two calls describe one list.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRowLabel(uint32_t index, char* out_label,
                                                        uint32_t label_capacity) {
    return refii::vfs::MenuRowLabel(index, out_label, label_capacity);
}

/// The UIDs the guest considers DEAD — destroyed placements it will not respawn.
///
/// WHY THIS EXISTS. We draw the region's build-time copy of a placement and hide it while the
/// guest has a live entity for that record. Smash a barrel and the guest drops its entity, so
/// the copy comes back and stands where the smashed one was — a ghost. "No live entity" is not
/// "should be visible"; the missing term is the guest's own statement that the placement is
/// gone, and it keeps one.
///
/// `sub_82366088` inserts on every destroy. Verified in the disassembly, offsets measured from
/// the ENTITY and the ENTITY MANAGER respectively:
///
///   8236627c  ld   r11, 0x14(r31)   ; r31 = entity  -> the 64-bit UID, the map KEY
///   8236628c  lwz  r11, 0x84(r31)   ; LayerHash, the map VALUE
///   82366298  addi r4, r30, 0x94    ; r30 = EntityManager -> the map at +148
///
/// And the node layout from the save writer `sub_82367398`, which walks the same tree:
/// `_Myhead` at map+4 (mgr+152), iteration ends when the node equals it, key `__int64` at
/// node+12, LayerHash at node+20. MSVC `_Tree`: node+0 `_Left`, +4 `_Parent`, +8 `_Right`,
/// +24 `_Color`, +25 `_Isnil`.
///
/// It PERSISTS — written to the save as `DeadEntities`/`DeadEntity` and read back on load — and
/// deactivating a layer purges that layer's entries (`sub_82366A30`), so entries disappearing
/// is meaningful rather than stale: a layer cycling off and on legitimately restores its broken
/// props, exactly as the guest does for itself.
///
/// Writes up to `max_uids`; returns how many were written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeadEntities(uint64_t* out_uids, uint32_t max_uids) {
    if (out_uids == nullptr || max_uids == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t kEntityManagerGlobal = refii::guest::Address(0x83496B28u);
    const uint32_t manager = read32(kEntityManagerGlobal);
    if (!plausible(manager)) {
        return 0u;
    }

    const uint32_t head = read32(manager + 152u);   // map+4, the end sentinel
    if (!plausible(head)) {
        return 0u;
    }

    const uint32_t stated = read32(manager + 156u); // _Mysize

    // WALKED WITH AN EXPLICIT STACK AND A HARD CAP. This is a live guest tree read without a
    // lock, so a rotation in flight can hand back a cycle; a recursive or unbounded walk would
    // hang the caller rather than return something merely wrong. The cap is the size the
    // container states, bounded again so a torn `_Mysize` cannot widen it.
    constexpr uint32_t kMaxNodes = 8192u;
    uint32_t budget = stated < kMaxNodes ? stated + 1u : kMaxNodes;

    uint32_t stack[64];
    uint32_t depth = 0u;
    uint32_t written = 0u;

    const uint32_t root = read32(head + 4u);        // _Myhead->_Parent
    if (plausible(root) && root != head) {
        stack[depth++] = root;
    }

    while (depth != 0u && written < max_uids && budget-- != 0u) {
        const uint32_t node = stack[--depth];
        if (!plausible(node) || node == head) {
            continue;
        }

        // The sentinel is the only node flagged nil; anything else carries a live pair.
        if (GuestByteAt(base, node + 25u) == 0u) {
            const uint64_t hi = read32(node + 12u);
            const uint64_t lo = read32(node + 16u);
            out_uids[written++] = (hi << 32) | lo;
        }

        const uint32_t left = read32(node + 0u);
        const uint32_t right = read32(node + 8u);
        if (depth + 2u <= 64u) {
            if (plausible(left) && left != head) {
                stack[depth++] = left;
            }
            if (plausible(right) && right != head) {
                stack[depth++] = right;
            }
        }
    }

    return written;
}

/// The meshes a `CECGraphicAppearanceStaticMultipleMesh` (component 5) draws.
///
/// THE MISSING GEOMETRY ROUTE. `RefiiHostGetEntities` reports one model hash per entity, taken
/// from the single-slot appearance components (4 `+0x18`, 6 `+0x20`, 10/11 `+0x14`). Component 5
/// has no such slot — it holds a VECTOR of mesh records — so every entity using one reported
/// zero and read as "not drawable". That is what buildings use: the standing static copies in a
/// region are named `… interior` / `… exterior`, which are two records of one MeshRecords list.
///
/// Verified in `sub_828F9BA0` (component 5's InitialiseFromDefinition), offsets measured from
/// the COMPONENT pointer and from the RECORD pointer respectively:
///
///   comp5+0x10  vector object      comp5+0x14 begin, +0x18 end, +0x1C capacity
///   stride 64                      `(end - begin) >> 6` in both the init and the draw walk
///   record+0x00 render handle, initialised to -1
///   record+0x04 `Mesh`             <- what this returns
///   record+0x08 `ShadowMesh`
///   record+0x3C `Interior` flag    (`*(_BYTE *)(rec + 60)` in the draw walk `sub_828F9F90`)
///
/// Each record's hash comes from a GDB entry resolved per `MeshRecords` element, so it is a
/// lower-cased resource-name hash exactly like `ModelFile` — the same index resolves it.
///
/// Writes up to `max_hashes`; returns how many were written. `out_total` receives the record
/// count the component states, which may exceed what was written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMeshParts(uint32_t entity, uint32_t* out_hashes,
                                      uint32_t max_hashes, uint32_t* out_total) {
    if (out_total != nullptr) {
        *out_total = 0u;
    }

    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    constexpr uint8_t kStaticMultipleMeshId = 5u;

    if (((read32(entity + 0x24u + (kStaticMultipleMeshId / 32u) * 4u) >>
          (kStaticMultipleMeshId % 32u)) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t component = EntityComponent(base, entity, kStaticMultipleMeshId);

    if (!plausible(component)) {
        return 0u;
    }

    // CHECKED AS A TRIPLE, for the reason the morph list is: a vector that grows publishes its
    // pointers one at a time, so `begin <= end <= capacity` is the only statement that a pair
    // cannot satisfy by accident mid-write.
    const uint32_t begin = read32(component + 0x14u);
    const uint32_t end = read32(component + 0x18u);
    const uint32_t capacity = read32(component + 0x1Cu);

    if (!plausible(begin) || end <= begin || capacity < end ||
        ((end - begin) % 64u) != 0u || ((capacity - begin) % 64u) != 0u ||
        (end - begin) > 0x10000u) {
        return 0u;
    }

    if (out_total != nullptr) {
        *out_total = (end - begin) / 64u;
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at + 64u <= end && written < max_hashes; at += 64u) {
        const uint32_t hash = read32(at + 4u);
        if (hash != 0u) {
            out_hashes[written++] = hash;
        }
    }

    return written;
}

/// The INTERIOR records of a multi-mesh appearance (component 5): the `Mesh` hashes of the
/// records whose flag word +0x38 carries 0x2000 (`sub_828F9BA0` 0x828F9E10: set when the record's
/// GDB `Interior` is non-zero). The consumer `sub_828F9F90` ORs it into the descriptor (0x828FA55C)
/// and the twin init shifts it to twin+8 bit 14, which is what the weather's roof walk keeps
/// (`sub_82A78528` 0x82A78694): these meshes are the rain's and snow's roofs.
/// Same vector and checks as RefiiHostGetEntityMeshParts; returns the hashes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityInteriorMeshes(uint32_t entity, uint32_t* out_hashes,
                                                              uint32_t max_hashes) {
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }
    constexpr uint8_t kStaticMultipleMeshId = 5u;
    if (((GuestWordAt(base, entity + 0x24u + (kStaticMultipleMeshId / 32u) * 4u) >>
          (kStaticMultipleMeshId % 32u)) & 1u) == 0u) {
        return 0u;
    }
    const uint32_t component = EntityComponent(base, entity, kStaticMultipleMeshId);
    if (component < 0x10000u || component >= 0xC0000000u) {
        return 0u;
    }
    const uint32_t begin = GuestWordAt(base, component + 0x14u);
    const uint32_t end = GuestWordAt(base, component + 0x18u);
    const uint32_t capacity = GuestWordAt(base, component + 0x1Cu);
    if (begin < 0x10000u || end <= begin || capacity < end || ((end - begin) % 64u) != 0u ||
        ((capacity - begin) % 64u) != 0u || (end - begin) > 0x10000u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at + 64u <= end && written < max_hashes; at += 64u) {
        const uint32_t hash = GuestWordAt(base, at + 4u);
        if (hash != 0u && (GuestWordAt(base, at + 0x38u) & 0x2000u) != 0u) {
            out_hashes[written++] = hash;
        }
    }
    return written;
}

/// The DISPLACEMENT parts of a composite character: the model hashes of component 17's built
/// records (+0x38 begin, +0x3C end, +0x40 capacity, stride 24) whose +0x15 byte is set — the
/// definition's GDB `DisplacementMeshes` (`sub_8217FA50`). The model create for such a record
/// carries flag 0x800 (`sub_8220DE88` 0x8220E09C), which moves its twin to the displacement
/// renderer's layer alone (`sub_82AA3C78`): drawn only as distortion. Returns the hashes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityDisplacementParts(uint32_t entity, uint32_t* out_hashes,
                                                                 uint32_t max_hashes) {
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }
    const uint32_t morph = EntityComponent(base, entity, 17u);
    if (morph < 0x10000u || morph >= 0xC0000000u) {
        return 0u;
    }
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    if (!refii::hostcore::IsAppearanceSubcomponentActive(
            refii::hostcore::GuestReader{base}, renderable,
            refii::hostcore::kFable2AppearanceSublistOffset, morph + 0x0Cu)) {
        return 0u;
    }
    const uint32_t begin = GuestWordAt(base, morph + 0x38u);
    const uint32_t end = GuestWordAt(base, morph + 0x3Cu);
    const uint32_t capacity = GuestWordAt(base, morph + 0x40u);
    if (begin < 0x10000u || end <= begin || capacity < end || ((end - begin) % 24u) != 0u ||
        ((capacity - begin) % 24u) != 0u || (end - begin) > 0x18000u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at + 24u <= end && written < max_hashes; at += 24u) {
        const uint32_t hash = GuestWordAt(base, at);
        if (hash != 0u && GuestByteAt(base, at + 0x15u) != 0u) {
            out_hashes[written++] = hash;
        }
    }
    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMorphParts(uint32_t entity, uint32_t* out_hashes,
                                      uint32_t max_hashes, uint32_t* out_built) {
    if (out_built != nullptr) {
        *out_built = 0u;
    }

    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    // Components, through the entity's own lookup contract: presence bitmask of 8 dwords at
    // entity+0x24, then either the byte index table at entity+0x8C or a walk of the {id, ptr}
    // pairs. Same shape RefiiHostGetEntities uses for the graphic-appearance slots.
    const auto component = [&](uint8_t id) -> uint32_t {
        return EntityComponent(base, entity, id);
    };

    // THE EYES ARE NOT MORPH PARTS. They are component 7 `CECGraphicAppearanceEyes`' own two
    // render instances: the right eye model hash at comp7+0x18 (`sub_8259D380`, `result[6]`)
    // and the left at comp7+0x28 (`sub_8259D3F8`, `result[10]`, which already holds the RIGHT
    // model when `BodgeEyes` at comp7+81 is set). The eye-case fields the GDB also names are
    // read and discarded by the guest (`sub_825B5160` 0x825B56E0/0x825B56F4) and are not
    // here. Both are appended after the parts so the host can hang them on
    // `Shadow_R_Eye`/`Shadow_L_Eye` the way the guest does (`sub_82209480`). Nothing is
    // appended when the component is absent or its `Enabled` byte at +0x60 is clear.
    //
    // READ BEFORE THE MORPH COMPONENT, NOT INSIDE IT. `CreatureHeroChild` states its whole
    // body as ONE ModelFile on component 6 and its eyes on component 7; its morph component
    // has an empty source list. The child therefore never had a morph read at all, and its
    // eyes — hung by the guest on the same rig, `sub_8259CF80` stores them at comp7+0x18/+0x28
    // for any entity with the component — were never crossed. An entity with eyes and no
    // morph parts answers with the two eyes alone.
    constexpr uint8_t kEyesComponentId = 7u;
    const uint32_t eyes = component(kEyesComponentId);
    const uint32_t renderable = component(3u);

    const auto append_eyes = [&](uint32_t written) -> uint32_t {
        if (!plausible(eyes) || read8(eyes + 0x60u) == 0u ||
            !refii::hostcore::IsAppearanceSubcomponentActive(
                refii::hostcore::GuestReader{base}, renderable,
                refii::hostcore::kFable2AppearanceSublistOffset, eyes + 0x0Cu)) {
            return written;
        }
        const uint32_t right = read32(eyes + 0x18u);
        const uint32_t left = read32(eyes + 0x28u);
        if (right != 0u && written < max_hashes) {
            out_hashes[written++] = right;
        }
        if (left != 0u && written < max_hashes) {
            out_hashes[written++] = left;
        }
        return written;
    };

    constexpr uint8_t kMorphComponentId = 17u;
    const uint32_t morph = component(kMorphComponentId);
    if (!plausible(morph) ||
        !refii::hostcore::IsAppearanceSubcomponentActive(
            refii::hostcore::GuestReader{base}, renderable,
            refii::hostcore::kFable2AppearanceSublistOffset, morph + 0x0Cu)) {
        return append_eyes(0u);
    }

    // THE BUILT LIST IS THE DRESSED CHARACTER, AND IT IS THE ONE TO READ.
    //
    // The source list at +0x28 holds ONLY what the CharacterCreationRecord named — head, torso,
    // legs, eyes. Worn clothing is never in it and never can be: `sub_8217EDE0` appends garments
    // straight into the BUILT vector, reading them from a different component entirely —
    // id 15, `CECAppearanceModifierManager`, whose applied-modifier list at +0x10..+0x14 is the
    // outfit. Equipping does not touch the source list; `sub_824BECF8` only marks
    // CECGraphicAppearance dirty and the next rebuild picks it up.
    //
    // This used to return the source list on the reasoning that our guest never draws, so the
    // built list might never be filled. THAT IS MEASURED FALSE: we see 147-245 built records
    // against 84 source parts. The difference is the clothes, and the hero was rendering naked.
    // The rebuild is BUILD-ON-DEMAND, not per-frame: `sub_8220DE88` calls `sub_8217FA50` only
    // when the render-instance vector at comp+0x48..+0x4C is empty (what the clear
    // `sub_825B8C70` leaves behind, 0x825b8cd0) or when its caller sets bit 0x40 of ctx+0x30
    // — every other trigger is a discrete appearance change, so gear changes are when this
    // list moves, not movement or streaming.
    //
    // ORDER IS FREE. The rebuild ends in `sub_825BC9C8(...)`, a sort by record+0x10
    // (ClusterSortLayer) — body parts author -100 so they sort first — so reading the vector in
    // array order already gives the guest's own draw order.
    //
    // Record layout, 24 bytes: +0x00 model hash, +0x10 i32 sort layer, +0x14 u8 isBodyPart
    // (0 = GARMENT), +0x15 u8 has displacement mesh, +0x16 u8 authored-source flag.
    // +0x04..+0x0F IS NOT INLINE DATA — it is an MSVC `_Tree` (std::set<string>) of the body
    // clusters the garment hides: +0x08 is `_Myhead`, a pointer to a heap sentinel node.
    // DO NOT DEREFERENCE IT from here; reading those names means walking a red-black tree of
    // guest heap nodes that the rebuild frees.
    const uint32_t built_begin = read32(morph + 0x38u);
    const uint32_t built_end = read32(morph + 0x3Cu);
    const uint32_t built_capacity = read32(morph + 0x40u);

    // THE TRIPLE IS CHECKED AS A TRIPLE. `sub_824C6CC0` grows the vector by freeing the old
    // block, publishing `begin` (0x824c6e2c), then `capacity` (0x824c6e4c), then `end`
    // (0x824c6e54) — eleven instructions in which `begin` addresses the new block while `end`
    // still addresses the freed one, so `end - begin` is the difference of two unrelated heap
    // pointers and can be any size or sign. `begin <= end <= capacity` is the container's own
    // invariant at rest, so a triple that breaks it is torn by definition.
    const bool built_usable = plausible(built_begin) && built_end > built_begin &&
                              built_capacity >= built_end &&
                              ((built_end - built_begin) % 24u) == 0u &&
                              ((built_capacity - built_begin) % 24u) == 0u &&
                              (built_end - built_begin) <= 0x18000u;

    if (built_usable) {
        const uint32_t count = (built_end - built_begin) / 24u;
        if (out_built != nullptr) {
            *out_built = count;
        }

        uint32_t written = 0u;
        for (uint32_t at = built_begin; at + 24u <= built_end && written < max_hashes;
             at += 24u) {
            const uint32_t hash = read32(at);
            if (hash != 0u) {
                out_hashes[written++] = hash;
            }
        }

        if (written != 0u) {
            return append_eyes(written);
        }
    }

    // FALLBACK: the source list, stride 4. Reached when the draw path has not run for this
    // entity yet, which is a real state on the frame a character is spawned — the naked body is
    // a better answer than nothing, and the next frame carries the clothes.
    const uint32_t begin = read32(morph + 0x28u);
    const uint32_t end = read32(morph + 0x2Cu);
    if (!plausible(begin) || end <= begin || ((end - begin) % 4u) != 0u ||
        (end - begin) > 0x4000u) {
        return append_eyes(0u);
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at + 4u <= end && written < max_hashes; at += 4u) {
        const uint32_t hash = read32(at);
        if (hash != 0u) {
            out_hashes[written++] = hash;
        }
    }

    return append_eyes(written);
}

/// One live character's SKELETON POSE, so Godot can drive a Skeleton3D from the guest.
///
/// The placement feed puts a prop where the guest says it is; this is the rest of what a
/// CHARACTER needs. Like the placement read it calls no guest code — the pose the animation
/// system evaluated is left in a stable, component-owned heap array, and we copy it out.
///
/// WHERE IT IS. The animated-mesh component is id 6 (`CECGraphicAppearanceAnimatedMesh`),
/// and it owns a vector of 48-byte bone transforms at comp+0x2C: begin +0x30, end +0x34, so
/// the bone count is `(end - begin) / 48`.
///
/// WHY THAT ARRAY AND NOT THE POSE OBJECT AT comp+0x24. Both exist and only one is the live
/// finished pose. `sub_821F2C78` XMemCpys the freshly evaluated pose INTO comp+0x30 —
/// `XMemCpy(*(a1+48), *(a2+4), 48 * ((*(a1+52) - *(a1+48)) / 48))` — and its caller
/// `sub_8257D8B8` then runs `sub_821F57D0(*(a1+48), *(a1+148), a1+112, 0)` to turn it from
/// local into MODEL space in place, stamps `*(a1+20) = 2` and sets the dirty byte at
/// `*(a1+228)`. So this array is per-frame, and by the time the frame is over it is already
/// object-space: no parent walk on our side.
///
/// `comp+0x14` STATES the space — 1 local, 2 model — and it is returned rather than assumed.
/// A pose read mid-update is local, and silently treating those bones as model-space is the
/// kind of plausible-and-wrong result that reads as a rigging bug.
///
/// ELEMENT LAYOUT, 48 bytes: quaternion at +0 (w in lane 3), translation at +0x10, scale at
/// +0x20. NOTE this is NOT Havok's order — `hkQsTransform` is translation first, and the two
/// are the same size, so one reader cannot serve both.
///
/// The PARENT table comes from the skeleton at comp+0x94: entries of 8 bytes between
/// skel+0x2C and skel+0x30, parent index (-1 for a root) in the first dword. Bones are
/// ordered parent-before-child.
///
/// Fills 12 floats per bone and one parent index per bone. Returns the bone count, or 0 when
/// the entity carries no animated mesh — which is the common case and not an error.
static uint32_t RefiiHostReadEntityRig(uint32_t entity, float* out_transforms,
                                       int32_t* out_parents, uint32_t max_bones,
                                       uint32_t* out_space, uint32_t* out_torn, bool rest);

/// `out_torn` is set when the read could not be made coherent — see the snapshot comment in
/// `RefiiHostReadEntityRig`. The bones are still filled in on a torn read, because the
/// caller is better placed to decide between last frame's pose and this one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPose(uint32_t entity, float* out_transforms, int32_t* out_parents,
                                uint32_t max_bones, uint32_t* out_space, uint32_t* out_torn) {
    return RefiiHostReadEntityRig(entity, out_transforms, out_parents, max_bones, out_space,
                                  out_torn, false);
}

/// The REST pose of a character's rig — the bind pose Godot needs to build its skin.
///
/// Separate from the live pose because it is read ONCE per model and never changes, while
/// the live pose is read every frame. Same bone ordering and same parent table, so the two
/// index the same rig.
///
/// It is stored LOCAL and has to be composed through the parent chain to be useful, which is
/// the opposite of the live pose. Verified rather than assumed: `sub_82B73170` computes the
/// count as `(*(skel+148) - *(skel+144)) / 48`, copies those elements to a scratch buffer,
/// and runs the local-to-model converter `sub_82B734B0` ON THE COPY — you do not convert
/// something that is already converted.
///
/// Bones come back parent-before-child, so one forward pass composes them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityRestPose(uint32_t entity, float* out_transforms,
                                    int32_t* out_parents, uint32_t max_bones) {
    return RefiiHostReadEntityRig(entity, out_transforms, out_parents, max_bones, nullptr,
                                  nullptr, true);
}

/// The shared body of the two rig reads above.
///
/// One implementation, because the only difference between them is WHICH 48-byte array on
/// the same component they take — the entity walk, the component lookup, the bounds checks
/// and the parent table are identical, and two copies of that would be two things to keep
/// in agreement.
static uint32_t RefiiHostReadEntityRig(uint32_t entity, float* out_transforms,
                                       int32_t* out_parents, uint32_t max_bones,
                                       uint32_t* out_space, uint32_t* out_torn, bool rest) {
    constexpr uint8_t kAnimatedMeshId = 6u;

    if (out_space != nullptr) {
        *out_space = 0u;
    }

    // Clean by default: every path that returns without reaching the snapshot returns no
    // bones at all, and "torn" must not read as true for a rig that was never read.
    if (out_torn != nullptr) {
        *out_torn = 0u;
    }

    if (out_transforms == nullptr || out_parents == nullptr || max_bones == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    if (!plausible(entity)) {
        return 0u;
    }

    // Same component lookup the model-hash read uses: the id bitmask gates it, the
    // accelerator resolves it in one step, and the linear scan is the fallback for an
    // entity that has not built one.
    if (((read32(entity + 0x24u) >> kAnimatedMeshId) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t component = EntityComponent(base, entity, kAnimatedMeshId);

    if (!plausible(component)) {
        return 0u;
    }

    // The parent table is the skeleton's, and it has to agree with whichever array we read
    // or the two are describing different rigs.
    const uint32_t skeleton = read32(component + 0x94u);
    if (!plausible(skeleton)) {
        return 0u;
    }

    // The live pose hangs off the COMPONENT; the rest pose off the SKELETON. Same element
    // size, same bone order, different owner — and quoting one against the other's base is
    // exactly the mistake that has cost this project a shipped-wrong offset before.
    // The live pose is the published block, not the component's working array.
    //
    // `sub_821FF010` is the guest's publish: it allocates a fresh block (`sub_821F5C18`),
    // XMemCpy's the finished pose into it, applies the attachment override, and swaps it into
    // the shared_ptr at `comp+0x24`. The attachment override at `comp+0x3C`/`+0x40` is written
    // into the published copy only (`821ff0e0`..`821ff11c`), and the published block is never
    // rewritten in place, unlike `comp+0x30`.
    //
    // Layout: holder = `*(comp+0x24)` (a shared_ptr's px), block = `*holder`, bone buffer =
    // `*(block+0x10)`, and `block+8` is the skeleton identity tag every render consumer checks
    // against `skel+0x14`. The count is the publisher's own — `(skel+0x30 - skel+0x2C) >> 3` —
    // not `block+0xC`, which nothing in the image reads.
    //
    // Falls back to the working array: a component publishes only through `sub_821F56F8`, and
    // `sub_8257D8B8` poses without ever calling it, so a block is not guaranteed to exist.
    uint32_t pose = 0u;
    uint32_t pose_end = 0u;
    uint32_t holder = 0u;
    uint32_t published = 0u;
    bool from_block = false;

    // A ragdoll's pose exists only at the end of the tick, and only in the working array.
    //
    // The publish runs twice per tick for an active ragdoll. `sub_8220D1B8` publishes the clip
    // pose unconditionally (`8220dbe8 .. bl sub_821F56F8` @0x8220dbf0); Havok steps; then
    // `sub_8220A700` copies the bodies over the array (`sub_8257DBC0` @0x8220abd8), stamps
    // `comp+0x10 := 2` (`8257dc94 stw r11,0x10(r31)`) and publishes again (@0x8220ad48). The
    // guest draws after the second; an asynchronous reader can see the first.
    //
    // So a physics-owned rig reads `comp+0x30` and only while `comp+0x10 == 2`. `sub_821F2C78`
    // stamps 1 when it copies the next clip pose in, so 2 brackets the physics result; the one
    // window it lags (the XMemCpy before that stamp) leaves a local-space array, which the
    // double read or the host's space vote refuses.
    const bool physics = !rest && EntityPhysicsOwnsPose(base, entity);

    if (!rest && !physics) {
        holder = read32(component + 0x24u);
        if (plausible(holder)) {
            published = read32(holder);
            if (plausible(published) && read32(published + 8u) == read32(skeleton + 0x14u)) {
                const uint32_t buffer = read32(published + 0x10u);
                const uint32_t table_begin = read32(skeleton + 0x2Cu);
                const uint32_t table_finish = read32(skeleton + 0x30u);
                if (plausible(buffer) && table_finish > table_begin) {
                    pose = buffer;
                    pose_end = buffer + (((table_finish - table_begin) >> 3) * 48u);
                    from_block = true;
                }
            }
        }
    }

    if (!from_block) {
        pose = rest ? read32(skeleton + 0x90u) : read32(component + 0x30u);
        pose_end = rest ? read32(skeleton + 0x94u) : read32(component + 0x34u);
    }

    if (!plausible(pose) || pose_end <= pose) {
        return 0u;  // sized but never filled — the character has not been posed yet
    }

    uint32_t bones = (pose_end - pose) / 48u;
    if (bones > max_bones) {
        bones = max_bones;
    }

    const uint32_t bone_table = read32(skeleton + 0x2Cu);
    const uint32_t bone_table_end = read32(skeleton + 0x30u);
    if (!plausible(bone_table) || bone_table_end <= bone_table ||
        (bone_table_end - bone_table) / 8u < bones) {
        return 0u;
    }

    // A COHERENT SNAPSHOT, because the guest rewrites this array underneath us.
    //
    // The guest publishes each finished pose into a fresh block and swaps it into the
    // shared_ptr at `comp+0x24` (`sub_821FF010` -> `sub_821F5C18`); its own renderer keeps the
    // last two and interpolates between them (`sub_8222E2B8`, blocks at
    // `renderObj+0x200`/`+0x204`). `comp+0x30` itself is rewritten in place — `sub_821F2C78`
    // XMemCpy's the pose in, modifiers mutate it, and `sub_821F57D0` converts it to model space
    // bone by bone over the same memory — so the fallback path below reads it twice.
    //
    // AND A TORN RIG IS NOT A SMALL ERROR. During `sub_821F57D0` the array holds two
    // different SPACES at once — bones behind its cursor are model-space, bones ahead of it
    // are still parent-relative. Read then, part of the skeleton is interpreted in the wrong
    // space entirely, which is a character exploding for one frame rather than a bone being
    // slightly stale.
    //
    // NEITHER MARKER THE COMPONENT CARRIES CAN GATE THIS, and both fail the same way. The
    // flag at `comp+0x14` is stamped to 1 at the TAIL of `sub_821F2C78` (0x821F2F7C), so it
    // still reads 2 from the previous frame for the whole duration of the memcpy — a flag
    // check would accept exactly the tear it exists to catch. `dword_83319650` trails the
    // entire world update, so it does not move during one either. A marker that lags its own
    // payload is worse than no marker, because it reports confidence it has not earned.
    //
    // So: read the payload twice and require the two to agree, which assumes nothing about
    // either marker. An idle character memcpy'd back to identical values compares equal, and
    // that is correct — identical bytes are not torn in any sense that matters.
    constexpr uint32_t kMaxSnapshotBones = 256u;
    if (bones > kMaxSnapshotBones) {
        bones = kMaxSnapshotBones;
    }

    static thread_local uint8_t first[kMaxSnapshotBones * 48u];
    static thread_local uint8_t second[kMaxSnapshotBones * 48u];
    const size_t span = (size_t)bones * 48u;

    const uint32_t latch_before = physics ? read32(component + 0x10u) : 2u;
    bool coherent = false;
    if (from_block) {
        // The holder's pointer is the generation counter. The publisher never writes a block in
        // place, so the copy can only be spoiled by a swap; an unchanged holder after the copy
        // proves the block was current for the whole read.
        for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
            CopyGuest(base, pose, first, span);
            coherent = read32(holder) == published;
            if (!coherent) {
                published = read32(holder);
                if (!plausible(published) ||
                    read32(published + 8u) != read32(skeleton + 0x14u)) {
                    return 0u;  // the rig changed skeletons mid-read; nothing to draw
                }

                pose = read32(published + 0x10u);
                if (!plausible(pose)) {
                    return 0u;
                }
            }
        }
    } else {
        // The working array at `comp+0x30` is rewritten in place, and during `sub_821F57D0` it
        // holds two different spaces at once. No generation to lean on: read it twice and
        // require agreement. Reached for the rest pose, physics rigs and components that never publish.
        for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
            CopyGuest(base, pose, first, span);
            CopyGuest(base, pose, second, span);
            coherent = std::memcmp(first, second, span) == 0;
        }
    }

    if (physics && (latch_before != 2u || read32(component + 0x10u) != 2u)) {
        coherent = false;  // mid-tick: the array holds this tick's clip pose, not the bodies
    }

    // REPORTED, NOT HIDDEN. A caller that knows the read was torn can hold last frame's pose
    // for that entity, which is always better than drawing a rig assembled from two of them.
    // Swallowing it here would leave the host unable to tell a stale pose from a broken one.
    if (out_torn != nullptr) {
        *out_torn = coherent ? 0u : 1u;
    }

    // 12 big-endian floats per bone; the parent index is the first word of each 8-byte
    // bone-table entry. The table is read in one guarded copy, not one per bone.
    rex::memory::copy_and_swap_32_unaligned(out_transforms, first, (size_t)bones * 12u);

    static thread_local uint8_t table[kMaxSnapshotBones * 8u];
    CopyGuest(base, bone_table, table, (size_t)bones * 8u);
    for (uint32_t i = 0u; i < bones; ++i) {
        uint32_t parent = 0u;
        std::memcpy(&parent, table + (i * 8u), sizeof(parent));
        out_parents[i] = (int32_t)__builtin_bswap32(parent);
    }

    if (out_space != nullptr) {
        *out_space = read32(component + 0x14u);
    }

    return bones;
}

/// The guest's own NAME for each of a character's bones.
///
/// THIS IS WHAT MAKES A CHARACTER DRAWABLE. The `.mdl` numbers its bones one way and the
/// guest's runtime skeleton numbers them another — 143 against 161 for the child hero, and
/// 39 against 39 with only 11 of the parent links agreeing for a chicken. A mesh's
/// BLENDINDICES address the model's table, so binding them to a skeleton built in the
/// guest's order attaches every vertex to the wrong bone and the character renders as a fan
/// of spikes. The name is the only key both sides share.
///
/// WHERE THE CHARACTERS ARE. `sub_821B5508` is the guest's own find-bone-by-name and is the
/// ground truth for all of this:
///
///     v3 = *(skel + 44);                    // skel+0x2C, bone table begin
///     v4 = (*(skel + 48) - v3) >> 3;        // 8 bytes per entry
///     for ( i = (int *)(v3 + 4); ; i += 2 ) // &bone[0].name, stride 8
///         if ( *i == v5 ) ...               // *a2, compared WITHOUT a dereference
///
/// so the dword at `bone + 4` IS the string node, not a handle to be resolved. The node's
/// own layout comes from the comparator it falls back on when the pointers differ,
/// `LHString_CBasicString_char_operator` @ 0x8226D790, which is called with those same two
/// node pointers:
///
///     return *(_DWORD *)(a1 + 4) == *(_DWORD *)(a2 + 4)
///         && sub_8226D7F8(*(_BYTE **)a1, *(_BYTE **)a2) == 0;
///
/// — `node + 4` is the byte length and `*(char**)node` is NUL-terminated ASCII. Plain
/// characters, in memory, in the shipped build: a strcmp cannot work otherwise.
///
/// A null node is legitimate and means an unnamed bone, not a failure — the guest degrades
/// it to the shared empty string at 0x8208FFDF, which is a single 0x00 byte. Names are also
/// INTERNED through a global set, so two bones with the same name share one node; that is
/// why the scan above has a pointer fast path, and it is why a caller must never key a map
/// on the pointer.
///
/// Fills `max_bones * stride` bytes, one NUL-padded name per bone. Returns the bone count,
/// or 0 when the entity carries no animated mesh.
///
/// The STRIDE is the caller's, not a constant here, so there is exactly one owner of it. A
/// matching pair of constants either side of a P/Invoke is a thing that drifts, and it
/// drifts silently: the reader simply starts finding the tail of the previous name.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityBoneNames(uint32_t entity, char* out_names, uint32_t stride,
                                     uint32_t max_bones) {
    constexpr uint8_t kAnimatedMeshId = 6u;

    if (out_names == nullptr || max_bones == 0u || stride < 2u) {
        return 0u;
    }

    std::memset(out_names, 0, (size_t)max_bones * stride);

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity) || ((read32(entity + 0x24u) >> kAnimatedMeshId) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t component = EntityComponent(base, entity, kAnimatedMeshId);

    if (!plausible(component)) {
        return 0u;
    }

    const uint32_t skeleton = read32(component + 0x94u);
    if (!plausible(skeleton)) {
        return 0u;
    }

    const uint32_t table = read32(skeleton + 0x2Cu);
    const uint32_t table_end = read32(skeleton + 0x30u);
    if (!plausible(table) || table_end <= table) {
        return 0u;
    }

    uint32_t bones = (table_end - table) / 8u;
    if (bones > max_bones) {
        bones = max_bones;
    }

    for (uint32_t i = 0u; i < bones; ++i) {
        char* slot = out_names + ((size_t)i * stride);
        const uint32_t node = read32(table + (i * 8u) + 4u);
        if (!plausible(node)) {
            continue;  // unnamed — the guest's own empty-string case, not an error
        }

        const uint32_t chars = read32(node);
        uint32_t length = read32(node + 4u);
        if (!plausible(chars)) {
            continue;
        }

        // The length is the guest's, so it is clamped rather than trusted: a torn read of a
        // string being rebuilt would otherwise walk off into whatever follows it.
        if (length > stride - 1u) {
            length = stride - 1u;
        }

        for (uint32_t c = 0u; c < length; ++c) {
            const uint8_t byte = read8(chars + c);
            if (byte == 0u) {
                break;
            }

            slot[c] = (char)byte;
        }
    }

    return bones;
}

/// Each guest skeleton bone's PARENT index, -1 for a root, in the order of
/// RefiiHostGetEntityBoneNames. The skeleton's bone table (comp6+0x94 -> +0x2C..+0x30) holds
/// 8-byte entries {parent index, name}; `sub_82B7F348` builds the tree the model is matched
/// against from that first word (`*(_DWORD *)v11 == -1` is the root).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityBoneParents(uint32_t entity, int32_t* out_parents,
                                                             uint32_t max_bones) {
    constexpr uint8_t kAnimatedMeshId = 6u;
    if (out_parents == nullptr || max_bones == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity) || ((read32(entity + 0x24u) >> kAnimatedMeshId) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t component = EntityComponent(base, entity, kAnimatedMeshId);
    if (!plausible(component)) {
        return 0u;
    }

    const uint32_t skeleton = read32(component + 0x94u);
    if (!plausible(skeleton)) {
        return 0u;
    }

    const uint32_t table = read32(skeleton + 0x2Cu);
    const uint32_t table_end = read32(skeleton + 0x30u);
    if (!plausible(table) || table_end <= table) {
        return 0u;
    }

    uint32_t bones = (table_end - table) / 8u;
    if (bones > max_bones) {
        bones = max_bones;
    }

    for (uint32_t i = 0u; i < bones; ++i) {
        const int32_t parent = static_cast<int32_t>(read32(table + (i * 8u)));
        out_parents[i] = parent >= 0 && static_cast<uint32_t>(parent) < bones ? parent : -1;
    }

    return bones;
}

/// The command-stream rings, read-only. `sub_8222ABE0` blocks when the reader traps the
/// writer, and these four numbers per slot are the whole condition.
///
/// The frame-constant sources the title's render passes read, as host floats:
///   [0..3]    ambient light colour, `*(0x834A4D60)+208` (the second directional light, whose
///             colour is the theme's AMBIENT_COLOUR) -> PS c20 / VS c54 (`sub_821B1A88`)
///   [4..7]    its direction, `*(0x834A4D60)+304` (the sun's, as the light travels)
///             -> PS c23 = (-dir, 4), VS c19 = -dir (`sub_821B16F8`)
///   [8..11]   lightmap factor, `0x834A4D40`       -> PS c21 = (xyz, 1.3), VS c18 = (xyz, 2.86)
///   [12..15]  normal-map darkening, `0x834A4D50`  -> PS c22
///   [16..35]  atmosphere block, `0x834A0EC0`, 5 float4 -> PS c64..68 / VS c108..112
///             (filled by `sub_82263570` while the sky is on)
///   [36..131] the scene's THEME BLOCK, 24 float4 at scene+16: MAIN_LIGHT_COLOUR,
///             MAIN_LIGHT_SHADOWED, AMBIENT_COLOUR, LIGHTMAP_COLOUR, AMBIENT_NORMAL_MAP_
///             DARKENING_COLOUR, DISTANT_SHADOW_FACTOR, ENABLE_LIGHT_ZONE_SHADOW_CULLING,
///             SUN_INTENSITY, SKY_BETA_RAYLEIGH_MULTIPLIER, SKY_BETA_MIE_MULTIPLIER, SKY_COLOUR,
///             SKY_COMPLEMENTARY_COLOUR, SKY_COMPLEMENTARY_COLOUR_BIAS, SKY_SUNSET_COLOUR,
///             SKY_FOGGING_START, NEAR_DISTANCE, NEAR_DENSITY, FAR_DISTANCE, FAR_DENSITY,
///             CLOSE_FOG_COLOUR, CLOSE_FOG_MAX_DISTANCE, then tone mapping (`sub_83288CA0`'s
///             table; the render-side copy `sub_822AF480` writes at scene+16)
///   [132..135] the sun light's colour (twin+208), [136..139] its direction (twin+304),
///   [140] its shadow enable (byte twin+336), [141] intensity (twin+448), [142] twin+452,
///   [143] the scene's global mist strength (O+3040.x), [144..146] the wind direction and
///   [147] its speed (O+3384..3399).
/// The scene is `*(*(0x8349F79C) + 4*idx)` and the sun twin `*(*(0x8349F7AC) + 4*slot)`, with
/// idx = L+4 and slot = L+8 off the sky owner L = `*(wg+92)`,
/// wg = `*(*(*(*(0x83496AB8)+12)+88)+4)` (`sub_8234EF58`, `sub_825A14C0`).
///
/// THE SOURCES, NOT THE DEVICE: the passes copy these into the D3D constant shadow as they
/// run, and in this host they never run (the scene stream is drained, the twins are kept, the
/// pass bodies are not entered), so the shadow is zero while these are live. Returns the floats
/// written (148), or 0 when the lighting objects are not up yet; a missing scene or sun leaves
/// its part zero.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicFrame(float* out, uint32_t capacity) {
    constexpr uint32_t kFloats = 148u;
    if (out == nullptr || capacity < kFloats) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    for (uint32_t i = 0; i < kFloats; ++i) {
        out[i] = 0.0f;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };

    const uint32_t lighting = read32(refii::guest::Address(0x834A4D60u));
    if (!plausible(lighting)) {
        return 0;
    }
    read_floats(lighting + 208u, out, 4u);
    read_floats(lighting + 304u, out + 4, 4u);
    read_floats(refii::guest::Address(0x834A4D40u), out + 8, 4u);
    read_floats(refii::guest::Address(0x834A4D50u), out + 12, 4u);
    read_floats(refii::guest::Address(0x834A0EC0u), out + 16, 20u);

    // The sky owner, through the game mode's world graphics.
    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return kFloats;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    if (!plausible(owner)) {
        return kFloats;
    }

    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (plausible(scenes) && scene_index < 4096u) {
        const uint32_t scene = read32(scenes + scene_index * 4u);
        if (plausible(scene)) {
            read_floats(scene + 16u, out + 36, 96u);
            // [143] the scene's global mist strength (O+3040.x), the mist binder's factor.
            read_floats(scene + 3040u, out + 143, 1u);
            // [144..146] the wind's unit direction and [147] its speed (O+3384..3399, the wind
            // object's command `sub_8229B798`): the tree and grass sway terms.
            read_floats(scene + 3384u, out + 144, 4u);
        }
    }

    const uint32_t sun_slot = read32(owner + 8u);
    const uint32_t twins = read32(refii::guest::Address(0x8349F7ACu));
    if (plausible(twins) && sun_slot < 65536u) {
        const uint32_t sun = read32(twins + sun_slot * 4u);
        if (plausible(sun)) {
            read_floats(sun + 208u, out + 132, 4u);
            read_floats(sun + 304u, out + 136, 4u);
            out[140] = static_cast<float>(GuestWordAt(base, sun + 336u) >> 24);
            read_floats(sun + 448u, out + 141, 2u);
        }
    }
    return kFloats;
}

/// The render scene the classic theme lives in: `*(0x8349F79C)[owner+4]`, with owner reached from
/// the game mode (`*(0x83496AB8)+12 -> +88 -> +4 -> +92`). 0 when the world is not up.
static uint32_t ClassicThemeScene(uint8_t* base) {
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    const uint32_t a = plausible(mode) ? read32(mode + 12u) : 0u;
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0u;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    return plausible(scene) ? scene : 0u;
}

/// THE THEME'S TEXTURE SLOTS, raw, for the classic sky and water. The theme (T = scene+16) holds,
/// per slot s, a vector of {f32 weight, u32 texture hash} at T + 16 x (194 + s): +4 begin, +8 end
/// (`sub_821D0C00`, 0x821D0C14..0x821D0C30). Slots: 0 TONE_MAPPING_OVERLAY, 1 SKY_OVERLAY,
/// 2 SKY_SUNBEAMS, 3 SKY_SUNDISC, 4 SKY_STREAKSTEXTURE, 5 SKY_GLARETEXTURE, 6 SKY_MOONTEXTURE,
/// 7 SKY_MOONGLARETEXTURE, 8..11 CLOUDS_LAYERn_DENSITY_MAP, 12 WATER_NORMAL_MAP,
/// 13 WATER_DETAIL_MAP, 14 WATER_HEIGHT_MAP. Out, u32 words, per slot 17: [0] the element count
/// (all of them, even past the 8 copied), then up to 8 pairs (weight bits, hash) in the vector's
/// own order — the caller picks and normalises as `sub_821D0C00` does. Returns the words written
/// (15 x 17), or 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicThemeTextures(uint32_t* out, uint32_t capacity) {
    constexpr uint32_t kSlots = 15u;
    constexpr uint32_t kPairs = 8u;
    constexpr uint32_t kStride = 1u + kPairs * 2u;
    if (out == nullptr || capacity < kSlots * kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0;
    }
    const uint32_t theme = scene + 16u;
    for (uint32_t slot = 0; slot < kSlots; ++slot) {
        uint32_t* row = out + slot * kStride;
        for (uint32_t i = 0; i < kStride; ++i) {
            row[i] = 0u;
        }
        const uint32_t header = theme + 16u * (194u + slot);
        const uint32_t begin = GuestWordAt(base, header + 4u);
        const uint32_t end = GuestWordAt(base, header + 8u);
        if (begin < 0x10000u || end < begin || end - begin > 0x10000u) {
            continue;
        }
        const uint32_t count = (end - begin) >> 3;
        row[0] = count;
        const uint32_t copied = count < kPairs ? count : kPairs;
        for (uint32_t i = 0; i < copied; ++i) {
            row[1u + i * 2u] = GuestWordAt(base, begin + i * 8u);
            row[2u + i * 2u] = GuestWordAt(base, begin + i * 8u + 4u);
        }
    }
    return kSlots * kStride;
}

/// THE WHOLE THEME BLOCK and the sky object's sun and moon, for the classic sky. Out, floats:
///   [0 .. 775]  the scene's theme block, 194 parameters x 4 lanes (scene+16 + 16 x index, the
///               table `sub_83288CA0` fills; Engine_ClassicThemeParams names them)
///   [776..779]  sun colour (sky S+0), [780..783] sun direction (S+16)
///   [784..787]  moon direction (M+0), [788] moon phase (M+20, an integer 0..7)
/// S = sky+144, M = sky+208, the sky being the render twin named by owner+16 (vtable 0x820052C8,
/// slot 17 `sub_8228F038` draws dome, moon, stars and clouds). Returns the floats written, 0
/// when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTheme(float* out, uint32_t capacity) {
    constexpr uint32_t kTheme = 194u * 4u;
    constexpr uint32_t kFloats = kTheme + 13u;
    if (out == nullptr || capacity < kFloats) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    const uint32_t a = plausible(mode) ? read32(mode + 12u) : 0u;
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scene = ClassicThemeScene(base);
    if (!plausible(owner) || scene == 0u) {
        return 0;
    }
    for (uint32_t i = 0; i < kFloats; ++i) {
        out[i] = 0.0f;
    }
    read_floats(scene + 16u, out, kTheme);
    const uint32_t twins = read32(refii::guest::Address(0x8349F7ACu));
    const uint32_t sky_slot = read32(owner + 16u);
    const uint32_t sky = plausible(twins) && sky_slot < 65536u ? read32(twins + sky_slot * 4u) : 0u;
    if (plausible(sky) && read32(sky) == refii::guest::Address(0x820052C8u)) {
        read_floats(sky + 144u, out + kTheme, 4u);
        read_floats(sky + 160u, out + kTheme + 4u, 4u);
        read_floats(sky + 208u, out + kTheme + 8u, 4u);
        out[kTheme + 12u] = static_cast<float>(read32(sky + 228u));
    }
    return kFloats;
}

/// THE LIGHT HALOS the guest draws this frame: the light manager's halo list, a global vector of
/// light pointers at 0x834A4DA0 (begin) / 0x834A4DA4 (end) that `sub_821D5628` refills each frame
/// from families 8 and 9, and that `sub_82274370` walks to draw LIGHTHALO (VS 0x9B / PS 0x9C,
/// ONE/ONE, depth-tested, no depth write) into the half-res bloom layer. Per light L the draw
/// (`sub_82273E60`) needs L+240 (intensity) and L+236 (size) both >= 0.01 and a texture at L+460;
/// it places a camera-facing quad of half-extent L+236 (aspect 1, 1 in the main pass) at L+288
/// and sets PS c123 = L+208.rgb x L+448 x L+240. Out, per halo 12 floats:
///   [0..2] centre L+288, [3] half-extent L+236, [4..6] colour (the c123 product), [7] the
///   water pass's extra factor L+244, [8..9] its aspect L+248 / L+252, [10] the texture's
///   resource hash (*(L+460)+28) as raw bits, [11] the light pointer as raw bits.
/// Returns the halos written (at most capacity / 12), 0 when the list is empty.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLightHalos(float* out, uint32_t capacity) {
    constexpr uint32_t kFloats = 12u;
    if (out == nullptr || capacity < kFloats) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(float));
        return value;
    };
    const auto put_bits = [](float* to, uint32_t bits) { std::memcpy(to, &bits, sizeof(float)); };

    const uint32_t begin = read32(refii::guest::Address(0x834A4DA0u));
    const uint32_t end = read32(refii::guest::Address(0x834A4DA4u));
    if (!plausible(begin) || end < begin || end - begin > 4u * 4096u) {
        return 0;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && (written + 1u) * kFloats <= capacity; at += 4u) {
        const uint32_t light = read32(at);
        if (!plausible(light)) {
            continue;
        }
        const float intensity = read_float(light + 240u);
        const float size = read_float(light + 236u);
        const uint32_t texture = read32(light + 460u);
        if (!(intensity >= 0.01f) || !(size >= 0.01f) || !plausible(texture)) {
            continue;
        }
        const float scale = read_float(light + 448u) * intensity;
        float* o = out + written * kFloats;
        o[0] = read_float(light + 288u);
        o[1] = read_float(light + 292u);
        o[2] = read_float(light + 296u);
        o[3] = size;
        o[4] = read_float(light + 208u) * scale;
        o[5] = read_float(light + 212u) * scale;
        o[6] = read_float(light + 216u) * scale;
        o[7] = read_float(light + 244u);
        o[8] = read_float(light + 248u);
        o[9] = read_float(light + 252u);
        put_bits(o + 10, read32(texture + 28u));
        put_bits(o + 11, light);
        ++written;
    }
    return written;
}

/// The render scene's ACTIVE LIGHTS, as the light manager holds them: the set the title's
/// per-draw light query walks (`sub_82AB6360`), so the classic renderer can assign lights to
/// a draw the way the guest does. LM = scene+3684; its entries are 32 bytes at `*(LM+8)`
/// (vec4 position.xyz / radius.w, +16 the light object L; the sun's radius is 1e10) and
/// `*(LM+24)` is the 64-bit mask of live entries, bit i for entry i. Per light, 20 floats:
///   [0..2] position, [3] radius (the entry's), [4..6] colour (L+208), [7] intensity (L+448),
///   [8..10] direction (L+304), [11] type (L+4: 8 point, 9 spot, 10 directional),
///   [12] range (L+320), [13] cone angle (L+324), [14] range scale (L+452),
///   [15] flags: bit0 L+336, bit1 L+337, bit2 L+338, bit3 L+484 (the shadow-casting bytes),
///   [16] lighting zone id (L+416) as raw bits, [17] the entry index, [18..19] reserved.
/// Returns the number of lights written (at most `capacity / 20`), or 0 when the scene is not
/// up. The fields are the ones `sub_82A6CDD0` packs into the light block and the query reads.
/// The SPOT LIGHTS' RECEIVE FRUSTUMS: a spot takes a draw only when the draw's box is inside its
/// frustum (light vtable +96 = `sub_82A6CC98` -> +92 `sub_82A6CC80` -> `sub_822362F8(light+560,
/// box)`: planes vec4 at +0, count at +0x100; a box is out when, for some plane, the box corner
/// along the normal gives dot(n, p) + w < 0). Per live spot of the manager RefiiHostGetClassicLights
/// walks: [0] entry index, [1] plane count (<= 16), [2..65] the planes. out[0] = spots; returns the
/// floats written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSpotFrustums(float* out, uint32_t capacity) {
    constexpr uint32_t kRecord = 66u;
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const uint32_t scene = ClassicThemeScene(base);
    const uint32_t manager = scene != 0u ? GuestWordAt(base, scene + 3684u) : 0u;
    if (!plausible(manager)) {
        return 1u;
    }
    const uint32_t entries = GuestWordAt(base, manager + 8u);
    const uint64_t mask = (static_cast<uint64_t>(GuestWordAt(base, manager + 24u)) << 32) | GuestWordAt(base, manager + 28u);
    if (!plausible(entries)) {
        return 1u;
    }
    uint32_t used = 1u;
    uint32_t spots = 0u;
    for (uint32_t index = 0; index < 64u && used + kRecord <= capacity; ++index) {
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t light = GuestWordAt(base, entries + index * 32u + 16u);
        if (!plausible(light) || GuestWordAt(base, light + 4u) != 9u) {
            continue;
        }
        const uint32_t frustum = light + 560u;
        uint32_t count = GuestWordAt(base, frustum + 0x100u);
        if (count > 16u) {
            count = 16u;
        }
        float* f = out + used;
        f[0] = static_cast<float>(index);
        f[1] = static_cast<float>(count);
        for (uint32_t k = 0; k < 64u; ++k) {
            const uint32_t bits = k < count * 4u ? GuestWordAt(base, frustum + k * 4u) : 0u;
            std::memcpy(f + 2 + k, &bits, sizeof(float));
        }
        used += kRecord;
        ++spots;
    }
    out[0] = static_cast<float>(spots);
    return used;
}

/// The manager's LOCAL lights (types 8 point and 9 spot) as their packer and their shadow
/// object state them. A light's manager entry is its bounding sphere (L+112, copied by
/// `sub_82240CE8`), but `sub_82A6CDD0` packs the slot's position from L+288; and a spot with a
/// shadow object (`Render_LightHasActiveShadow`: S = L+176, S+8, L+336, !L+338) renders a 512²
/// map from L+288 along L+304 (`Render_SpotShadow_UpdateCamera`) with the parameters
/// `Render_ShadowObject_SetParams` stored on S. 16 floats per light:
///   [0] entry index, [1..3] position L+288, [4..6] direction L+304, [7] has a map (0/1),
///   [8] fovY 2·L+324, [9] near S+56, [10] far L+452·L+320, [11] depth bias S+48,
///   [12] slope-scale bias S+52, [13..15] reserved.
/// The map's matrices (S+448, S+512) are not read: the guest writes them while it draws the
/// map, which it does not do here. Returns the lights written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicLocalLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 16u;
    if (out == nullptr || capacity < kStride) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };
    const auto byte_at = [base](uint32_t at) { return (GuestWordAt(base, at & ~3u) >> (24u - 8u * (at & 3u))) & 0xFFu; };
    const uint32_t scene = ClassicThemeScene(base);
    const uint32_t manager = scene != 0u ? GuestWordAt(base, scene + 3684u) : 0u;
    if (!plausible(manager)) {
        return 0u;
    }
    const uint32_t entries = GuestWordAt(base, manager + 8u);
    const uint64_t mask = (static_cast<uint64_t>(GuestWordAt(base, manager + 24u)) << 32) | GuestWordAt(base, manager + 28u);
    if (!plausible(entries)) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t index = 0; index < 64u && (written + 1u) * kStride <= capacity; ++index) {
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t light = GuestWordAt(base, entries + index * 32u + 16u);
        const uint32_t type = plausible(light) ? GuestWordAt(base, light + 4u) : 0u;
        if (type != 8u && type != 9u) {
            continue;
        }
        float* o = out + written * kStride;
        std::memset(o, 0, kStride * sizeof(float));
        o[0] = static_cast<float>(index);
        for (uint32_t k = 0; k < 3u; ++k) {
            o[1 + k] = read_float(light + 288u + 4u * k);
            o[4 + k] = read_float(light + 304u + 4u * k);
        }
        const uint32_t shadow = GuestWordAt(base, light + 176u);
        const bool mapped = type == 9u && plausible(shadow) && byte_at(shadow + 8u) != 0u &&
            byte_at(light + 336u) != 0u && byte_at(light + 338u) == 0u;
        o[7] = mapped ? 1.0f : 0.0f;
        o[8] = 2.0f * read_float(light + 324u);
        o[10] = read_float(light + 452u) * read_float(light + 320u);
        if (mapped) {
            o[9] = read_float(shadow + 56u);
            o[11] = read_float(shadow + 48u);
            o[12] = read_float(shadow + 52u);
        }
        ++written;
    }
    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 20u;
    if (out == nullptr || capacity < kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t manager = read32(scene + 3684u);
    if (!plausible(manager)) {
        return 0;
    }
    const uint32_t entries = read32(manager + 8u);
    const uint64_t mask = (static_cast<uint64_t>(read32(manager + 24u)) << 32) | read32(manager + 28u);
    if (!plausible(entries) || mask == 0u) {
        return 0;
    }

    const uint32_t max_lights = capacity / kStride;
    uint32_t written = 0;
    for (uint32_t index = 0; index < 64u && written < max_lights; ++index) {
        // Bit i is entry i: live, the mask read 1 with the sun in entry 0 and entry 63 empty.
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t entry = entries + index * 32u;
        const uint32_t light = read32(entry + 16u);
        if (!plausible(light)) {
            continue;
        }
        float* o = out + written * kStride;
        o[0] = read_float(entry);
        o[1] = read_float(entry + 4u);
        o[2] = read_float(entry + 8u);
        o[3] = read_float(entry + 12u);
        o[4] = read_float(light + 208u);
        o[5] = read_float(light + 212u);
        o[6] = read_float(light + 216u);
        o[7] = read_float(light + 448u);
        o[8] = read_float(light + 304u);
        o[9] = read_float(light + 308u);
        o[10] = read_float(light + 312u);
        o[11] = static_cast<float>(read32(light + 4u));
        o[12] = read_float(light + 320u);
        o[13] = read_float(light + 324u);
        o[14] = read_float(light + 452u);
        const uint32_t flag_word = read32(light + 336u);  // bytes +336..+339
        uint32_t flags = 0;
        flags |= ((flag_word >> 24) & 0xFFu) != 0u ? 1u : 0u;
        flags |= ((flag_word >> 16) & 0xFFu) != 0u ? 2u : 0u;
        flags |= ((flag_word >> 8) & 0xFFu) != 0u ? 4u : 0u;
        flags |= (read32(light + 484u) >> 24) != 0u ? 8u : 0u;
        o[15] = static_cast<float>(flags);
        // The lighting zone id, bit for bit: a zone is a hash and a float cast would round it.
        const uint32_t zone = read32(light + 416u);
        std::memcpy(o + 16, &zone, sizeof(zone));
        o[17] = static_cast<float>(index);
        // The light's address, bit for bit: matches it to RefiiHostGetClassicSceneLights.
        std::memcpy(o + 18, &light, sizeof(light));
        o[19] = 0.0f;
        ++written;
    }
    return written;
}

/// EVERY LIGHT OF THE RENDER SCENE, not only the manager's active list: the scene registry's
/// lights (scene+3412, nodes of 24 bytes at +24..+28 with flag 1 at +4, objects at +12..+16, as
/// `sub_821FF800` walks them; a light is an object whose vtable+8 is `sub_8221FAE0`), less type 11
/// and those with L+338 set — what the manager's gather tests further against ONE eye, the view the
/// guest draws (player one's). For split screen's second view the classic renderer gathers from
/// these by the same rules against player two's eye (Engine_ClassicLights).
/// 36 floats per light: [0..19] as RefiiHostGetClassicLights (sphere L+112, colour L+208,
/// intensity L+448 as player one's eye left it, direction, type, range, cone, range scale, flags,
/// zone, [17] 0, [18] the light's address), then [20..22] position L+288, [23] fade distance
/// L+328, [24] fade range L+332, [25] flicker brightness L+224, [26] the flicker walk L+444,
/// [27..29] AABB min L+80, [30..32] AABB max L+96. Returns the lights written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicSceneLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 36u;
    if (out == nullptr || capacity < kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xE0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    const uint32_t a = plausible(mode) ? read32(mode + 12u) : 0u;
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t registry = scene + 3412u;
    const uint32_t nodes_begin = read32(registry + 24u);
    const uint32_t nodes_end = read32(registry + 28u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || (nodes_end - nodes_begin) % 24u != 0u ||
        (nodes_end - nodes_begin) / 24u > 64u) {
        return 0;
    }
    const uint32_t stamp = refii::guest::Address(0x8221FAE0u);
    const uint32_t max_lights = capacity / kStride;
    uint32_t written = 0;
    for (uint32_t node = nodes_begin; node < nodes_end && written < max_lights; node += 24u) {
        if ((read32(node + 4u) & 1u) == 0u) {
            continue;
        }
        const uint32_t objects = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects) || objects_end < objects || objects_end - objects > 4u * 4096u) {
            continue;
        }
        for (uint32_t at = objects; at < objects_end && written < max_lights; at += 4u) {
            const uint32_t light = read32(at);
            const uint32_t vtable = plausible(light) ? read32(light) : 0u;
            if (!plausible(vtable) || read32(vtable + 8u) != stamp || read32(light + 4u) == 11u ||
                GuestByteAt(base, light + 338u) != 0u) {
                continue;
            }
            float* o = out + written * kStride;
            o[0] = read_float(light + 112u);
            o[1] = read_float(light + 116u);
            o[2] = read_float(light + 120u);
            o[3] = read_float(light + 124u);
            o[4] = read_float(light + 208u);
            o[5] = read_float(light + 212u);
            o[6] = read_float(light + 216u);
            o[7] = read_float(light + 448u);
            o[8] = read_float(light + 304u);
            o[9] = read_float(light + 308u);
            o[10] = read_float(light + 312u);
            o[11] = static_cast<float>(read32(light + 4u));
            o[12] = read_float(light + 320u);
            o[13] = read_float(light + 324u);
            o[14] = read_float(light + 452u);
            const uint32_t flag_word = read32(light + 336u);
            uint32_t flags = 0;
            flags |= ((flag_word >> 24) & 0xFFu) != 0u ? 1u : 0u;
            flags |= ((flag_word >> 16) & 0xFFu) != 0u ? 2u : 0u;
            flags |= ((flag_word >> 8) & 0xFFu) != 0u ? 4u : 0u;
            flags |= (read32(light + 484u) >> 24) != 0u ? 8u : 0u;
            o[15] = static_cast<float>(flags);
            const uint32_t zone = read32(light + 416u);
            std::memcpy(o + 16, &zone, sizeof(zone));
            o[17] = 0.0f;
            std::memcpy(o + 18, &light, sizeof(light));
            o[19] = 0.0f;
            o[20] = read_float(light + 288u);
            o[21] = read_float(light + 292u);
            o[22] = read_float(light + 296u);
            o[23] = read_float(light + 328u);
            o[24] = read_float(light + 332u);
            o[25] = read_float(light + 224u);
            o[26] = read_float(light + 444u);
            for (uint32_t k = 0; k < 3u; ++k) {
                o[27 + k] = read_float(light + 80u + 4u * k);
                o[30 + k] = read_float(light + 96u + 4u * k);
            }
            for (uint32_t k = 33u; k < kStride; ++k) {
                o[k] = 0.0f;
            }
            ++written;
        }
    }
    return written;
}

/// The render scene's SHADOW SPHERES: the category-36 objects of its typed list (O+3412, as
/// RefiiHostGetClassicMists walks it), which `Render_EntityShadowSphere_Update` 0x8222A8A8 creates
/// for the hero, the dog and the villagers (radius 0.5 / 0.5 / 0.4, centre the entity + 0.1 up)
/// and moves every tick. Each holds its last two stated keyframes, shifted by every newer command
/// (`Render_ShadowSphere_ApplyCommand` 0x82A74F20 for the create, `sub_821F5F78` for the type-1
/// moves): centre R+176 (previous) and R+192 (newest), radius R+224 / R+228; the title's draw
/// blends them by its render clock (`Render_ShadowSphere_Interpolate` 0x821E5CB8). 8 floats each:
///   [0..2] previous centre, [3] previous radius, [4..6] newest centre, [7] newest radius.
/// Returns the spheres written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicShadowSpheres(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 8u;
    constexpr uint32_t kShadowSphere = 36u;
    if (out == nullptr || capacity < kStride) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };
    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0u;
    }
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = GuestWordAt(base, list + 4u);
    const uint32_t nodes_end = GuestWordAt(base, list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        if (GuestWordAt(base, node) != kShadowSphere) {
            continue;
        }
        const uint32_t objects_begin = GuestWordAt(base, node + 12u);
        const uint32_t objects_end = GuestWordAt(base, node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin) {
            break;
        }
        for (uint32_t at = objects_begin; at + 4u <= objects_end && (written + 1u) * kStride <= capacity; at += 4u) {
            const uint32_t sphere = GuestWordAt(base, at);
            if (!plausible(sphere)) {
                continue;
            }
            float* o = out + written * kStride;
            for (uint32_t k = 0; k < 3u; ++k) {
                o[k] = read_float(sphere + 176u + 4u * k);
                o[4 + k] = read_float(sphere + 192u + 4u * k);
            }
            o[3] = read_float(sphere + 224u);
            o[7] = read_float(sphere + 228u);
            ++written;
        }
    }
    return written;
}

/// The render scene's MIST VOLUMES: the type-27 objects of the scene's typed list (O+3412:
/// nodes of 24 bytes between *(L+4) and *(L+8), keyed by type at +0, objects at *(node+12)..
/// *(node+16); `sub_82A8B998`, `sub_821F4480`), as the per-draw test and the binder read them
/// (`sub_82B1DE38`, `sub_82A6DD30`, `Render_BindHighSamplersAndPixelBool` 0x82A6EAA8). Per
/// mist, 36 floats:
///   [0..2] bounds min (V+80), [3..5] bounds max (V+96), [6..9] colour (V+192) -> c72,
///   [10] density (V+184), [11] noise offset (V+216) -> the evolve pass's c46,
///   [12] time speed (V+220), [13..14] noise tiling (V+264/268) -> the evolve pass's c31.xy,
///   [15] V+316 (c76.w = 1/it), [16..19] V+320 -> c75, [20] global-strength weight (V+344),
///   [21..24] V+352 -> c73, [25..28] V+368 -> c74, [29..31] extents (V+224), [32..34] origin
///   (V+240), [35] 1 when the height texture (V+276) is set.
/// Returns the mists written (at most capacity / 36), or 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicMists(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 36u;
    if (out == nullptr || capacity < kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = read32(list + 4u);
    const uint32_t nodes_end = read32(list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 0;
    }

    const uint32_t max_mists = capacity / kStride;
    uint32_t written = 0;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        if (read32(node) != 27u) {
            continue;
        }
        const uint32_t objects_begin = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin) {
            break;
        }
        const uint32_t count = (objects_end - objects_begin) / 4u;
        for (uint32_t i = 0; i < count && written < max_mists; ++i) {
            const uint32_t mist = read32(objects_begin + i * 4u);
            if (!plausible(mist)) {
                continue;
            }
            float* o = out + written * kStride;
            read_floats(mist + 80u, o, 3u);
            read_floats(mist + 96u, o + 3, 3u);
            read_floats(mist + 192u, o + 6, 4u);
            read_floats(mist + 184u, o + 10, 1u);
            read_floats(mist + 216u, o + 11, 1u);
            read_floats(mist + 220u, o + 12, 1u);
            read_floats(mist + 264u, o + 13, 2u);
            read_floats(mist + 316u, o + 15, 1u);
            read_floats(mist + 320u, o + 16, 4u);
            read_floats(mist + 344u, o + 20, 1u);
            read_floats(mist + 352u, o + 21, 4u);
            read_floats(mist + 368u, o + 25, 4u);
            read_floats(mist + 224u, o + 29, 3u);
            read_floats(mist + 240u, o + 32, 3u);
            o[35] = plausible(read32(mist + 276u)) ? 1.0f : 0.0f;
            ++written;
        }
        break;
    }
    return written;
}

/// THE PARTICLE-SYSTEM INSTANCE BEHIND EACH QUAD AND TRAIL TWIN, so the host can tell which
/// effects the guest is drawing itself. The twin keeps no pointer back to the sim, so the link is
/// walked from the sim side: every live instance (the vector at dword_8349E48C, {instance,
/// refnode} stride 8) whose class carries the render component at +0x150 (`sub_8322F600`
/// 0x8322F6C8 `addi r3,r31,0x150` -> `sub_83228588`) holds the renderer holder at component +28
/// (`*(a1+28) = v39`), i.e. instance +0x16C; the holder (vtable off_82001AC0) keeps the quad/trail
/// proxy at +24 and the proxy its twin handle at +4, -1 when none (`sub_82456E60`); the handle
/// indexes the twin table dword_8349F7AC and the twin repeats it at +16 (`sub_821E1F40`
/// `v24[4] = v21`). A class without the component reads past its end here, so a link counts
/// only when the holder's vtable and the table's twin both agree. Child components (the 496-byte
/// class's map at +460) are not walked: their twins have no owner.
static void ParticleTwinOwners(uint8_t* base, std::unordered_map<uint32_t, uint32_t>& owners) {
    owners.clear();
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const uint32_t begin = read32(refii::guest::Address(0x8349E490u));
    const uint32_t end = read32(refii::guest::Address(0x8349E494u));
    const uint32_t table = read32(refii::guest::Address(0x8349F7ACu));
    const uint32_t table_end = read32(refii::guest::Address(0x8349F7B0u));
    const uint32_t holder_vtable = refii::guest::Address(0x82001AC0u);
    if (!plausible(begin) || end < begin || end - begin > 8u * 8192u || !plausible(table) || table_end < table) {
        return;
    }
    const uint32_t handles = (table_end - table) / 4u;
    for (uint32_t at = begin; at + 8u <= end; at += 8u) {
        const uint32_t instance = read32(at);
        if (!plausible(instance)) {
            continue;
        }
        const uint32_t holder = read32(instance + 0x16Cu);
        if (!plausible(holder) || read32(holder) != holder_vtable) {
            continue;
        }
        const uint32_t proxy = read32(holder + 24u);
        if (!plausible(proxy)) {
            continue;
        }
        const uint32_t handle = read32(proxy + 4u);
        if (handle >= handles) {
            continue;
        }
        const uint32_t twin = read32(table + handle * 4u);
        if (plausible(twin) && read32(twin + 16u) == handle) {
            owners[twin] = instance;
        }
    }
}

/// The render scene's PARTICLE GROUPS as their render-side twins hold them: the data the sim
/// states to the renderer through the twin's receivers (`sub_821D8EA8` the records, material
/// and bounds; `sub_821D9D40` the transform), which the title's point-sprite draw
/// (`sub_821CA790`) turns into FACE_ME quads. Walks the scene's typed object list (O+3412) for
/// the two particle classes (types 12 and 30, `sub_82A43EA0` / `sub_82A449A8`) and writes, per
/// group whose +240 kind is 1 (quads; trails are 2 and 3):
///   a 320-float header, then count x 12 floats of 48-byte records as the stream holds them.
/// Header (R = the render object, D = *(*(R+180)+4) its render data, M = *(D+48) its material):
///   [0] R (bits) [1] R+240 kind [2] count R+176 [3] R+244
///   [4..6] bounds min R+80 [7] the owning particle-system instance (bits; 0 unknown,
///   ParticleTwinOwners) [8..10] bounds max R+96
///   [12..16] fade distances R+384..400 (in near, in far, out near, out far, alpha)
///   [17] scale R+404 [18] R+408 [19] R+409 [20] R+412
///   [21..36] the current transform R+320, row-major as the title lerps it (`sub_8288F408`)
///   [37] M (bits) [38] texture count M+180 [39..41] blend src/dst/op M+36..44 (engine enums:
///       0 ZERO 1 ONE 2 SRCCOLOR 3 INVSRCCOLOR 4 SRCALPHA 5 INVSRCALPHA 6 DESTCOLOR
///       7 INVDESTCOLOR 8 DESTALPHA 9 INVDESTALPHA; ops 0 ADD 1 SUB 2 REVSUB 3 MIN 4 MAX)
///   [42] soft fade distance M+48 [43] M+52 displacement [44] M+53 alpha ref 16 [45] M+56 fixed axes
///   [46..49] D+32 the fixed-axis quaternion [50] keyframe count (at most 19)
///   [51..53] the lighting object's +496 (the ambient the lit particle adds) [54] probe count
///   [64..253] 19 keyframes x 10: colour(4) size x, size y, global scale x, y, self-illum, start
///       (M+8 vector, stride 40: +0 colour, +16/+20 sizes, +24/+28 global scales, +32, +36)
///   [254..333] 4 textures x 20: key word, start time, x divisions, y divisions, 16 words of the
///       key's text when the key is a pointer to one (M+80 + 24 i: +4 key, +8 start, +20/+21);
///       the draw takes up to four (`sub_821CA790`, blend index clamp(M+180 - 1, 0, 3))
/// out[0] = groups written, out[1] = floats used; returns the floats used, 0 when the scene is
/// not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicParticles(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 340u;
    constexpr uint32_t kRecord = 12u;
    constexpr uint32_t kMaxRecords = 8192u;
    if (out == nullptr || capacity < 2u + kHeader) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&read32](uint32_t at) {
        return (read32(at & ~3u) >> (8u * (3u - (at & 3u)))) & 0xFFu;
    };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const auto put_bits = [](float* to, uint32_t bits) { std::memcpy(to, &bits, sizeof(float)); };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = read32(list + 4u);
    const uint32_t nodes_end = read32(list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 0;
    }
    const uint32_t lighting = read32(refii::guest::Address(0x834A4D60u));
    // The probe container the lit particle's ambient asks (`sub_821907F8`): *(scene+3408), read
    // at 0x821CA98C..A0 as *(*(view+4)+20)+0xD50. Its +72/+76 vector holds 12-byte entries.
    const uint32_t container = read32(scene + 3408u);
    const uint32_t probes = plausible(container)
        ? (read32(container + 76u) - read32(container + 72u)) / 12u
        : 0u;

    uint32_t used = 2u;
    static thread_local std::unordered_map<uint32_t, uint32_t> owners;
    ParticleTwinOwners(base, owners);
    uint32_t groups = 0u;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        const uint32_t type = read32(node);
        if (type != 12u && type != 30u) {
            continue;
        }
        const uint32_t objects_begin = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin) {
            continue;
        }
        const uint32_t count_objects = (objects_end - objects_begin) / 4u;
        for (uint32_t i = 0; i < count_objects; ++i) {
            const uint32_t r = read32(objects_begin + i * 4u);
            if (!plausible(r) || read32(r + 240u) != 1u) {
                continue;
            }
            uint32_t count = read32(r + 176u);
            if (count == 0u || count > kMaxRecords) {
                continue;
            }
            const uint32_t shared = read32(r + 180u);
            const uint32_t data = plausible(shared) ? read32(shared + 4u) : 0u;
            if (!plausible(data)) {
                continue;
            }
            const uint32_t records = read32(data + 8u);
            const uint32_t material = read32(data + 48u);
            if (!plausible(records) || !plausible(material)) {
                continue;
            }
            if (used + kHeader + count * kRecord > capacity) {
                break;
            }
            float* h = out + used;
            for (uint32_t k = 0; k < kHeader; ++k) {
                h[k] = 0.0f;
            }
            put_bits(h + 0, r);
            { const auto owner = owners.find(r); put_bits(h + 7, owner != owners.end() ? owner->second : 0u); }
            h[1] = 1.0f;
            h[2] = static_cast<float>(count);
            h[3] = static_cast<float>(static_cast<int32_t>(read32(r + 244u)));
            read_floats(r + 80u, h + 4, 3u);
            read_floats(r + 96u, h + 8, 3u);
            read_floats(r + 384u, h + 12, 5u);
            read_floats(r + 404u, h + 17, 1u);
            h[18] = static_cast<float>(read8(r + 408u));
            h[19] = static_cast<float>(read8(r + 409u));
            read_floats(r + 412u, h + 20, 1u);
            read_floats(r + 320u, h + 21, 16u);
            put_bits(h + 37, material);
            const uint32_t textures = read32(material + 180u);
            h[38] = static_cast<float>(textures > 4u ? 4u : textures);
            h[39] = static_cast<float>(read32(material + 36u));
            h[40] = static_cast<float>(read32(material + 40u));
            h[41] = static_cast<float>(read32(material + 44u));
            read_floats(material + 48u, h + 42, 1u);
            h[43] = static_cast<float>(read8(material + 52u));
            h[44] = static_cast<float>(read8(material + 53u));
            h[45] = static_cast<float>(read8(material + 56u));
            read_floats(data + 32u, h + 46, 4u);
            const uint32_t keys_begin = read32(material + 8u);
            const uint32_t keys_end = read32(material + 12u);
            uint32_t keys = plausible(keys_begin) && keys_end >= keys_begin ? (keys_end - keys_begin) / 40u : 0u;
            if (keys > 19u) {
                keys = 19u;
            }
            h[50] = static_cast<float>(keys);
            if (plausible(lighting)) {
                read_floats(lighting + 496u, h + 51, 3u);
            }
            h[54] = static_cast<float>(probes);
            for (uint32_t k = 0; k < keys; ++k) {
                const uint32_t key = keys_begin + k * 40u;
                float* o = h + 64 + k * 10;
                read_floats(key, o, 4u);
                read_floats(key + 16u, o + 4, 2u);
                read_floats(key + 24u, o + 6, 2u);
                read_floats(key + 32u, o + 8, 2u);
            }
            for (uint32_t t = 0; t < textures && t < 4u; ++t) {
                const uint32_t entry = material + 80u + t * 24u;
                float* o = h + 254 + t * 20;
                const uint32_t key = read32(entry + 4u);
                put_bits(o, key);
                read_floats(entry + 8u, o + 1, 1u);
                o[2] = static_cast<float>(static_cast<int8_t>(read8(entry + 20u)));
                o[3] = static_cast<float>(static_cast<int8_t>(read8(entry + 21u)));
                // A key that points at printable text is the texture's path; carry it.
                if (plausible(key)) {
                    bool text = true;
                    for (uint32_t c = 0; c < 4u && text; ++c) {
                        const uint32_t ch = read8(key + c);
                        text = ch >= 0x20u && ch < 0x7Fu;
                    }
                    if (text) {
                        for (uint32_t w = 0; w < 16u; ++w) {
                            put_bits(o + 4 + w, read32(key + w * 4u));
                        }
                    }
                }
            }
            read_floats(records, h + kHeader, count * kRecord);
            used += kHeader + count * kRecord;
            ++groups;
        }
    }
    out[0] = static_cast<float>(groups);
    out[1] = static_cast<float>(used);
    return groups == 0u ? 0u : used;
}

/// The render scene's TRAILS (R+240 == 2) and DOUBLE TRAILS (3), as their render twins hold them,
/// for the title's TRAILS / DOUBLE_TRAILS vertex shaders. The draw (`sub_821CA5E8` 0x821CA6A4)
/// takes D = *(*(R+0xB4)+4) and the material TM = *(D+0x40) (trails) or DM = *(D+0x2C) (double),
/// drawing into the displacement target instead when TM+0x34 / DM+0x28 (IsDisplacementTexture) is
/// set (`Render_EffectStripDraw` 0x82A597B0 / `Render_EffectStripDraw2` 0x82A5A818: VS 0041 / 0043,
/// PS 0037, the renderer's ONE / ONE; header [55]).
/// The stream it draws is the ring at D (+8 base, +20 head, +32 mask, +36 count c) unrolled with
/// a point either side, as `sub_82179C00` / `sub_82A592F0` fill it: c + 3 points of 8 floats.
/// Per group a 288-float header, then the points:
///   [0] R (bits) [1] kind [2] n = R+176 [3] c [4] s (TM+0x20 / DM+0x14, int) [5] vertices
///   2((n-1)s+1) [6] closed loop (kind 2) [7] TM or DM (bits) [8..11] c366 (s, 1/((n-1)s), 1/s, 0)
///   [12..16] fades R+384..400 [17..32] transform R+320 [33] fixed axis TM+0x38 [34..37] D+48
///   quaternion [38] the owning particle-system instance (bits; 0 unknown, ParticleTwinOwners)
///   [42..44] blend src/dst/op (B+0/4/8; B = TM+0x24 / DM+0x18) [45] soft distance B+12
///   [46] B+16 [47] B+17 (alpha ref 16) [48] texture count [50] keyframes (<= 19)
///   [51..53] the lighting object's +496 [54] probe count
///   [56..207] 19 keyframes x 8: K+0..12 colour, K+16, K+20, K+24, 0 (TM/DM+8, stride 28)
///   [208..287] 4 textures x 20 as the quad export's [254..333] (block TM+0x50 / DM+0x2C)
/// out[0] = groups, out[1] = floats used; returns the floats used, 0 when none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTrails(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 288u;
    constexpr uint32_t kPoint = 8u;
    constexpr uint32_t kMaxPoints = 4096u;
    if (out == nullptr || capacity < 2u + kHeader) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&read32](uint32_t at) {
        return (read32(at & ~3u) >> (8u * (3u - (at & 3u)))) & 0xFFu;
    };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const auto put_bits = [](float* to, uint32_t bits) { std::memcpy(to, &bits, sizeof(float)); };

    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0;
    }
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = read32(list + 4u);
    const uint32_t nodes_end = read32(list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 0;
    }
    const uint32_t lighting = read32(refii::guest::Address(0x834A4D60u));
    const uint32_t container = read32(scene + 3408u);
    const uint32_t probes = plausible(container)
        ? (read32(container + 76u) - read32(container + 72u)) / 12u
        : 0u;

    uint32_t used = 2u;
    static thread_local std::unordered_map<uint32_t, uint32_t> owners;
    ParticleTwinOwners(base, owners);
    uint32_t groups = 0u;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        const uint32_t type = read32(node);
        if (type != 12u && type != 30u) {
            continue;
        }
        const uint32_t objects_begin = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin) {
            continue;
        }
        for (uint32_t at = objects_begin; at + 4u <= objects_end; at += 4u) {
            const uint32_t r = read32(at);
            if (!plausible(r)) {
                continue;
            }
            const uint32_t kind = read32(r + 240u);
            if (kind != 2u && kind != 3u) {
                continue;
            }
            const uint32_t shared = read32(r + 180u);
            const uint32_t d = plausible(shared) ? read32(shared + 4u) : 0u;
            if (!plausible(d)) {
                continue;
            }
            const uint32_t m = read32(d + (kind == 2u ? 0x40u : 0x2Cu));
            if (!plausible(m)) {
                continue;
            }
            // IsDisplacementTexture (the key's +0x34 / +0x28): drawn into the displacement target.
            const bool displaces = read8(m + (kind == 2u ? 0x34u : 0x28u)) != 0u;
            const uint32_t n = read32(r + 176u);
            const uint32_t c = read32(d + 36u);
            if (n < 2u || c < n || c > kMaxPoints) {
                continue;  // `sub_82179C00`'s own exit
            }
            const uint32_t points = c + 3u;
            if (used + kHeader + points * kPoint > capacity) {
                break;
            }
            const int32_t sub = static_cast<int32_t>(read32(m + (kind == 2u ? 0x20u : 0x14u)));
            if (sub <= 0) {
                continue;
            }

            float* h = out + used;
            for (uint32_t k = 0; k < kHeader; ++k) {
                h[k] = 0.0f;
            }
            put_bits(h + 0, r);
            { const auto owner = owners.find(r); put_bits(h + 38, owner != owners.end() ? owner->second : 0u); }
            h[1] = static_cast<float>(kind);
            h[2] = static_cast<float>(n);
            h[3] = static_cast<float>(c);
            h[4] = static_cast<float>(sub);
            h[5] = static_cast<float>(2u * ((n - 1u) * static_cast<uint32_t>(sub) + 1u));
            put_bits(h + 7, m);
            h[55] = displaces ? 1.0f : 0.0f;
            const float span = static_cast<float>(n - 1u) * static_cast<float>(sub);
            h[8] = static_cast<float>(sub);
            h[9] = span > 0.0f ? 1.0f / span : 0.0f;
            h[10] = 1.0f / static_cast<float>(sub);
            read_floats(r + 384u, h + 12, 5u);
            read_floats(r + 320u, h + 17, 16u);
            if (kind == 2u) {
                h[33] = static_cast<float>(read8(m + 0x38u));
            }
            read_floats(d + 48u, h + 34, 4u);
            const uint32_t blend = m + (kind == 2u ? 0x24u : 0x18u);
            h[42] = static_cast<float>(read32(blend));
            h[43] = static_cast<float>(read32(blend + 4u));
            h[44] = static_cast<float>(read32(blend + 8u));
            read_floats(blend + 12u, h + 45, 1u);
            h[46] = static_cast<float>(read8(blend + 16u));
            h[47] = static_cast<float>(read8(blend + 17u));
            const uint32_t block = m + (kind == 2u ? 0x50u : 0x2Cu);
            const uint32_t textures = read32(block + 100u);
            h[48] = static_cast<float>(textures > 4u ? 4u : textures);
            const uint32_t keys_begin = read32(m + 8u);
            const uint32_t keys_end = read32(m + 12u);
            uint32_t keys = plausible(keys_begin) && keys_end >= keys_begin ? (keys_end - keys_begin) / 28u : 0u;
            if (keys > 19u) {
                keys = 19u;
            }
            h[50] = static_cast<float>(keys);
            if (plausible(lighting)) {
                read_floats(lighting + 496u, h + 51, 3u);
            }
            h[54] = static_cast<float>(probes);
            for (uint32_t k = 0; k < keys; ++k) {
                read_floats(keys_begin + k * 28u, h + 56 + k * 8, 7u);
            }
            for (uint32_t t = 0; t < textures && t < 4u; ++t) {
                const uint32_t entry = block + t * 24u;
                float* o = h + 208 + t * 20;
                const uint32_t key = read32(entry + 4u);
                put_bits(o, key);
                read_floats(entry + 8u, o + 1, 1u);
                o[2] = static_cast<float>(static_cast<int8_t>(read8(entry + 20u)));
                o[3] = static_cast<float>(static_cast<int8_t>(read8(entry + 21u)));
                if (plausible(key)) {
                    bool text = true;
                    for (uint32_t ch = 0; ch < 4u && text; ++ch) {
                        const uint32_t value = read8(key + ch);
                        text = value >= 0x20u && value < 0x7Fu;
                    }
                    if (text) {
                        for (uint32_t w = 0; w < 16u; ++w) {
                            put_bits(o + 4 + w, read32(key + w * 4u));
                        }
                    }
                }
            }

            // The stream: the ring unrolled from its head, with the points either side.
            const uint32_t ring = read32(d + 8u);
            const uint32_t head = read32(d + 20u);
            const uint32_t mask = read32(d + 32u);
            float* p = h + kHeader;
            const auto point = [&](uint32_t i, float* to) {
                read_floats(ring + ((head + i) & mask) * 32u, to, kPoint);
            };
            for (uint32_t i = 0; i < c; ++i) {
                point(i, p + (1u + i) * kPoint);
            }
            const float* first = p + kPoint;
            const float* second = p + 2u * kPoint;
            const float* last = p + c * kPoint;
            const float* before_last = p + (c - 1u) * kPoint;
            float* pre = p;
            float* post1 = p + (c + 1u) * kPoint;
            float* post2 = p + (c + 2u) * kPoint;
            bool closed = false;
            if (kind == 2u && c > 2u) {
                closed = true;
                for (uint32_t i = 0; i < 3u && closed; ++i) {
                    closed = std::fabs(first[i] - last[i]) < 0.003f;
                }
            }
            if (closed) {
                // A closed loop wraps: the neighbours are the ring's own (0x82179CB0..D38).
                std::memcpy(pre, p + (c - 1u) * kPoint, kPoint * sizeof(float));
                std::memcpy(post1, second, kPoint * sizeof(float));
                std::memcpy(post2, p + 3u * kPoint, kPoint * sizeof(float));
            } else {
                // Extrapolated: a trail its first float4 (its second the end point's), a double
                // trail its first seven floats (its eighth the end point's).
                const uint32_t lanes = kind == 2u ? 4u : 7u;
                for (uint32_t i = 0; i < kPoint; ++i) {
                    if (i < lanes) {
                        pre[i] = 2.0f * first[i] - second[i];
                        post1[i] = 2.0f * last[i] - before_last[i];
                        post2[i] = 3.0f * last[i] - 2.0f * before_last[i];
                    } else {
                        pre[i] = first[i];
                        post1[i] = last[i];
                        post2[i] = last[i];
                    }
                }
            }
            h[6] = closed ? 1.0f : 0.0f;
            used += kHeader + points * kPoint;
            ++groups;
        }
    }
    out[0] = static_cast<float>(groups);
    out[1] = static_cast<float>(used);
    return groups == 0u ? 0u : used;
}

/// THE DECALS the guest stated (`sub_82A40610`, taken by Fable2/src/vfs/src/hooks/decals.cpp), oldest first, 27
/// words each: [0..14] as core/payload_bridge.h states them (serial, owner twin, texture name
/// hash, position, direction, half sizes, life, flag), [15..26] the owner twin's world matrix as
/// `sub_8220B490` loads it from twin+336: three rows (axis.xyz, translation) of floats, zeros when
/// the decal has no owner. `out_serial` moves whenever the set changed. Returns the count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDecals(uint32_t* out, uint32_t capacity, uint32_t* out_serial) {
    constexpr uint32_t kOut = kDecalWords + 12u;
    uint32_t stated[50u * kDecalWords];
    const uint32_t count = refii::vfs::StatedDecals(stated, 50u * kDecalWords, out_serial);
    if (out == nullptr) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    uint32_t written = 0u;
    for (uint32_t i = 0u; i < count && (written + 1u) * kOut <= capacity; ++i) {
        uint32_t* row = out + written * kOut;
        std::memcpy(row, stated + i * kDecalWords, kDecalWords * sizeof(uint32_t));
        const uint32_t owner = row[1];
        for (uint32_t k = 0u; k < 12u; ++k) {
            row[kDecalWords + k] =
                (base != nullptr && owner >= 0x10000u && owner < 0xC0000000u) ? GuestWordAt(base, owner + 336u + k * 4u) : 0u;
        }
        ++written;
    }
    return written;
}

/// What the title's RAIN (`0x822803D0`) and SNOW (`0x821BDE68`) draws read beyond the theme block
/// and the wind, which the frame export already carries. Out, floats:
///   [0] 1 when the light-zone stack (`0x834A4D68` begin, `0x834A4D6C` end) has a top zone Z
///       [1..3] its colour Z+0xD0 — the rain's diffuse and specular are 0.25 x it, the snow's
///       colour 0.75 + 0.25 x it (0x82281474, 0x821BEE20); without one, 0.1 and 1.
///   [4] the clock the snow animates by, `(float)dbl_83496E18` (0x821BEC30, binding 397)
///   [5] n, the scene's lights the rain may take: `*(scene+0xE64)`, a vector at +8 / +0xC of
///       32-byte entries whose light is at +0x10, kept when L+0xC0 == 1 (0x8228074C..0x822807B4).
///       The draw keeps those within 20 of the camera and drops the farthest down to three;
///       which camera is the host's, so the cut is the caller's.
///   [8..71] the snow's spell pushes, g_PushVector (binding 0x17A, 16 registers, 0x821BEC78): the
///       weather primitive's (scene list type 25, vtable 0x82004BC8) 8 slots at +0x50, each
///       (start xyz, clock + t1) and (end xyz, clock + t2), as message 24 states them
///       (`sub_82A77F60`, from the game's `sub_8260E100`: strikes, shots, spells)
///   [72..79] g_PushVectorOverDist (0x17B, 0x821BEC8C): 1 / d per slot at +0x150
///   [80 + 8i ..] position L+0x120 xyz, radius L+0x140, colour L+0xD0 rgb, 0.
/// Returns the floats written, 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicWeather(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 80u;
    constexpr uint32_t kLight = 8u;
    if (out == nullptr || capacity < kHeader) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    for (uint32_t i = 0; i < kHeader; ++i) {
        out[i] = 0.0f;
    }

    const uint32_t zones_begin = GuestWordAt(base, refii::guest::Address(0x834A4D68u));
    const uint32_t zones_end = GuestWordAt(base, refii::guest::Address(0x834A4D6Cu));
    if (plausible(zones_end) && zones_end > zones_begin && zones_end - zones_begin < 0x10000u) {
        const uint32_t zone = GuestWordAt(base, zones_end - 4u);
        if (plausible(zone)) {
            out[0] = 1.0f;
            read_floats(zone + 0xD0u, out + 1, 3u);
        }
    }

    const uint32_t clock_at = refii::guest::Address(0x83496E18u);
    const uint64_t clock_bits = (static_cast<uint64_t>(GuestWordAt(base, clock_at)) << 32) |
        GuestWordAt(base, clock_at + 4u);
    double clock = 0.0;
    std::memcpy(&clock, &clock_bits, sizeof(clock));
    out[4] = std::isfinite(clock) ? static_cast<float>(clock) : 0.0f;

    // The snow's push slots: the weather primitive in the scene's typed list (type 25).
    for (uint32_t k = 0; k < 8u; ++k) {
        out[72u + k] = 0.0f;
    }
    {
        const uint32_t list = scene + 3412u;
        const uint32_t nodes_begin = GuestWordAt(base, list + 4u);
        const uint32_t nodes_end = GuestWordAt(base, list + 8u);
        if (plausible(nodes_begin) && nodes_end >= nodes_begin && nodes_end - nodes_begin <= 48u * 24u) {
            for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
                if (GuestWordAt(base, node) != 25u) {
                    continue;
                }
                const uint32_t objects_begin = GuestWordAt(base, node + 12u);
                const uint32_t objects_end = GuestWordAt(base, node + 16u);
                const uint32_t weather = plausible(objects_begin) && objects_end > objects_begin
                    ? GuestWordAt(base, objects_begin) : 0u;
                if (plausible(weather)) {
                    read_floats(weather + 0x50u, out + 8, 64u);
                    read_floats(weather + 0x150u, out + 72, 8u);
                }
                break;
            }
        }
    }

    uint32_t lights = 0u;
    const uint32_t list = GuestWordAt(base, scene + 0xE64u);
    if (plausible(list)) {
        const uint32_t begin = GuestWordAt(base, list + 8u);
        const uint32_t end = GuestWordAt(base, list + 0xCu);
        if (plausible(begin) && end >= begin && end - begin < 0x40000u) {
            for (uint32_t at = begin; at + 32u <= end; at += 32u) {
                if (kHeader + (lights + 1u) * kLight > capacity) {
                    break;
                }
                const uint32_t light = GuestWordAt(base, at + 0x10u);
                if (!plausible(light) || GuestWordAt(base, light + 0xC0u) != 1u) {
                    continue;
                }
                float* row = out + kHeader + lights * kLight;
                read_floats(light + 0x120u, row, 3u);
                read_floats(light + 0x140u, row + 3, 1u);
                read_floats(light + 0xD0u, row + 4, 3u);
                row[7] = 0.0f;
                ++lights;
            }
        }
    }
    out[5] = static_cast<float>(lights);
    return kHeader + lights * kLight;
}

/// The FX MESH PARTICLES the guest's effects state (Fable2/src/vfs/src/hooks/fx_meshes.cpp): per descriptor its
/// render twin (descriptor +4 -> `dword_8349F7AC[handle]`) as the effect's mesh renderer
/// (`sub_821CEA80` kind 4) keeps it with its messages. Per mesh 24 floats:
///   [0] descriptor (bits) [1] model hash (descriptor +128) [2] flags (descriptor +20: 0x800
///   displacement) [3..14] the world rows twin+336 (3 x vec4, translation in w, `sub_8220B6A8`)
///   [15..18] the tint the renderer states to the handle (model command 17, vtable 0x820B6EFC,
///   +16..+28; the twin binds it as PS c49) [19] the alpha it states (model command 16, vtable
///   0x820B6EF0, +4: particle alpha x effect alpha) — both taken at `Render_ApplyCommandToHandle`,
///   1.0 until stated [20..22] the distance fade twin+284 (full within), +288 (slope), +292 (gone
///   beyond) [23] twin type (+4).
/// out[0] = meshes; returns the floats written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFxMeshes(float* out, uint32_t capacity) {
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const uint32_t table = GuestWordAt(base, refii::guest::Address(0x8349F7ACu));
    if (!plausible(table)) {
        return 1u;
    }
    uint32_t descriptors[1024];
    const uint32_t stated = refii::vfs::StatedFxMeshes(descriptors, 1024u);
    uint32_t used = 1u;
    uint32_t meshes = 0u;
    for (uint32_t i = 0; i < stated && used + 24u <= capacity; ++i) {
        const uint32_t descriptor = descriptors[i];
        const uint32_t handle = GuestWordAt(base, descriptor + 4u);
        if (handle == 0u || handle == 0xFFFFFFFFu || handle > 0x100000u) {
            continue;
        }
        const uint32_t twin = GuestWordAt(base, table + handle * 4u);
        if (!plausible(twin)) {
            continue;
        }
        float* m = out + used;
        std::memcpy(m + 0, &descriptor, sizeof(float));
        const uint32_t hash = GuestWordAt(base, descriptor + 128u);
        std::memcpy(m + 1, &hash, sizeof(float));
        m[2] = static_cast<float>(GuestWordAt(base, descriptor + 20u));
        read_floats(twin + 336u, m + 3, 12u);
        refii::vfs::StatedFxMeshLook(descriptor, handle, m + 15);
        read_floats(twin + 284u, m + 20, 3u);
        m[23] = static_cast<float>(GuestWordAt(base, twin + 4u));
        used += 24u;
        ++meshes;
    }
    out[0] = static_cast<float>(meshes);
    return used;
}

/// Which heroes are drunk enough for the drunk warp, the warp builder's own test (`sub_822B62F0`:
/// component 157 +12 at or past +36): bit 0 hero one, bit 1 hero two. On split screen the warp
/// draws in each drunk player's own view (the guest builds player one's effect, the pump player
/// two's, from the same record).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDrunkPlayers(void) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    uint32_t bits = 0u;
    for (uint32_t player = 1u; player <= 2u; ++player) {
        const uint32_t hero = PlayerEntityForKey(base, player);
        const uint32_t drink = hero >= 0x10000u && hero < 0xC0000000u ? EntityComponent(base, hero, 157u) : 0u;
        if (drink < 0x10000u || drink >= 0xC0000000u) {
            continue;
        }
        const uint32_t level = GuestWordAt(base, drink + 12u);
        const uint32_t limit = GuestWordAt(base, drink + 36u);
        float a = 0.0f, b = 0.0f;
        std::memcpy(&a, &level, sizeof(a));
        std::memcpy(&b, &limit, sizeof(b));
        if (a >= b) {
            bits |= 1u << (player - 1u);
        }
    }
    return bits;
}

/// The render scene's DISPLACEMENT QUADS (typed list type 29, ctor `sub_82A4B9A8`, vtable
/// 0x820F72F0): screen-space quads the displacement renderer draws with VS 176 / PS 177 (a textured
/// quad) — as shipped only the drunk warp (`sub_822B62F0`: "Art\GUI\HUD\FXT_Fullscreen_Drunk",
/// full screen, while the drunkenness stat is past its threshold). Per quad 16 floats, from the
/// fields message 35 sets (`sub_82A81FB0`): [0..3] x0 y0 x1 y1 pixels (+76..+88), [4] z (+92),
/// [5] rotation (+96), [6..13] the corners' uv (+100..+128, in the draw's v0..v3 order), [14..15]
/// the guest's screen size (`0x834A5FE8` / +4, what the draw's g_ScreenToViewportTransform divides
/// by). out[0] = quads; returns the floats written, 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDisplacementQuads(float* out, uint32_t capacity) {
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const uint32_t screen = refii::guest::Address(0x834A5FE8u);
    const float width = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, screen)));
    const float height = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, screen + 4u)));
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = GuestWordAt(base, list + 4u);
    const uint32_t nodes_end = GuestWordAt(base, list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 1u;
    }
    uint32_t quads = 0u;
    uint32_t used = 1u;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        if (GuestWordAt(base, node) != 29u) {
            continue;
        }
        const uint32_t objects_begin = GuestWordAt(base, node + 12u);
        const uint32_t objects_end = GuestWordAt(base, node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin || objects_end - objects_begin > 4096u) {
            continue;
        }
        for (uint32_t at = objects_begin; at + 4u <= objects_end && used + 16u <= capacity; at += 4u) {
            const uint32_t quad = GuestWordAt(base, at);
            if (!plausible(quad)) {
                continue;
            }
            float* q = out + used;
            for (uint32_t k = 0; k < 6u; ++k) {
                const uint32_t bits = GuestWordAt(base, quad + 76u + k * 4u);
                std::memcpy(q + k, &bits, sizeof(float));
            }
            for (uint32_t k = 0; k < 8u; ++k) {
                const uint32_t bits = GuestWordAt(base, quad + 100u + k * 4u);
                std::memcpy(q + 6u + k, &bits, sizeof(float));
            }
            q[14] = width;
            q[15] = height;
            used += 16u;
            ++quads;
        }
    }
    out[0] = static_cast<float>(quads);
    return used;
}

/// Fills 4 slots x 6 words: object, capacity, write cursor (+32), write consumed (+36),
/// reader consumed (+64), last read (+24). A zero object means the slot is empty.
REFII_HOST_ABI_EXPORT void RefiiHostGetSceneStreams(uint32_t* out) {
    if (out == nullptr) {
        return;
    }

    for (int i = 0; i < 24; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    for (uint32_t slot = 0; slot < 4u; ++slot) {
        const uint32_t object = GuestWordAt(base, refii::guest::Address(0x8349F9B0u) + slot * 4u);
        if (object < 0x10000u || object >= 0xC0000000u) {
            continue;
        }

        uint32_t* row = out + slot * 6u;
        row[0] = object;
        row[1] = read32(object + 8u);
        row[2] = read32(object + 32u);
        row[3] = read32(object + 36u);
        row[4] = read32(object + 64u);
        row[5] = read32(object + 24u);
    }
}

/// The 15 Hz pacer's own arithmetic — whether `rex_lhGameLoop_Tick` is ticking, and why. From
/// outside, a tick thread parked in a deadlock and one healthily yielding are the same silence.
/// From `sub_82276D10` (loop head 0x82276E78):
///
///     sub_8221EA28(&now);                                  ; 0x8221EA28 is `mftb`
///     seconds = ((double)now - dbl_834970A0) / dbl_834970A8
///     target  = (int)((seconds - resyncTime) * 15.0 + resyncTick)     ; 0x82276EF0
///     *(double*)(game + 0x110) = (double)(target - resyncTick) * (1/15.0) + resyncTime
///     if (target - lastTick <= 0) SwitchToThread();         ; 0x82276F1C -> 0x82277344
///     else <the whole tick body>
///
/// The spin at LR 0x82277348 is the ORDINARY fixed-step yield ("no tick is due yet"). What
/// separates a paced thread from a stuck one is whether `target` is still climbing, and
/// `game + 0x110` is that number in seconds, rewritten EVERY iteration, spin arm included.
///
/// `resyncTime` and `resyncTick` live in registers and cannot be read from here, so the two
/// globals the seconds derive from are reported instead. A zero divisor at `dbl_834970A8`
/// makes `seconds` infinite and `fctiwz` saturate; a NaN parks the comparison on the spin arm.
///
/// `0x83497124` is a SLOT INDEX, not a count: `Render_FrameCompletionCallback` (0x82B90160)
/// publishes it with `extrwi r3, r4, 1, 26` / `xori r10, r3, 1`, one bit, so it is 0 or 1. The
/// completed count is a field of the record it names,
/// `*(u32*)(0x83491D98 + 24*index + 0x10)`.
///
/// Read-only, and every value is one aligned load. <paramref name="words"/> takes 8:
///
///     [0] the game-loop object, or 0 when there is not one
///     [1] game+0x104 — the in-game simulation gate
///     [2] game+0x105 — the world-ready gate
///     [3] presented   (0x83497120)
///     [4] frame-sync slot INDEX (0x83497124), 0 or 1
///     [5] completed   (records + 24*index + 0x10)
///     [6] that slot's reader lock (+0x14)
///     [7] the app-object quit byte the loop's own continuation predicate reads
///
/// <paramref name="values"/> takes 3: sim time (game+0x110), then the timebase epoch and
/// ticks-per-second the seconds are derived from.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGameLoopPacer(uint32_t* words, uint32_t word_capacity,
                                                        double* values, uint32_t value_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto word = [&](uint32_t index, uint32_t value) {
        if (words != nullptr && index < word_capacity) {
            words[index] = value;
        }
    };
    const auto real = [&](uint32_t index, double value) {
        if (values != nullptr && index < value_capacity) {
            values[index] = value;
        }
    };
    // The guest stores a double high word first, the same way its `stfd` leaves it.
    const auto read_double = [base](uint32_t at) {
        const uint64_t bits = (static_cast<uint64_t>(GuestWordAt(base, at)) << 32) |
                              GuestWordAt(base, at + 4u);
        double out = 0.0;
        std::memcpy(&out, &bits, sizeof(out));
        return out;
    };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    for (uint32_t i = 0; i < 11u; ++i) word(i, 0u);
    for (uint32_t i = 0; i < 5u; ++i) real(i, 0.0);

    real(1, read_double(refii::guest::Address(0x834970A0u)));
    real(2, read_double(refii::guest::Address(0x834970A8u)));

    // The GUI manager's update gate and the presentation clock. `sub_821A1140` runs the
    // gameplan update — advancing every timeline and draining every presentation's event queue
    // — only while `byte_83319409 && *(u8*)(GUI+0x65)` (registered players) and the game state
    // is not 7; it then sets `GUI+0x64` (the DoString window). A presentation's root time block
    // is at `pres+852` (`{+0 time ms, +4 duration, +8 offset, +12 pauseTime, +16 flags}`); `+0`
    // is absolute milliseconds and advances ~1000 per second while the manager updates.
    {
        // The enable byte is a global: read it whether or not the in-game manager exists, or a
        // front-end sample reports "update off" for a byte that is 1 from image load.
        word(9, GuestByteAt(base, refii::guest::Address(0x83319409u)));
        const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
        if (plausible(gui)) {
            word(8, GuestByteAt(base, gui + 0x65u));
            word(10, GuestByteAt(base, gui + 0x64u));

            const uint32_t hud = GuestWordAt(base, gui + 32u);
            const uint32_t hud_pres = plausible(hud) ? GuestWordAt(base, hud + 4u) : 0u;
            if (plausible(hud_pres)) {
                real(3, static_cast<double>(GuestWordAt(base, hud_pres + 852u)));
            }

            const uint32_t manager = GuestWordAt(base, gui + 56u);
            const uint32_t screen_pres = plausible(manager) ? GuestWordAt(base, manager + refii::guest::ScreenManagerField(156u)) : 0u;
            if (plausible(screen_pres)) {
                real(4, static_cast<double>(GuestWordAt(base, screen_pres + 852u)));
            }
        }
    }

    word(3, GuestWordAt(base, refii::guest::Address(0x83497120u)));

    // The index first, then the record it names — never a fixed address: the callback
    // ping-pongs the slot on every completion.
    const uint32_t index = GuestWordAt(base, refii::guest::Address(0x83497124u));
    word(4, index);
    if (index < 2u) {
        const uint32_t record = refii::guest::Address(0x83491D98u) + index * 24u;
        word(5, GuestWordAt(base, record + 0x10u));
        word(6, GuestWordAt(base, record + 0x14u));
    }

    // The loop's continuation predicate is `app->vtbl[0x10]()` == `*(u8*)(app + 44) == 0`
    // (`sub_82185868`). Non-zero means the loop was asked to end, not that it is stuck.
    const uint32_t app = GuestWordAt(base, refii::guest::Address(0x8349E9ECu));
    if (plausible(app)) {
        word(7, GuestByteAt(base, app + 44u));
    }

    const uint32_t game_root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) {
        return 0;
    }
    const uint32_t game = GuestWordAt(base, game_root + 12u);
    if (!plausible(game)) {
        return 0;
    }

    word(0, game);
    word(1, GuestByteAt(base, game + refii::guest::GameField(0x104u)));
    word(2, GuestByteAt(base, game + refii::guest::GameField(0x105u)));
    real(0, read_double(game + 0x110u));

    return game;
}

/// One guest LHString global to a NUL-terminated ASCII buffer.
///
/// The global holds a REP POINTER, not characters — `sub_821F0240` copies `*a2` and then
/// bumps a refcount at `Rep+12` (0x821F02A4 / 0x821F02B4). Reading the Rep as if it were the
/// text is what made this return empty and skip the world build silently.
///
/// THE REP LAYOUT, decompiled rather than guessed. `sub_82208778` is the whole definition —
/// it is the only thing that ever fills one:
///
///   v7 = alloc((len + 4) & ~3);   *a1 = v7;      // +0x00 = the CHARACTER BUFFER
///   a1[1] = len;                                 // +0x04 = length
///   a1[2] = (2 * ((len + 4) & ~3)) | (a1[2] & 1);// +0x08 = capacity<<1 | flag
///   *(BYTE *)(v7 + len) = 0;                     // NUL, so it is narrow, not UTF-16
///   XMemCpy(*a1, a2, len);                       // one byte per character
///
/// Read the layout, do not probe for it. A heuristic that tries candidate pointers against
/// two encodings and takes whichever comes out printable does not fail loudly when it is
/// wrong — it returns plausible rubbish, and one global read as "TravelRS_Coach", "P ",
/// "Albion" and "Mh" across four consecutive runs.
static bool read_guest_name(const uint8_t* base, uint32_t global, char* out,
                            uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return false;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t rep = read32(global);
    if (rep < 0x10000u || rep >= 0xC0000000u) {
        return false;
    }

    const uint32_t chars = read32(rep);
    const uint32_t length = read32(rep + 4u);
    if (chars < 0x10000u || chars >= 0xC0000000u || length == 0u || length >= capacity) {
        out[0] = '\0';
        return false;
    }

    for (uint32_t i = 0; i < length; ++i) {
        const char c = static_cast<char>(GuestByteAt(base, chars + i));

        // A name here is short and printable. Anything else means the Rep was freed or torn
        // under us, and half a name is worse than none.
        if (c < 0x20 || c > 0x7E) {
            out[0] = '\0';
            return false;
        }

        out[i] = c;
    }

    out[length] = '\0';
    return true;
}

/// Where the guest is going. `sub_82379628` — the sole new-game entry — writes these before
/// it latches the mode change, into four CONSECUTIVE globals:
///
///     g_StartWorldName    = "Albion"                 0x83497584
///     g_StartLevelName    = "BWSSlums"               0x83497588
///     g_StartScenarioName = "DefaultScenario"        0x8349758C   (0x8237972C)
///     g_StartEntranceName = "QC010_ChildhoodStart"   0x83497590
///
/// Fills three NUL-terminated buffers. Returns 1 when the level name came out non-empty —
/// that is the one a caller cannot proceed without. The scenario has its own entry point
/// because adding a parameter here would break the ABI for a value most callers do not want.
REFII_HOST_ABI_EXPORT int RefiiHostGetStartLevel(char* world, char* level, char* entrance, uint32_t capacity) {
    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    read_guest_name(base, refii::guest::Address(0x83497584u), world, capacity);
    read_guest_name(base, refii::guest::Address(0x83497590u), entrance, capacity);
    return read_guest_name(base, refii::guest::Address(0x83497588u), level, capacity) ? 1 : 0;
}

/// The world manager, from the one fixed global that reaches it.
///
/// Six hops, and every one of them was quoted out of `sub_823BBA70` (0x823BBA8C onward), which
/// emits the whole chain in seven consecutive instructions. Two other call sites — the savegame
/// loader at 0x82447B44 and the Lua registrar at 0x82458FFC — emit the identical form, which is
/// what a canonical accessor looks like when it has been inlined everywhere.
///
/// NOT CACHED across a level load: the chain is rebuilt, so a stale pointer here is a pointer
/// into a freed object.
static uint32_t world_manager(const uint8_t* base) {
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        if (at < 0x10000u || at >= 0xC0000000u) {
            return 0;
        }
        return u32(at + offset);
    };

    uint32_t at = u32(refii::guest::Address(0x83496AB8u));
    at = step(at, 0x0Cu);
    at = step(at, 0x58u);
    at = step(at, 0x04u);
    at = step(at, 0x08u);
    at = step(at, 0x04u);
    return (at >= 0x10000u && at < 0xC0000000u) ? at : 0;
}

/// WHERE THE GUEST ACTUALLY IS — as opposed to where a New Game asked it to go.
///
/// `RefiiHostGetStartLevel` reads `g_StartWorldName`/`g_StartLevelName` (0x83497584/88), and a
/// host that polls those to notice a level change is reading the wrong thing. Every writer of
/// those globals is a GAME-START path: the CRT static initialisers ("TestWorld"/"TW_Level1"),
/// the `SetInitialWorldName`/`SetInitialLevelName` Lua natives, the new-game entry
/// `sub_82379628`, `Frontend_SetStartGameTarget`, the dev override, the network-join handler
/// and the debug level-select menu.
///
/// NOT ONE TRANSITION PATH TOUCHES THEM. Doors and teleporters (`sub_8283C7F8`), fast travel
/// (`sub_82519B08`), the `World.LoadLevel` Lua native (`sub_82350D20`) and a level reload
/// (`sub_8231C988`) all reach `World_RequestLevelChange` (0x82350B20), which puts the
/// destination on the COMMAND OBJECT at cmd+0x14/+0x18/+0x1C and never in a global. So polling
/// the start globals notices a new game and nothing else — and it appears to work in a dev
/// build only because the debug menu happens to rewrite them before it calls `Debug.LoadLevel`.
///
/// This reads the standing World and Level instead, which is mechanism-independent: new game,
/// load-save, door, teleporter, fast travel and network join all land here through the single
/// `World_LoadLevel_Execute` (0x8231C608) -> Level ctor (0x82354228) path.
///
/// OFFSETS, EACH AGAINST ITS OWN BASE, verified in the decompile rather than taken on report:
///
///     world = world_manager(base)   // despite the name this IS the World: the helper's last
///                                   // two hops are *(mgr+8) then +4, which is exactly the
///                                   // `*(*(a1+8)+4)` that 0x82350B64 dereferences
///       world + 0x14  -> Rep, world name    (written by `sub_822651A0(a1+20, a2)` @0x8231C664)
///       world + 0x24  -> holder; *(holder+4) = Level
///     level + 0xAC -> Rep, level name       (`sub_821F0240(a1+172, a3)` @0x823542E0)
///     level + 0xB0 -> Rep, entrance name    (ctor seeds it "PlayerStart" @0x823542F4; state 5
///                                            of CmdLevelChange_Update overwrites it)
///     level + 0xB4 =  u32 key               (`*(a1+180) = a2`, and the caller passes
///                                            `World_ComputeWorldLevelFnv1Key(a1,a2,a3)`)
///
/// THE KEY IS THE THING TO POLL. It is one aligned dword that changes on exactly the
/// transitions that reload a level, so a host can compare it frame to frame without parsing a
/// string or caring which mechanism moved the player. The names are for humans and for
/// resolving the region; the key is the edge.
///
/// `out_key` receives it. Returns 1 when a level is standing, 0 when none is — which is the
/// ordinary state before the first load and during the window when the old Level has been
/// released and the new one not yet constructed.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentLevel(char* world, char* level, char* entrance, uint32_t capacity,
                                  uint32_t* out_key) {
    if (out_key != nullptr) {
        *out_key = 0u;
    }

    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t world_object = world_manager(base);
    if (world_object == 0) {
        return 0;
    }

    const uint32_t holder = u32(world_object + 0x24u);
    if (holder < 0x10000u || holder >= 0xC0000000u) {
        return 0;  // no level standing
    }

    const uint32_t level_object = u32(holder + 4u);
    if (level_object < 0x10000u || level_object >= 0xC0000000u) {
        return 0;
    }

    if (out_key != nullptr) {
        *out_key = u32(level_object + 0xB4u);
    }

    read_guest_name(base, world_object + 0x14u, world, capacity);
    read_guest_name(base, level_object + 0xB0u, entrance, capacity);

    // The LEVEL name is the one a caller cannot proceed without, so it decides the result.
    return read_guest_name(base, level_object + 0xACu, level, capacity) ? 1 : 0;
}

/// The scenario the guest is ACTUALLY in, for the level it currently has loaded.
///
/// This is the live answer, where RefiiHostGetStartScenario is only the value a new game began
/// with. `Layers.ActivateScenario` rewrites it and reloads the level, so a quest advancing the
/// story changes which map and which placements the level has.
///
/// The lookup is the guest's own: the level caches its FNV-1 key at level+180, and the scenario
/// map is a vector of {u32 key, LHString name} at worldMgr+88..+92. THE SORT IS LAZY — worldMgr
/// +101 is a dirty flag and `sub_82321228` only sorts when it is set, so a host polling between
/// an insert and the flush must scan linearly or it will silently miss the entry it wants.
REFII_HOST_ABI_EXPORT int RefiiHostGetActiveScenario(char* scenario, uint32_t capacity) {
    if (scenario == nullptr || capacity == 0) {
        return 0;
    }

    scenario[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t holder = u32(wm + 36u);
    if (holder < 0x10000u || holder >= 0xC0000000u) {
        return 0;  // no level loaded
    }

    const uint32_t level = u32(holder + 4u);
    if (level < 0x10000u || level >= 0xC0000000u) {
        return 0;
    }

    const uint32_t key = u32(level + 180u);
    const uint32_t first = u32(wm + 88u);
    const uint32_t last = u32(wm + 92u);
    if (first < 0x10000u || last < first || ((last - first) % 8u) != 0u) {
        return 0;
    }

    // Linear either way. The vector is at most a few dozen entries, and a binary search that is
    // wrong when the dirty flag is set is worse than a scan that is always right.
    for (uint32_t at = first; at + 8u <= last; at += 8u) {
        if (u32(at) != key) {
            continue;
        }

        return read_guest_name(base, at + 4u, scenario, capacity) ? 1 : 0;
    }

    return 0;
}

/// The FNV-1 hashes of the layers the guest currently has active.
///
/// Layers gate ENTITY INSTANTIATION — the `.engine_level` marks elements AlwaysOn or
/// InitiallyOn and `Layers.ActivateLayer` turns the rest on — so a host that places everything
/// it finds will place things the guest has not.
///
/// The container is an MSVC `std::list<uint32_t>`, which the overflow guard gives away by
/// raising "list<T> too long" (`sub_82322878`). worldMgr+60 is the _Myproxy and reads as a
/// perfectly plausible pointer; the head is +64 and the size is +68. Nodes are 12 bytes,
/// {_Next, _Prev, u32 hash}.
///
/// There is a SECOND identically shaped list at worldMgr+72; it is not this one and its meaning
/// is unproven.
///
/// Returns how many hashes were written. Hashes are FNV-1 over the layer name's EXACT bytes —
/// no lowercasing and no separator normalisation, unlike the world+level key.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveLayers(uint32_t* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t head = u32(wm + 64u);
    if (head < 0x10000u || head >= 0xC0000000u) {
        return 0;
    }

    const uint32_t size = u32(wm + 68u);
    if (size > 4096u) {
        return 0;  // not a list we recognise
    }

    uint32_t written = 0;
    uint32_t node = u32(head);
    for (uint32_t guard = 0; guard <= size + 8u; ++guard) {
        if (node == head || node < 0x10000u || node >= 0xC0000000u) {
            break;
        }

        if (out != nullptr && written < capacity) {
            out[written] = u32(node + 8u);
        }

        ++written;
        node = u32(node);
    }

    return written;
}

/// THE SCENARIO, which is what actually picks the map.
///
/// `Level_BuildDataWorldsFilePath` (0x8250EED0) builds
/// `Data\Worlds\<World>\<Level>\<Scenario>\<Scenario>.list` and the loader takes every
/// .ghf that manifest names — `Level_LoadHeightfieldGhfEntry` (0x8250ECE0) does no filtering
/// of its own. So a level with five heightfields has five scenarios, and without this value a
/// host has to guess which one; picking by name order put a childhood entrance in chapter 2's
/// Bowerstone, which reads as the wrong season because the seasons ARE the scenarios.
///
/// This returns the START scenario. The LIVE one is a world-manager map keyed by
/// FNV-1(world + level) that Lua's `Layers.ActivateScenario` writes, and it is not exposed
/// yet — so this is right for a new game and stale once a quest moves the story on.
REFII_HOST_ABI_EXPORT int RefiiHostGetStartScenario(char* scenario, uint32_t capacity) {
    if (scenario == nullptr || capacity == 0) {
        return 0;
    }

    scenario[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    return read_guest_name(base, refii::guest::Address(0x8349758Cu), scenario, capacity) ? 1 : 0;
}

/// Non-zero while the guest is IN its front end (title screen / main menu): the front-end
/// game-flow mode from its enter to its exit, as stated value 18 (frontend_stated.cpp). Not the
/// mode singleton `dword_83496AC0`, which stays set for good once that mode is built. The mode is
/// entered only after every startup logo has played and the bank mounter's handshake completed,
/// so this going true is the title saying the boot sequence is over.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFrontEndState(void) {
    return refii_bridge_stated_value_now(18u);
}

/// Every live value the in-game HUD's METERS are holding, keyed by the name each is published
/// under.
///
/// THIS IS THE HUD'S WHOLE VALUE CONTRACT, and it is one read. Unlike the front end, the HUD
/// has no script to mirror: `hud.lua` carries no layout tables, no timing constants and — over
/// all 587 shipped chunks — no reader for a single hero stat. Its `g_HUD.State` health and mana
/// numbers are Anark authoring placeholders, each written in one chunk and read in none. What
/// the HUD has instead is a binding authored in `hud.bgf` — an element carries
/// `MeterName = "PlayerHealth"` — and C++ publishes a percentage against FNV-1 of that string
/// every frame. So a host reads the published values and draws whichever elements the FILE says
/// they belong to; nothing here has to know what a name MEANS.
///
/// THE CHAIN, every hop verified in the decompile rather than taken on report:
///
///   G        = *(0x83496B08)
///   GUIMgr   = *(G + 0x20)
///   vector   = *(GUIMgr + 0x20)         ; begin at +28, end at +32, stride 4
///   registry = *(begin + 4)             ; element [1] — the METER registry
///
/// The index is the guest's own: `sub_825E8DE0` bounds-checks the vector for at least two
/// entries and then passes `*(v31 + 4)` to `sub_823FE210` when it writes PlayerHealth
/// (0x825E9074..0x825E90D0). Text and pulsers live at other indices in the same vector and are
/// deliberately NOT read here — a second index taken on inference would be an offset reaching
/// running code without a decompile behind it.
///
/// THE MAP is a plain MSVC `std::_Tree`, and `sub_82375480` (its lower_bound) states the whole
/// layout without ambiguity:
///
///     v3 = *(map + 4);              // _Myhead, the sentinel
///     v4 = v3[1];                   // head+4 = _Parent = the ROOT
///     while ( !*((_BYTE *)v4 + 21) )        // node+21 = _Isnil
///       if ( (unsigned)v4[3] >= *a3 )       // node+12 = the KEY, unsigned
///         { v3 = v4; v4 = (int *)*v4; }     // node+0  = _Left
///       else v4 = (int *)v4[2];             // node+8  = _Right
///
/// and `sub_822A9C60` returns `*(node + 16)` — node+16 is the mapped RECORD. The map object
/// itself sits at `registry+4`, so `*(registry+8)` is `_Myhead`.
///
/// A RECORD IS A VECTOR OF ELEMENTS, not one element: `record+4`/`record+8` are begin/end,
/// stride 4, and the setters walk the whole span (`sub_823FE2D0` at 0x823FE3A8 does
/// `r10 += 4; cmplw r10, *(r3+8); bne`). One name genuinely drives several — "TopBoxY" is on
/// five elements and "CornerY" on eight — so the value is per NAME and the host applies it to
/// every element the file bound to that name.
///
/// THE VALUE IS `element + 0x14`, the CURRENT percent, 0..100. Its two neighbours are what
/// prove the roles: `sub_823FE2D0` writes the clamped target to `+0x1C`, zero to `+0x0C`, and
/// then either `rate = 0` into `+0x18` for an immediate set, or
/// `(target - *(float *)(e + 0x14)) / seconds` into `+0x18` for a tween — reading `+0x14` as
/// the value it is interpolating FROM. Taking `+0x14` rather than `+0x1C` is therefore taking
/// what is on screen now instead of where it is heading, which is what a bar should draw.
///
/// Fills two parallel arrays. Returns the number of meters FOUND, which may exceed
/// `capacity` — a caller that gets a bigger number than it asked for should resize and ask
/// again rather than assume it saw everything. Pass nulls to size the buffers.
/// The GUI's typed element registries, by index into the vector at `*(GUIMgr + 0x20)`.
///
/// NOT INFERRED. `sub_827F0F00` writes each sub-registry at a fixed offset paired with its own
/// vtable constant, and every one of those vtables' slot [3] decompiles to a one-line
/// `sub_8222CED0(a1, "<name>")` returning the `.bgf` attribute it binds on. The full set is
/// [0] ParticleEffect, [1] MeterName, [2] SlideFaderName, [3] SlideFader2Name, [4] PulserName,
/// [5] BoxName, [6] TextName, [7] an inert base instance, [8] TinterName, [9] SlideToggleName,
/// [10] SlideSetName, [11] PrimDescName, [12] ImageName.
///
/// Only the two this file reads are named here — an unused constant for a registry nobody
/// walks would be a claim with no code behind it to keep it honest.
constexpr uint32_t kGuiRegistryMeter = 1u;
constexpr uint32_t kGuiRegistryPulser = 4u;
constexpr uint32_t kGuiRegistryText = 6u;

/// Walks one of the GUI's typed element registries, calling `visit(nameHash, element)` for the
/// FIRST element registered under each name.
///
/// ONE WALK FOR EVERY REGISTRY, because they are the same structure with different element
/// types behind them — `sub_827F0F00` builds all thirteen from one base whose register/lookup
/// code is shared. Writing the traversal twice is how the meter reader and the text reader
/// would drift apart on the day one of them learned something.
///
/// `index` selects the registry, and the indices are the guest's own, proven by each
/// sub-registry's vtable slot [3] returning its `.bgf` attribute name:
/// **[1] MeterName, [4] PulserName, [6] TextName, [9] SlideToggleName.**
///
/// THE FIRST ELEMENT PER NAME STANDS FOR THE NAME. One hash genuinely drives several elements
/// and every setter writes them all the same value in one loop, so reporting one per name
/// reports what the guest stated; asking per element would be asking which of several identical
/// answers was preferred.
///
/// Fills parallel `out_hashes` / `out_elements` arrays and returns the number of names found,
/// which may exceed `capacity` — a caller that gets a bigger number should resize and ask again
/// rather than assume it saw everything.
///
/// Deliberately NOT a template taking a visitor: this lives inside the file's `extern "C"`
/// block, and a template cannot have C language linkage. Handing back the pairs and letting
/// each reader pull the field it wants costs one small array and keeps the traversal in one
/// place, which is the point.
static uint32_t walk_gui_registry(const uint8_t* base, uint32_t index, uint32_t* out_hashes,
                                  uint32_t* out_elements, uint32_t capacity) {
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui)) {
        return 0u;
    }

    const uint32_t manager = read32(gui + 0x20u);
    if (!plausible(manager)) {
        return 0u;
    }

    const uint32_t vector = read32(manager + 0x20u);
    if (!plausible(vector)) {
        return 0u;
    }

    const uint32_t begin = read32(vector + 28u);
    const uint32_t end = read32(vector + 32u);
    if (!plausible(begin) || end <= begin || (end - begin) > 0x400u) {
        return 0u;
    }

    // The same bound the guest's own writers check before indexing — they `__trap()` on a
    // vector too short for the slot they want, and a host must simply decline instead.
    if (((end - begin) / 4u) <= index) {
        return 0u;
    }

    const uint32_t registry = read32(begin + (index * 4u));
    if (!plausible(registry)) {
        return 0u;
    }

    // The map object is at `registry+4`, so its `_Myhead` is at `registry+8` — which is exactly
    // what `sub_822A9C60` compares a found node against to detect `end()`.
    const uint32_t head = read32(registry + 8u);
    if (!plausible(head)) {
        return 0u;
    }

    const uint32_t root = read32(head + 4u);  // head->_Parent
    if (!plausible(root)) {
        return 0u;
    }

    // AN EXPLICIT STACK, NOT RECURSION. This runs on Godot's thread against memory another
    // thread is mutating, so a torn pointer must cost a truncated answer rather than the
    // process. The depth bound is the same reasoning: a red-black tree of N nodes is at most
    // 2*log2(N+1) deep, so 64 is unreachable for any real registry and a tree that asks for
    // more has been corrupted under us.
    constexpr uint32_t kMaxDepth = 64u;
    constexpr uint32_t kMaxNames = 512u;

    uint32_t stack[kMaxDepth];
    uint32_t depth = 0;
    uint32_t found = 0;

    stack[depth++] = root;

    while (depth > 0 && found < kMaxNames) {
        const uint32_t node = stack[--depth];
        if (!plausible(node) || GuestByteAt(base, node + 21u) != 0u) {
            continue;  // node+21 is _Isnil: the sentinel, or a pointer that cannot be a node
        }

        const uint32_t key = read32(node + 12u);
        const uint32_t record = read32(node + 16u);

        if (plausible(record)) {
            const uint32_t elements = read32(record + 4u);
            const uint32_t elements_end = read32(record + 8u);
            if (plausible(elements) && elements_end > elements) {
                const uint32_t element = read32(elements);
                if (plausible(element)) {
                    if (out_hashes != nullptr && out_elements != nullptr && found < capacity) {
                        out_hashes[found] = key;
                        out_elements[found] = element;
                    }

                    ++found;
                }
            }
        }

        if (depth + 2u <= kMaxDepth) {
            stack[depth++] = read32(node + 0u);  // _Left
            stack[depth++] = read32(node + 8u);  // _Right
        }
    }

    return found;
}

/// The FNV-1 hashes of the layers the guest has explicitly DEACTIVATED — worldMgr+72.
///
/// THIS IS THE LIST A HOST NEEDS, and it is not the one the name "active layers" suggests.
/// `sub_823662C8` reads a different container per the layer's `load` attribute:
///
///   823664d0  bne cr6, loc_82366548   AlwaysOn    -> spawn, consulting NEITHER list
///   823664e8  bl  sub_82320A68        InitiallyOn -> spawn iff ABSENT from +72
///   82366520  bl  sub_8244C428        else        -> spawn iff PRESENT in +60
///
/// `sub_82320A68` walks `W+72` and returns PRESENT — `subf/cntlzw/extrwi` then `xori r3, r8, 1`
/// at 0x82320AE4 — and the caller spawns on zero (`beq cr6, loc_82366548`, 0x823664F4). The
/// else-branch test is inline at `loc_823664F8` against r23, which `World_LoadLevel_Execute`
/// supplies as `a1 + 60` (0x8231C788).
///
/// The two are pinned by their writers touching BOTH off one base, which is what makes the
/// assignment airtight rather than a guess:
///
///   Layers.ActivateLayer   sub_8231D8F0  insert +60, erase  +72
///   Layers.DeactivateLayer sub_8231DBE0  erase  +60, insert +72
///
/// and `IsLayerActive` (`sub_8231DEA8`) tests +60 for presence.
///
/// Both lists are EMPTY on a new game: `sub_8231BF20` default-constructs each, and the only bulk
/// writer is `World_LoadLevelFromSaveHeader` (0x8231C7E4/0x8231C7F0), which the new-game path
/// through `CmdLevelChange_Update` never reaches. That is why a host must gate on THIS list —
/// gating on the activated one would hide every `InitiallyOn` layer in a fresh world.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeactivatedLayers(uint32_t* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // Mirrors RefiiHostGetActiveLayers: MSVC `std::list`, _Myproxy then _Myhead at +4 and
    // _Mysize at +8. The only difference is the container offset — +60 activated, +72 here.
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t head = u32(wm + 72u + 4u);
    if (head < 0x10000u || head >= 0xC0000000u) {
        return 0;
    }

    const uint32_t size = u32(wm + 72u + 8u);
    if (size > 4096u) {
        return 0;  // not a list we recognise
    }

    uint32_t written = 0;
    uint32_t node = u32(head);
    for (uint32_t guard = 0; guard <= size + 8u; ++guard) {
        if (node == head || node < 0x10000u || node >= 0xC0000000u) {
            break;
        }

        if (out != nullptr && written < capacity) {
            out[written] = u32(node + 8u);
        }

        ++written;
        node = u32(node);
    }

    return written;
}

/// The shell's meter and text reads, answered with nothing: Fable 2's HUD meters and texts are
/// the registry writers' own statements (`@Gui.Meter`, `@Gui.Text`, hooks/gui_registry_hooks.cpp).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudMeters(uint32_t* /*out_hashes*/, float* /*out_values*/,
                                                    uint32_t /*capacity*/) {
    return 0u;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudTexts(uint32_t* /*out_hashes*/, uint32_t /*capacity*/, char* text,
                                                   uint32_t text_capacity) {
    if (text != nullptr && text_capacity > 0) {
        text[0] = '\0';
    }
    return 0u;
}

/// PLAYER TWO'S TARGETED READOUT on split screen. The guest keeps ONE readout, for the local hero
/// (`sub_82176808` on the block at `*(*(GUI+32)+112)`, its target set from hero one's targeting
/// only, `sub_8218FC60`), so player two's is built here from the SAME reads the guest's driver
/// makes, for hero two's own focus target (component 77, `GetFocusTarget` `sub_82843C40`:
/// `*(*(comp77 + 0xC8))`):
///   raised       — the target alive (+144 & 0x40) and a citizen (+36 bit 26 or +48 bit 1);
///   gift / axis  — component 131's first pending record (0x82176c4c..0x82176cd8): `axis = rec+4`,
///                  value `(rec+8 ± 1) × ±0.5`, behind the driver's one gate, the bytes +73/+75 of
///                  `*(*(*(game+88)+4)+104)` (0x82176c10) — read as the driver reads it; the button
///                  (`sub_825DD3C8`, a call) is not stated here;
///   opinion      — the opinion component's current axis (+44 + 4·axis), from the VILLAGER'S
///                  OPINION OF PLAYER TWO (core/coop_opinion.cpp) — their base attitudes until
///                  player two has done something there;
///   marry/divorce— the guest asks hero one's family and eligibility only; not stated.
/// The title is the guest's own composition, called for this target in the pump
/// (RefiiHostGetPlayerTwoHud). Words: [0] raised, [1] target entity, [2] published entity, [3] gift on, [4] gift value bits, [5] button on, [6] slide-set index, [7] divorce on, [8] divorce value bits, [9] marry on, [10] marry value bits, [11] opinion bits; returns 12.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPlayerTwoTargetedQuery(uint32_t* out_words, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out_words == nullptr || capacity < 12u) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    for (uint32_t i = 0; i < 12u; ++i) {
        out_words[i] = 0u;
    }
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const auto word = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto f32 = [&](uint32_t at) {
        const uint32_t bits = word(at);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };
    const auto bits = [](float value) {
        uint32_t out = 0u;
        std::memcpy(&out, &value, sizeof(out));
        return out;
    };

    const uint32_t hero = PlayerEntityForKey(base, 2u);
    const uint32_t targeting = plausible(hero) ? EntityComponent(base, hero, 77u) : 0u;
    const uint32_t holder = plausible(targeting) ? word(targeting + 0xC8u) : 0u;
    const uint32_t target = plausible(holder) ? word(holder) : 0u;
    const bool alive = plausible(target) && (GuestByteAt(base, target + 144u) & 0x40u) != 0u;
    const bool citizen = alive && (((word(target + 36u) >> 26) & 1u) != 0u || ((word(target + 48u) >> 1) & 1u) != 0u);
    if (!citizen) {
        return 12u;
    }
    out_words[0] = 1u;
    out_words[1] = target;
    out_words[2] = target;

    // Gift and axis, behind the driver's gate (0x82176c10..0x82176c28).
    uint32_t axis = 0u;
    const uint32_t game = word(word(refii::guest::Address(0x83496AB8u)) + 12u);
    const uint32_t players = plausible(game) ? word(word(game + 88u) + 4u) : 0u;
    const uint32_t gate_object = plausible(players) ? word(players + 104u) : 0u;
    const bool gate = plausible(gate_object) && GuestByteAt(base, gate_object + 75u) == 0u &&
                      GuestByteAt(base, gate_object + 73u) != 0u;
    const uint32_t wants = EntityComponent(base, target, 131u);
    if (gate && plausible(wants) && GuestByteAt(base, wants + 32u) != 0u && word(wants + 20u) != 0u) {
        const uint32_t list = word(wants + 16u);
        const uint32_t node = plausible(list) ? word(list) : 0u;
        const uint32_t record = plausible(node) && node != list ? word(node + 8u) : 0u;
        if (plausible(record)) {
            axis = word(record + 4u);
            const float raw = f32(record + 8u);
            out_words[3] = 1u;
            out_words[4] = bits(axis == 1u ? (raw - 1.0f) * -0.5f : (raw + 1.0f) * 0.5f);
        }
    }
    out_words[6] = axis + 1u;

    // Opinion: the villager's opinion of player two.
    const uint32_t opinion = EntityComponent(base, target, 47u);
    if (plausible(opinion) && axis < 5u) {
        uint8_t two[REFII_OPINION_BLOCK_BYTES];
        float value = 0.0f;
        if (refii_bridge_opinion_two_load(opinion, two) != 0) {
            uint32_t raw = 0u;
            std::memcpy(&raw, two + 20u + (4u * axis), sizeof(raw));
            raw = __builtin_bswap32(raw);
            std::memcpy(&value, &raw, sizeof(value));
        } else {
            value = f32(opinion + 24u + (4u * axis));
        }
        out_words[11] = bits(axis == 1u ? (value - 1.0f) * -0.5f : (value + 1.0f) * 0.5f);
    }
    return 12u;
}

/// An entity's network id, the 64-bit UID the guest keys its entity map on (`entity+20` high,
/// `+24` low; `sub_82264488`). 0 for none.
REFII_HOST_ABI_EXPORT uint64_t RefiiHostGetEntityUid(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }
    return (static_cast<uint64_t>(GuestWordAt(base, entity + 20u)) << 32) | GuestWordAt(base, entity + 24u);
}

/// A villager's opinion of player `player` (1 or 2) by its network id: the five axes (love, fear,
/// attractiveness, morality, respect) into `out`. Player one's are the opinion component's current
/// axes (+44); player two's the set kept for them (current at block+20), or the villager's base
/// attitudes (+24) before player two has done anything there. The entity is found the guest's way,
/// in the UID map at players+136 (`sub_82264488`: {left +0, parent +4, right +8, key high +12, low
/// +16, value +20, nil byte +25}). Returns 1 when found.
REFII_HOST_ABI_EXPORT int RefiiHostGetOpinionOfUid(uint64_t uid, int player, float* out) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || out == nullptr || uid == 0u || (player != 1 && player != 2)) {
        return 0;
    }
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const auto word = [base](uint32_t at) { return GuestWordAt(base, at); };
    const uint32_t root = word(refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? word(root + 12u) : 0u;
    const uint32_t manager = plausible(game) ? word(game + 88u) : 0u;
    const uint32_t cell = plausible(manager) ? word(manager + 4u) : 0u;
    const uint32_t players = plausible(cell) ? word(cell) : 0u;
    const uint32_t head = plausible(players) ? word(players + 140u) : 0u;
    if (!plausible(head)) {
        return 0;
    }
    const auto key = [&](uint32_t node) { return (static_cast<uint64_t>(word(node + 12u)) << 32) | word(node + 16u); };
    uint32_t found = head;
    uint32_t node = word(head + 4u);
    for (uint32_t steps = 0; steps < 96u && plausible(node) && GuestByteAt(base, node + 25u) == 0u; ++steps) {
        if (key(node) >= uid) {
            found = node;
            node = word(node);
        } else {
            node = word(node + 8u);
        }
    }
    if (found == head || key(found) != uid) {
        return 0;
    }
    const uint32_t entity = word(found + 20u);
    const uint32_t opinion = plausible(entity) ? EntityComponent(base, entity, 47u) : 0u;
    if (!plausible(opinion)) {
        return 0;
    }
    uint8_t two[REFII_OPINION_BLOCK_BYTES];
    const bool kept = player == 2 && refii_bridge_opinion_two_load(opinion, two) != 0;
    for (uint32_t axis = 0; axis < 5u; ++axis) {
        uint32_t raw = 0u;
        if (kept) {
            std::memcpy(&raw, two + 20u + (4u * axis), sizeof(raw));
            raw = __builtin_bswap32(raw);
        } else {
            raw = word(opinion + (player == 1 ? 44u : 24u) + (4u * axis));
        }
        std::memcpy(&out[axis], &raw, sizeof(raw));
    }
    return 1;
}

/// A PLAYER'S PROPERTIES, as the guest's own calls stated them (core/property_list_state.cpp,
/// hooks/src/property_list_hooks.cpp): the records of their hero's CECPlayerProperties (comp69) — each
/// building's UID, BuildingName tag, flags, income per drop, value and bought-for price — with each
/// building's condition and a full repair's bill (core/property_condition.h). `player` 1 or 2, or 0 for the
/// hero whose menus are open (ResolvePlayerHero). One record per line: `uid(hex) \t name tag \t flags \t
/// income \t value \t condition \t repair bill \t bought for`, flags bit 0 marital, 1 rented, 2 dungeon,
/// 3 home, 4 tenants, 5 workers, 6 shared with the holder's partner (core/hero_partners.cpp), 7 held by that
/// partner: a property the other hero bought while partnered with this one (its co-owner is this hero) is
/// listed for both, its real owner's record. Returns the byte count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetProperties(int32_t player, char* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || out == nullptr || capacity == 0u) {
        return 0u;
    }
    const auto heap = [](uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; };
    const uint32_t hero = player == 1 || player == 2 ? PlayerEntityForKey(base, static_cast<uint32_t>(player))
                                                     : ResolvePlayerHero(base);
    const uint32_t props = heap(hero) ? EntityComponent(base, hero, 69u) : 0u;
    if (!heap(props)) {
        return 0u;
    }
    const int32_t holder = hero == PlayerEntityForKey(base, 2u) ? 2 : 1;
    const auto stated = [](uint32_t of) {
        std::vector<RefiiStatedProperty> list(refii_bridge_property_list_snapshot(of, nullptr, 0u));
        list.resize(refii_bridge_property_list_snapshot(of, list.data(), static_cast<uint32_t>(list.size())));
        return list;
    };
    std::vector<std::pair<RefiiStatedProperty, bool>> owned;
    for (const RefiiStatedProperty& property : stated(props)) {
        owned.emplace_back(property, false);
    }
    // The partner's purchases shared with this hero (Purchase, property_hooks.cpp: the co-owner is the
    // buyer's partner's identity), from the partner hero's own stated list.
    const uint64_t me = refii_bridge_hero_id(holder);
    const uint32_t other = PlayerEntityForKey(base, static_cast<uint32_t>(3 - holder));
    const uint32_t other_props = me != 0u && heap(other) && other != hero ? EntityComponent(base, other, 69u) : 0u;
    if (heap(other_props) && refii_bridge_partner_kind(holder, refii_bridge_hero_id(3 - holder)) > 0) {
        for (const RefiiStatedProperty& property : stated(other_props)) {
            if (refii_bridge_coowner(property.uid) == me) {
                owned.emplace_back(property, true);
            }
        }
    }
    std::string text;
    for (const auto& [property, partners] : owned) {
        uint32_t flags = property.flags & 0x3Fu;
        const uint64_t co_owner = refii_bridge_coowner(property.uid);
        flags |= co_owner != 0u && refii_bridge_partner_kind(holder, co_owner) > 0 ? 64u : 0u;
        flags |= partners ? (64u | 128u) : 0u;
        float condition = 1.0f;
        const std::string key = refii::property::ConditionKey(property.uid);
        refii_bridge_player_get(1, key.c_str(), key.size(), &condition);
        condition = std::clamp(condition, 0.0f, 1.0f);
        char line[512];
        std::snprintf(line, sizeof(line), "%016llX\t%s\t%u\t%d\t%d\t%.4f\t%d\t%d\n",
                      static_cast<unsigned long long>(property.uid), property.name, flags, property.income,
                      property.value, condition, refii::property::RepairCost(property.value, condition),
                      property.bought);
        text += line;
    }
    if (text.size() + 1u > capacity) {
        return 0u;
    }
    std::memcpy(out, text.c_str(), text.size() + 1u);
    return static_cast<uint32_t>(text.size());
}

/// The oldest property notice (core/payload_bridge.h): 1 with its kind (0 fell into disrepair,
/// 1 repaired, 2 not enough gold, 3 nothing to repair, 6 a purchase shared with the partner: the
/// building's UID; 4 owed gold paid on return, 5 a sale's half paid to the partner: the gold in
/// `uid`), 0 when none.
REFII_HOST_ABI_EXPORT int32_t RefiiHostTakePropertyNotice(int32_t* kind, uint64_t* uid) {
    return refii_bridge_property_notice_take(kind, uid);
}

/// THE PARTNERSHIP between the two heroes in the game, Fable 3's (core/hero_partners.cpp): 1 with
/// its kind (0 none, 1 business partners, 2 married) when both heroes are here with their
/// identities known, 0 otherwise.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetPartnership(int32_t* kind) {
    if (kind != nullptr) *kind = 0;
    if (refii_bridge_hero_id(1) == 0u || refii_bridge_hero_id(2) == 0u) {
        return 0;
    }
    if (kind != nullptr) *kind = refii_bridge_partnership();
    return 1;
}

/// The oldest household action for the co-op peer (mod_events.cpp): a joiner's estate action,
/// gift, furniture level or bed for the host to run for its hero two, or the host's kept gift for
/// the joiner. `<action>:<uid hex>:<value>`. Returns its length, 0 when none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeEstateIntent(char* out, uint32_t capacity) {
    return refii_bridge_estate_intent_take(out, capacity);
}

/// How many of the item definition `definition` (its GDB record hash, the game's FNV-1 of the
/// record's name) player `player`'s hero carries: the inventory component (13) list from its +16
/// sentinel, each node's item at +8 with the definition at +4 and the count at +12. -1 without the
/// hero.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetItemCount(int32_t player, uint32_t definition) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };
    const uint32_t hero = base != nullptr && (player == 1 || player == 2)
                              ? PlayerEntityForKey(base, static_cast<uint32_t>(player))
                              : 0u;
    const uint32_t inventory = plausible(hero) ? EntityComponent(base, hero, 13u) : 0u;
    if (!plausible(inventory)) {
        return -1;
    }
    const uint32_t sentinel = GuestWordAt(base, inventory + 16u);
    int32_t count = 0;
    uint32_t node = plausible(sentinel) ? GuestWordAt(base, sentinel) : 0u;
    for (uint32_t walked = 0; plausible(node) && node != sentinel && walked < 4096u; ++walked) {
        const uint32_t item = GuestWordAt(base, node + 8u);
        if (plausible(item) && GuestWordAt(base, item + 4u) == definition) {
            count += static_cast<int32_t>(GuestWordAt(base, item + 12u));
        }
        node = GuestWordAt(base, node);
    }
    return count;
}

/// Sets the partnership in both heroes' saved values (0 ends it, 1 business, 2 married). Returns
/// 1 when both identities are known.
REFII_HOST_ABI_EXPORT int32_t RefiiHostSetPartnership(int32_t kind) { return refii_bridge_partnership_set(kind); }

/// PLAYER TWO'S D-PAD AND TARGETED TITLE on split screen, from the guest's own builders called
/// for hero two each tick (guest_tick_pump.cpp, SamplePlayerTwoHud). `out` takes the title, then
/// the four d-pad icon paths (up, left, down, right), newline-separated; `out_target` the entity
/// the title is for (0 when none). Returns 1 when stated, 0 off split screen.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPlayerTwoHud(uint32_t* out_target, char* out, uint32_t capacity) {
    return refii_bridge_player_two_hud(out_target, out, capacity);
}

/// The shell's pulser read, answered with nothing: Fable 2's pulsers run in Godot from the
/// registry's own start and stop (`@Gui.Pulser`, hooks/gui_registry_hooks.cpp).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulserStates(uint32_t* /*out_hashes*/, float* /*out_phases*/,
                                                           uint32_t* /*out_modes*/, uint32_t /*capacity*/) {
    return 0u;
}

/// The phases alone — the shape before 223, kept for a host built against it.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulsers(uint32_t* out_hashes, float* out_phases, uint32_t capacity) {
    return RefiiHostGetHudPulserStates(out_hashes, out_phases, nullptr, capacity);
}

/// WHY THERE IS NO SLIDE OR IMAGE READER HERE, though the registries exist.
///
/// I wrote both and removed them: neither can work, and both returned a plausible number.
///
/// The SLIDE writers are WRITE-THROUGH. `sub_823FE848` (toggle) and `sub_828421F8` (fader and
/// set) look the name up, walk the record's element vector and call `sub_82426CA8` /
/// `sub_82426BA8` — storing nothing on the record. And `sub_82426BA8` does not store the index
/// on the element either; it is
///
///     controller = presentation->vt[0x24]();
///     controller->vt[0x08](controller, element, index, 1);
///
/// a virtual dispatch into the slide controller, whose concrete class is built through the GUI
/// manager array and never named in the image. `AKElement_getCurrentSlide` reads it back the
/// same way, through `vt[0x20]`. There is no address a host can read.
///
/// The IMAGE hash is real but is NOT on the registry record. `sub_8286DDC0` stores it at
/// `+512` (with `+516` normal, `+520` parallax and `+548` dirty) on the LIVE NODE — the object
/// that also carries the `Movie` string at `+564` — whereas this walker hands back the BINDING
/// record, whose own fields are the string at `+16` and the pulser phase at `+20`. Reading
/// `+512` off the binding record reads 512 bytes past a small struct.
///
/// Neither is needed. Slides and textures already cross PER ELEMENT through the ordinary
/// element walk that feeds every owned book (`Slides`, `Textures`), which is the same statement
/// arriving by a route that is actually addressable.

/// The WILL ASSIGNMENT state, for a Godot-owned spell widget.
///
/// `out` receives 14 words:
///   [0]  the CECSpellManager, 0 when the hero has none yet
///   [1..5]   the will on d-pad slot 0..4, or 0xFFFFFFFF for an empty slot
///   [6..10]  per slot, a bitmask of the wills that slot may be given
///   [11] the abilities screen's assignment-mode byte, or 0xFFFFFFFF when that screen is down
///   [12], [13] 0: the quick wheel's open state is the widget's own statement (`@Spell.Widget`)
/// Returns 1 when the manager resolved, 0 otherwise. The tail is zeroed either way.
///
/// The offering matrix is the only validation — `sub_82541F30` stores whatever byte it is
/// handed. `sub_826B4848` builds each row's list from two parallel byte planes, admitting a
/// will only when BOTH are set:
///
///     0x826b4ae4  v27 = mgr + 128 + row + 68;          // mgr + 196 + row
///     0x826b4af4  if ( *(v27 - 40) && *v27 )           // mgr + 156 + 5*s + row
///     0x826b4b14      spells[row][count++] = s;
///
/// with the loop bounds `while (v23 < 40)` stepping 8 (five rows) and `while (v26 - 2 < 8)`
/// (eight wills). The same `5*spell + row + 156/196` expression appears in the standalone
/// accessors `sub_825410C8` / `sub_82540EE8` / `sub_82541118`, from the same base.
///
/// The hero is NOT the player entity: the manager hangs off a sub-object two dereferences past
/// it. Chain from `sub_823B96E0` (0x823B970C..0x823B9730): the global at 0x83496AB8, `+12` the
/// game, `+88` then `+4`, one more load, `+108`, one more load. Read, never called.
REFII_HOST_ABI_EXPORT int RefiiHostGetSpellAssign(uint32_t* out) {
    constexpr uint32_t kWords = 14u;
    constexpr uint32_t kSlots = 5u;
    constexpr uint32_t kSpells = 8u;
    constexpr uint32_t kNone = 0xFFFFFFFFu;

    if (out != nullptr) {
        for (uint32_t i = 0; i < kWords; ++i) {
            out[i] = (i >= 1u && i <= kSlots) || i == 11u ? kNone : 0u;
        }
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr || out == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    out[11] = refii_bridge_stated_value_now(1u);

    const uint32_t hero = ResolvePlayerHero(base);
    // The callee's own two guards, so a half-built hero reports "no manager".
    if (!plausible(hero) || (read8(hero + 144u) & 0x40u) == 0u ||
        (read8(hero + 40u) & 0x01u) == 0u) {
        return 0;
    }

    // Component 56, by the guest's own two routes: the cached slot at `hero+140` first, then
    // the binary search over the sorted {id, pointer} pairs at `hero+72 .. hero+76`.
    constexpr uint32_t kSpellManagerComponent = 56u;
    uint32_t manager = 0u;

    const uint32_t cache = read32(hero + 140u);
    const uint32_t table = read32(hero + 72u);
    if (plausible(cache) && plausible(table)) {
        manager = read32(table + (static_cast<uint32_t>(read8(cache + 56u)) << 3) + 4u);
    } else if (plausible(table)) {
        const uint32_t end = read32(hero + 76u);
        if (end > table && (end - table) <= 0x2000u) {
            for (uint32_t at = table; at < end; at += 8u) {
                if (read32(at) == kSpellManagerComponent) {
                    manager = read32(at + 4u);
                    break;
                }
            }
        }
    }

    if (!plausible(manager)) {
        return 0;
    }

    out[0] = manager;

    for (uint32_t row = 0; row < kSlots; ++row) {
        const uint8_t assigned = read8(manager + 128u + row);

        // 0xFF is the guest's empty slot; anything past the will table has no icon.
        out[1u + row] = assigned < kSpells ? assigned : kNone;

        uint32_t offered = 0u;
        for (uint32_t spell = 0; spell < kSpells; ++spell) {
            const uint32_t index = (5u * spell) + row;
            if (read8(manager + 156u + index) != 0u && read8(manager + 196u + index) != 0u) {
                offered |= 1u << spell;
            }
        }

        out[6u + row] = offered;
    }

    return 1;
}

// ---------------------------------------------------------------------------
// Lua 5.1 values, read directly out of guest memory (read-only; no guest code runs).
//
//   lua_getfield (0x821E1848, Gameface copy)  `L->top += 8`: a TValue is EIGHT bytes, value
//                                             word then tag word, so lua_Number is a float.
//   luaS_newlstr (0x8227BBB8)                 TString length at +12, characters at +16.
// ---------------------------------------------------------------------------
//
// `extern "C++"` because this file's exports live inside one big `extern "C"` block and these
// helpers return `LuaValue` and `std::string`, which warn under C linkage.
extern "C++" {
namespace {
/// Vanilla Lua 5.1 type tags.
constexpr uint32_t kLuaTNil = 0u;

constexpr uint32_t kLuaTNumber = 3u;
constexpr uint32_t kLuaTString = 4u;
constexpr uint32_t kLuaTTable = 5u;

/// A TValue, split. `value` is the GC pointer, the boolean, or the float's bit pattern.
struct LuaValue {
    uint32_t tt = kLuaTNil;
    uint32_t value = 0u;
};

bool LuaPlausible(uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; }

LuaValue LuaRead(const uint8_t* base, uint32_t at) {
    LuaValue out;
    out.value = GuestWordAt(base, at);
    out.tt = GuestWordAt(base, at + 4u);
    return out;
}

float LuaNumber(const LuaValue& value) {
    float number = 0.0f;
    std::memcpy(&number, &value.value, sizeof(number));
    return number;
}

std::string LuaText(const uint8_t* base, const LuaValue& value) {
    if (value.tt != kLuaTString || !LuaPlausible(value.value)) {
        return std::string();
    }
    const uint32_t length = GuestWordAt(base, value.value + 12u);
    if (length > 1024u) {
        return std::string();
    }
    std::string out;
    out.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
        out.push_back(static_cast<char>(GuestByteAt(base, value.value + 16u + i)));
    }
    return out;
}
}  // namespace
}  // extern "C++"


REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGoldenCockInteraction(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = LuaPlausible(root) ? GuestWordAt(base, root + 0xCu) : 0;
    const uint32_t state = LuaPlausible(game) ? GuestWordAt(base, game + 0xACu) : 0;
    if (!LuaPlausible(state)) return 0;
    const LuaValue globals = LuaRead(base, state + 72u);
    if (globals.tt != kLuaTTable || !LuaPlausible(globals.value)) return 0;
    const uint32_t count = GuestWordAt(base, globals.value + 8u);
    const uint32_t nodes = GuestWordAt(base, globals.value + 20u);
    if (!LuaPlausible(nodes) || count > 65536u) return 0;
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t node = nodes + i * 20u;
        if (LuaText(base, LuaRead(base, node + 8u)) != "RefiiGoldenCockInteraction") continue;
        const LuaValue value = LuaRead(base, node);
        const float sequence = value.tt == kLuaTNumber ? LuaNumber(value) : 0.0f;
        return std::isfinite(sequence) && sequence >= 0.0f && sequence < 1000000.0f
            ? static_cast<uint32_t>(sequence) : 0u;
    }
    return 0;
}

/// THE LOADING SCREEN'S TEXT — the tip, the banner, the spinner — as the screen's own calls state
/// it (loading_screen_stated.cpp, core/loading_text_state.cpp). Fills SIX words: the tip's index, the tip count, the tip's
/// Babel tag key (the host localises it), the spinner's frame counter, and the tips / banner
/// switches; writes the level name then the overlay text as two NUL-terminated UTF-8 strings.
/// Returns 1 while a screen is stated.
REFII_HOST_ABI_EXPORT int RefiiHostGetLoadingScreenText(uint32_t* out, char* text, uint32_t capacity) {
    return refii_bridge_loading_text_read(out, text, capacity);
}

/// WHICH IN-GAME SCREEN THE GUEST HAS UP — stated by the guest, not inferred.
///
/// The front end names its two screens in dedicated GUI members; the thirty IN-GAME screens
/// do not work that way at all. They live in a screen manager hung off `GUI+56`, as an array
/// of `CPresentation*` indexed by a screen ID, with a `std::map<int, std::string>` beside it
/// mapping that ID to the `.bgf` file name.
///
/// Layout, verified in the binary rather than taken second-hand — every offset below is
/// measured from the SCREEN MANAGER, i.e. from `*(GUI + 56)`, except where it says GUI:
///
///   mgr        = *(0x83496B08 + 56)
///   mgr + 0x04 = the CURRENT screen ID; 31 means "none up"
///   mgr + 0x0C = the first of 30 `CPresentation*`, indexed by ID
///   mgr + 0xA0 = the std::map<int, std::string> of ID -> ".bgf" name
///
/// The proof for the two that matter:
///
///   sub_8236E630, the generic opener, at 0x8236eb20:
///       *(_DWORD *)(*(_DWORD *)(dword_83496B08 + 56) + 4) = a2;   // a2 IS the screen ID
///
///   sub_823DCBE0, which reads it back:
///       v3 = a1[1];                       // mgr + 4
///       if ( v3 < 30 ) return a1[v3 + 3]; // mgr + 0x0C + 4*id
///
///   sub_8281F620, the ID -> name table, at 0x8281f640..0x8281f684:
///       addi r4, r11, aPausemainmenuB   # "PauseMainMenu.bgf"
///       li   r10, 0                     # <-- the key
///       stw  r10, 0x90+var_38(r1)
///       bl   sub_82321D80               # map.insert(0, "PauseMainMenu.bgf")
///   so SCREEN ID 0 IS THE PAUSE MENU, and 1 is PhotomodeScreen.bgf.
///
/// WHY THIS IS WORTH A CALL OF ITS OWN. We already walk the presentation registry and draw
/// whatever has its root element active, which is right for the HUD and for anything layered
/// over it — but "active" cannot say which screen the player is IN. The pause menu's
/// presentation is created ONCE at game entry (`sub_823DC888` from `sub_82371788`, gated on
/// the once-only byte at GUI+102) and is never freed, so it is in the registry from the first
/// frame of play whether or not the player has ever pressed pause. This field is the guest
/// stating the answer, which beats inferring it.
///
/// Fills 4 + 30 + 2 words: the GUI object, its vtable, the screen manager, the current screen
/// ID; then the THIRTY presentation pointers indexed by screen ID; then the shared slot's
/// owning ID and its presentation.
///
/// THE THIRTY POINTERS ARE THE POINT, not padding. Knowing that no screen is up is useless on
/// its own — the host still has to know WHICH of the presentations it can see belong to this
/// array, because the HUD does not (it hangs off the HUD manager at GUI+32, not off the screen
/// manager) and must keep drawing regardless. Comparing a presentation's address against these
/// thirty is the guest's own statement of membership, and it needs no name list.
///
/// The vtable is there because `0x83496B08` has two writers and only the in-game one
/// (`0x82001610`) has a screen manager at +56.
///
/// Returns 1 when the screen manager was reached.
// ---------------------------------------------------------------------------
// PUB GAMES
//
// THE GUEST OWNS THE WHOLE LIFECYCLE; Godot only plays the game. Read from the binary rather
// than inferred, and every offset below was verified by decompiling the function that writes it:
//
//   sub_82180418  returns &dword_834C20B8            -- the manager singleton
//   sub_824679F8  StartPubGame(name, variation):     -- mgr+8 = name, mgr+12 = variation,
//                 refuses while mgr+52 is non-zero      mgr+136 = 0, then state 1
//   sub_82467E40  *(mgr + 52) = state, under the critical section at mgr+68
//   sub_82467EF8  mgr+124 = statsComponent + 0x500   -- `addi r3, r11, 0x500 ; stw r3, 0x7C(r29)`
//                 and seeds the profile's bank from the hero's real gold
//   sub_82EE8640  *(profile + 264) = gold  (0x108)   -- and sets the dirty byte at +868 (0x364)
//   sub_82EE8810  *(profile + 304) = debt  (0x130)
//
// THE MOVIE IS THE ONLY PART GODOT REPLACES. The guest's tick (sub_821987B8) case 1 picks the
// loading screen, snapshots the language, the pad, the camera and the hero's gold, seeds the
// profile's bank from it, then moves to state 2. The render-thread pump (sub_82467B08) would then
// create the GFx module and run game.swf through states 2-8; once the host claims the games,
// Fable2/src/vfs/src/hooks/pubgame_pump.cpp keeps that pump's transitions and its sound emitter and
// leaves the movie out. Godot's game plays while the guest sits in 3, and each fscommand it
// issues is handed to the branch of the guest's handler (sub_82EF2AC0) that would have taken it
// -- holdings, points, rank, sounds, volumes, and GameOver, which moves to 6 and then the settle.
//
// AND THE SETTLEMENT STAYS THE GUEST'S. sub_824684F8 credits the real gold by
// (profile gold now - mgr+96), nets the debt against mgr+100, updates the per-game statistics,
// applies PurityBalancing, gossips MiniGamePlayedGood/Bad, awards the achievement and raises
// "FinishedPubGame". Reimplementing any of that host-side would be a second, disagreeing copy of
// rules the binary already states.
//
// THE NAME IS TWO DEREFERENCES FROM mgr+8, not one. sub_82275438 allocates a 16-byte string
// object and stores the POINTER at the destination, so mgr+8 holds `object*` and `object+0` holds
// the `char*`. Reading one level in gets a plausible-looking pointer that is not text.

namespace {
constexpr refii::guest::Addr kPubGameManager{0x834C20B8u};


constexpr uint32_t kPubGameNameOffset = 8u;
constexpr uint32_t kPubGameVariationOffset = 12u;
constexpr uint32_t kPubGameStateOffset = 52u;
constexpr uint32_t kPubGameProfileOffset = 124u;

constexpr uint32_t kPubProfileGoldOffset = 0x108u;
constexpr uint32_t kPubProfileDebtOffset = 0x130u;
}  // namespace


REFII_HOST_ABI_EXPORT int RefiiHostGetPubGame(uint32_t* out,
                                              char* name,
                                              uint32_t name_capacity,
                                              char* variation,
                                              uint32_t variation_capacity) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 6; ++i) {
        out[i] = 0;
    }

    if (name != nullptr && name_capacity > 0) {
        name[0] = '\0';
    }

    if (variation != nullptr && variation_capacity > 0) {
        variation[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t state = read32(kPubGameManager + kPubGameStateOffset);
    const uint32_t profile = read32(kPubGameManager + kPubGameProfileOffset);

    out[0] = state;
    out[1] = plausible(profile) ? profile : 0u;

    if (out[1] != 0u) {
        // BIG ENDIAN, so the HIGH word is at the lower address. Reading these the other way round
        // turns 500 gold into 2,147,483,648 and the settlement would credit the difference.
        out[2] = read32(profile + kPubProfileGoldOffset);
        out[3] = read32(profile + kPubProfileGoldOffset + 4u);
        out[4] = read32(profile + kPubProfileDebtOffset);
        out[5] = read32(profile + kPubProfileDebtOffset + 4u);
    }

    const auto read_string = [&](uint32_t at, char* into, uint32_t capacity) {
        if (into == nullptr || capacity == 0) {
            return;
        }

        const uint32_t object = read32(at);
        if (!plausible(object)) {
            return;
        }

        const uint32_t text = read32(object);
        if (!plausible(text)) {
            return;
        }

        uint32_t i = 0;
        for (; i + 1 < capacity; ++i) {
            const char c = static_cast<char>(GuestByteAt(base, text + i));
            if (c == '\0') {
                break;
            }

            into[i] = c;
        }

        into[i] = '\0';
    };

    read_string(kPubGameManager + kPubGameNameOffset, name, name_capacity);
    read_string(kPubGameManager + kPubGameVariationOffset, variation, variation_capacity);
    return 1;
}

/// Godot plays the pub games: the guest's pump stops creating the Scaleform module and hands
/// the session to the host instead. See Fable2/src/vfs/src/hooks/pubgame_pump.cpp.
REFII_HOST_ABI_EXPORT int RefiiHostClaimPubGames(int claimed) {
    refii::vfs::ClaimPubGames(claimed != 0);
    return 1;
}

/// One fscommand from Godot's game. Kinds and argument slots are HostBridge.PubGameCommand;
/// the pump hands each to the branch of the guest's fscommand handler that would have taken it.
REFII_HOST_ABI_EXPORT int RefiiHostPostPubGameCommand(uint32_t kind,
                                                      const char* text,
                                                      double n0,
                                                      double n1,
                                                      double n2,
                                                      int64_t i0,
                                                      int64_t i1) {
    const double numbers[3] = {n0, n1, n2};
    const int64_t integers[2] = {i0, i1};
    refii::vfs::PostPubGameCommand(kind, text, numbers, integers);
    return 1;
}

/// What the pump told the movie since the last call; bits are HostBridge.PubGameEvent.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakePubGameEvents(void) {
    return refii::vfs::TakePubGameEvents();
}

/// What `sub_82EF18A0` hands the movie before `pubGame.Run`, read from the same places.
///
/// Fills 14 words: rank (int64 +0x110, high word first), the three per-game point slots
/// (int64 +0x118 + 8*i, `sub_82EE8770`), the tournament item bits (+0x35C, `sub_82EE88F0`), the
/// pad (mgr+116), the sound and music volumes as float bits (mgr+128/+132, 0..1), and the gold
/// and debt the session started from (mgr+96/+100). The name is the profile's UTF-16 string at
/// +8 (`sub_82EE8538`, handed over as a GFx StringW), written out as UTF-8; the language is the
/// two letters at mgr+104 after `sub_82467EF8`'s "sk" and missing-file fallbacks.
REFII_HOST_ABI_EXPORT int RefiiHostGetPubGameProfile(uint32_t* out,
                                                     char* name,
                                                     uint32_t name_capacity,
                                                     char* language,
                                                     uint32_t language_capacity) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 14; ++i) {
        out[i] = 0;
    }

    if (name != nullptr && name_capacity > 0) {
        name[0] = '\0';
    }

    if (language != nullptr && language_capacity > 0) {
        language[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };
    const uint32_t profile = read32(kPubGameManager + kPubGameProfileOffset);
    if (profile < 0x10000u || profile >= 0xC0000000u) {
        return 0;
    }

    out[0] = read32(profile + 0x110u);
    out[1] = read32(profile + 0x114u);
    for (uint32_t i = 0; i < 3; ++i) {
        out[2 + (i * 2)] = read32(profile + 0x118u + (i * 8u));
        out[3 + (i * 2)] = read32(profile + 0x11Cu + (i * 8u));
    }

    out[8] = read32(profile + 0x35Cu);
    out[9] = read32(kPubGameManager + 116u);
    out[10] = read32(kPubGameManager + 128u);
    out[11] = read32(kPubGameManager + 132u);
    out[12] = read32(kPubGameManager + 96u);
    out[13] = read32(kPubGameManager + 100u);

    if (name != nullptr && name_capacity > 1) {
        uint32_t written = 0;
        for (uint32_t i = 0; i < 64; ++i) {
            const uint32_t unit = (static_cast<uint32_t>(GuestByteAt(base, profile + 8u + i * 2u)) << 8) |
                                  GuestByteAt(base, profile + 9u + i * 2u);
            if (unit == 0u) {
                break;
            }

            char encoded[3];
            uint32_t length = 0;
            if (unit < 0x80u) {
                encoded[length++] = static_cast<char>(unit);
            } else if (unit < 0x800u) {
                encoded[length++] = static_cast<char>(0xC0u | (unit >> 6));
                encoded[length++] = static_cast<char>(0x80u | (unit & 0x3Fu));
            } else {
                encoded[length++] = static_cast<char>(0xE0u | (unit >> 12));
                encoded[length++] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
                encoded[length++] = static_cast<char>(0x80u | (unit & 0x3Fu));
            }

            if (written + length + 1 > name_capacity) {
                break;
            }

            for (uint32_t k = 0; k < length; ++k) {
                name[written++] = encoded[k];
            }
        }

        name[written] = '\0';
    }

    if (language != nullptr && language_capacity > 2) {
        language[0] = static_cast<char>(GuestByteAt(base, kPubGameManager + 104u));
        language[1] = static_cast<char>(GuestByteAt(base, kPubGameManager + 105u));
        language[2] = '\0';
    }

    return 1;
}

/// The chosen co-op hero save's name, or 0 bytes when none is chosen.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopHeroSaveName(char* out, uint32_t capacity) {
    if (out != nullptr && capacity != 0) out[0] = '\0';
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
    const uint32_t saves = plausible(game) ? GuestWordAt(base, game + 148u) : 0u;
    if (!plausible(saves)) return 0;
    const std::string name = ReadGuestString(base, saves + 4u);
    WriteOut(name, out, capacity);
    return static_cast<uint32_t>(name.size());
}

/// The chosen co-op hero save, as the guest holds it. `SaveMgr = *(*(0x83496AB8)+12)+148`.
/// `out_values` takes 28: money, gender, morality, purity, renown, 15 ranks, 4 experience
/// pools, 4 totals — in `sub_82440F78`'s order. Returns 1 when a save is chosen; the block is
/// only meaningful then.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopHeroSave(float* out_values, uint32_t value_count) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };
    const auto put = [&](uint32_t slot, float value) {
        if (out_values != nullptr && slot < value_count) out_values[slot] = value;
    };

    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
    const uint32_t saves = plausible(game) ? GuestWordAt(base, game + 148u) : 0u;
    if (!plausible(saves)) return 0;

    // The chosen name: an LHString whose data word sits at +4 and whose length is at +4 of
    // that — `sub_8243AE90` at 0x8243AEB8..0x8243AEE4.
    const uint32_t name = GuestWordAt(base, saves + 4u);
    if (!plausible(name) || GuestWordAt(base, name + 4u) == 0u) return 0;

    const uint32_t block = saves + 1016u;
    put(0, static_cast<float>(static_cast<int32_t>(GuestWordAt(base, block + 0u))));
    put(1, static_cast<float>(GuestWordAt(base, block + 4u)));
    put(2, read_float(block + 8u));
    put(3, read_float(block + 12u));
    put(4, static_cast<float>(static_cast<int32_t>(GuestWordAt(base, block + 16u))));
    for (uint32_t i = 0; i < 15u; ++i) {
        put(5u + i, static_cast<float>(static_cast<int32_t>(GuestWordAt(base, block + 20u + 4u * i))));
    }
    const uint32_t pools = GuestWordAt(base, block + 84u);
    const uint32_t pools_end = GuestWordAt(base, block + 88u);
    const uint32_t totals = GuestWordAt(base, block + 100u);
    for (uint32_t i = 0; i < 4u; ++i) {
        const bool have = plausible(pools) && pools + 4u * (i + 1u) <= pools_end;
        put(20u + i, have ? read_float(pools + 4u * i) : 0.0f);
        put(24u + i, have && plausible(totals) ? read_float(totals + 4u * i) : 0.0f);
    }
    return 1;
}

/// The hero's looks that no other export carries. Returns 1 when the hero has component 57.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroLooks(uint32_t* out_face, float* out_fat,
                                                     float* out_height, float* out_tan) {
    if (out_face != nullptr) *out_face = 0;
    if (out_fat != nullptr) *out_fat = 0.0f;
    if (out_height != nullptr) *out_height = 0.0f;
    if (out_tan != nullptr) *out_tan = 0.0f;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };
    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t stats = hero != 0u ? EntityComponent(base, hero, 57u) : 0u;
    const uint32_t morph = hero != 0u ? EntityComponent(base, hero, 17u) : 0u;
    if (stats == 0u) return 0;
    if (out_face != nullptr && morph != 0u) *out_face = GuestWordAt(base, morph + 180u);
    if (out_fat != nullptr) *out_fat = read_float(stats + 56u);
    if (out_height != nullptr) *out_height = read_float(stats + 60u);
    if (out_tan != nullptr) *out_tan = read_float(stats + 52u);
    return 1;
}

/// The hero's equipped weapons as record ids: `sub_824AD108` stores the weapon entity's `+28`
/// into `comp28+84` (melee, 0x824AD264) or `+88` (ranged, 0x824AD400), and a live read
/// confirmed the slot holds a bare id, not a pointer. 0 for an empty slot. Returns 1 when the
/// hero has a weapon set at all.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroWeapons(uint32_t* out_melee, uint32_t* out_ranged) {
    if (out_melee != nullptr) *out_melee = 0;
    if (out_ranged != nullptr) *out_ranged = 0;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t weapons = hero != 0u ? EntityComponent(base, hero, 28u) : 0u;
    if (weapons == 0u) return 0;

    if (out_melee != nullptr) *out_melee = GuestWordAt(base, weapons + 84u);
    if (out_ranged != nullptr) *out_ranged = GuestWordAt(base, weapons + 88u);
    return 1;
}

/// The item entities a player holds, as net ids (ABI 135, 64-bit since 170). Component 24
/// entries of 12 bytes at `+16..+20`, holder at entry+8, item = `*holder` (`sub_824FB4E8`).
/// An item is melee or ranged by the guest's own test (`sub_824AD108` at 0x824AD254): component
/// 179's `+12` through `sub_82561420` — 2..4 melee, 5..8 ranged. Component 28's `+84`/`+88` are
/// NOT the key: on a body the client controls, the client's weapon-set message (`sub_824AB0A0`,
/// 0x824AB0BC..) zeroes both and refills them with entity id words, so on the host player two's
/// read 0/0 while both weapons hung on the body (2026-10-08) and the joiner was never told which
/// replicas to attach. The first held item of each class answers. The net
/// id is the entity's 64-bit uid, high word `entity+20`, low word `entity+24` — `sub_821B2390`
/// returns `(entity+0x14 << 32) | entity+0x18` on the host, and `sub_82334A60(manager, id)`
/// takes that whole value in r4. An entity made on the host carries its creation counter in the
/// HIGH word (`sub_8238B9C8`: `{counter, 0}`), so the low word alone reads as zero and names
/// nothing. Returns how many of the two were found.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopHeldUids(uint32_t player, uint64_t* out_melee,
                                                        uint64_t* out_ranged) {
    if (out_melee != nullptr) *out_melee = 0u;
    if (out_ranged != nullptr) *out_ranged = 0u;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0u;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0u;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t entity = PlayerEntityForKey(base, player);
    if (entity == 0u) return 0u;
    const uint32_t held = EntityComponent(base, entity, 24u);
    if (held == 0u) return 0u;
    const uint32_t begin = GuestWordAt(base, held + 16u);
    const uint32_t end = GuestWordAt(base, held + 20u);
    if (!plausible(begin) || end < begin || (end - begin) > 12u * 64u) return 0u;
    uint32_t found = 0u;
    for (uint32_t at = begin; at + 12u <= end; at += 12u) {
        const uint32_t holder = GuestWordAt(base, at + 8u);
        if (!plausible(holder)) continue;
        const uint32_t item = GuestWordAt(base, holder);
        if (!plausible(item) || (GuestByteAt(base, item + 144u) & 0x40u) == 0u) continue;
        const uint32_t kind = EntityComponent(base, item, 179u);
        if (kind == 0u) continue;
        const uint32_t type = GuestWordAt(base, kind + 12u);
        const bool melee = type >= 2u && type <= 4u;
        const bool ranged = type >= 5u && type <= 8u;
        const uint64_t net = (static_cast<uint64_t>(GuestWordAt(base, item + 20u)) << 32) |
                             GuestWordAt(base, item + 24u);
        if (melee && out_melee != nullptr && *out_melee == 0u) {
            *out_melee = net;
            ++found;
        } else if (ranged && out_ranged != nullptr && *out_ranged == 0u) {
            *out_ranged = net;
            ++found;
        }
    }
    return found;
}

namespace {
/// The pad bound to player-map key 1 or 2, or 0xFFFFFFFF — `Player+0x28`, stored by the
/// Player ctor `sub_82389938` (`stw r5, 0x28(r31)` at 0x8238998c) from the joining pad. The
/// map is the only source of the pad; the ENTITY for a key is `PlayerEntityForKey`'s.
uint32_t PlayerPadForKey(const uint8_t* base, uint32_t wanted) {
    const uint32_t record = PlayerMapRecordForKey(base, wanted);
    return record == 0u ? 0xFFFFFFFFu : GuestWordAt(base, record + 0x28u);
}
}  // namespace


REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopSession(uint32_t* out_words, uint32_t capacity) {
    // [0] manager present, [1] session, [2] host, [3] client, [4] lost, [5] client joined,
    // [6] peer count, [7] player one entity, [8] player two entity, [9] game+140 copy present,
    // [10] game+284 — 0 front end, 1 game, 2 LOCAL co-op (the guest's own `IsLocalCoopGame`,
    // `sub_82340300`; set by `Coop_SetSharedScreenMode` 0x8233c34c), [11] player two's pad or
    // 0xFFFFFFFF, [12] split screen IN EFFECT (guest_tick_pump.cpp `RefiiCoopSplitScreen`: the
    // switch, local co-op, and player two's Player built with its own camera; the pump states it
    // each tick through refii_bridge_note_coop_split_in_effect). Words 10-12 are written only for
    // a caller that asks for them.
    constexpr uint32_t kWords = 10u;
    constexpr uint32_t kLocalWords = 13u;
    if (out_words == nullptr || capacity < kWords) return 0;
    const uint32_t written = capacity >= kLocalWords ? kLocalWords : capacity >= 12u ? 12u : kWords;
    for (uint32_t i = 0; i < written; ++i) out_words[i] = 0u;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t manager = GuestWordAt(base, refii::guest::Address(0x83497490u));
    if (plausible(manager)) {
        out_words[0] = 1u;
        out_words[1] = GuestByteAt(base, manager + 52u);
        out_words[2] = GuestByteAt(base, manager + 53u);
        out_words[3] = GuestByteAt(base, manager + 54u);
        out_words[4] = GuestByteAt(base, manager + 238u);
        out_words[5] = GuestByteAt(base, manager + 221u);
        out_words[6] = GuestWordAt(base, manager + 100u);
    }
    out_words[7] = PlayerEntityForKey(base, 1u);
    out_words[8] = PlayerEntityForKey(base, 2u);
    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
    out_words[9] = plausible(game) && plausible(GuestWordAt(base, game + 140u)) ? 1u : 0u;
    if (written >= 12u) {
        out_words[10] = plausible(game) ? GuestWordAt(base, game + 284u) : 0u;
        out_words[11] = PlayerPadForKey(base, 2u);
    }
    if (written == kLocalWords) {
        out_words[12] = refii::vfs::CoopSplitInEffect() != 0 ? 1u : 0u;
    }
    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopPlayerEntities(uint32_t* out_one, uint32_t* out_two) {
    if (out_one != nullptr) *out_one = 0;
    if (out_two != nullptr) *out_two = 0;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    if (out_one != nullptr) *out_one = PlayerEntityForKey(base, 1u);
    if (out_two != nullptr) *out_two = PlayerEntityForKey(base, 2u);
    return 1;
}

/// THE WORLD ICONS AS THE GUEST STATES THEM: every row comes from the shared table
/// (core/payload_bridge.h, src/runtime/src/core/stated_world_icons.cpp) that
/// Fable2/src/vfs/src/hooks/world_icon_hooks.cpp fills at the icons' own entries — their Init, their
/// SetType / SetColour / SetText, each Update the prompt manager runs, and the destructor. Nothing
/// here reads guest memory, and nothing walks the manager's vector.
///
/// Two classes: the CWorldIcon3D sprites (property signs, income and gift gold, opinion splashes,
/// quest markers, dog points, NPC status — drawn by Godot, the guest's `sub_82261B70` is off) and
/// the pooled HUD-element icons (types 31-35/52-54, the prompts and player markers on the 16
/// `WorldIconN` elements, whose element writes the element hooks state; only their world point and
/// slot are taken here). A row is exported while an Update drew it in the guest's latest step or the
/// one before (a pooled one once it was first placed; the table's VISIBLE, stamped by the step
/// guest_tick_pump.cpp states): the guest draws an icon only inside its Update (TU1 0x82242040,
/// disc 0x82242048), and lhPromptManager_Update runs none while a GUI is active
/// (lhGameFlow_IsGuiActive, TU1 0x821C5BE8, disc 0x821C5C58).
///
/// Words per icon: type, texture, rgba, entity, persistent, flags, pool slot, OBJECT (the icon's
/// identity for its lifetime). Values: anchor x y z, scale, seconds left, age. Text: title NUL
/// opinion NUL, per icon in order. rgba is SetColour's (+62, +61, +60) with the opacity +160 as
/// alpha; scale is the guest's own `sub_821AA758`. Flags: bit0 fading (+100), bit1 pooled, bit2
/// player two's (its key ends "_P2": mod_events.cpp, the opinion icons asked in player two's scope).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldSprites(uint32_t* words, uint32_t word_capacity,
                                                        float* values, uint32_t value_capacity,
                                                        char* text, uint32_t text_capacity,
                                                        uint32_t max_icons) {
    constexpr uint32_t kWords = 8u;
    constexpr uint32_t kValues = 6u;
    constexpr uint32_t kMaxIcons = 1024u;
    if (text != nullptr && text_capacity > 0u) {
        text[0] = '\0';
    }
    if (words == nullptr || values == nullptr) {
        return 0u;
    }

    thread_local std::vector<uint32_t> icons(kMaxIcons);
    thread_local std::vector<uint32_t> table_words(kMaxIcons * REFII_WORLD_ICON_WORDS);
    thread_local std::vector<float> table_values(kMaxIcons * REFII_WORLD_ICON_VALUES);
    thread_local std::vector<char> table_text(256u * 1024u);
    const uint32_t count = refii_bridge_world_icons_read(icons.data(), table_words.data(), table_values.data(),
                                                         table_text.data(),
                                                         static_cast<uint32_t>(table_text.size()), kMaxIcons);

    uint32_t written = 0u;
    uint32_t text_at = 0u;
    const char* at = table_text.data();
    for (uint32_t i = 0; i < count && written < max_icons; ++i) {
        const uint32_t* tw = table_words.data() + (i * REFII_WORLD_ICON_WORDS);
        const float* tv = table_values.data() + (i * REFII_WORLD_ICON_VALUES);
        at += std::strlen(at) + 1u;  // the key
        const char* title = at;
        at += std::strlen(at) + 1u;
        const char* opinion = at;
        at += std::strlen(at) + 1u;
        if ((tw[REFII_WORLD_ICON_FLAGS_WORD] & REFII_WORLD_ICON_VISIBLE) == 0u) {
            continue;
        }
        const bool pooled = tw[0] == REFII_WORLD_ICON_CLASS_HUD_ELEMENT;
        if (pooled) {
            title = "";
            opinion = "";
        }
        const uint32_t title_bytes = static_cast<uint32_t>(std::strlen(title)) + 1u;
        const uint32_t opinion_bytes = static_cast<uint32_t>(std::strlen(opinion)) + 1u;
        if ((written + 1u) * kWords > word_capacity || (written + 1u) * kValues > value_capacity ||
            (text != nullptr && text_at + title_bytes + opinion_bytes > text_capacity)) {
            break;
        }

        const uint32_t stated = tw[REFII_WORLD_ICON_FLAGS_WORD];
        uint32_t* w = words + (written * kWords);
        float* v = values + (written * kValues);
        w[0] = tw[1];
        w[1] = pooled ? 0u : tw[2];
        w[2] = pooled ? 0xFFFFFFFFu : tw[3];
        w[3] = tw[4];
        w[4] = !pooled && (stated & REFII_WORLD_ICON_PERSISTENT) != 0u ? 1u : 0u;
        w[5] = pooled ? 2u
                      : ((stated & REFII_WORLD_ICON_FADING) != 0u ? 1u : 0u) | (tw[7] == 2u ? 4u : 0u);
        w[6] = pooled ? tw[6] : 0xFFFFFFFFu;
        w[7] = icons[i];
        v[0] = tv[0];
        v[1] = tv[1];
        v[2] = tv[2];
        v[3] = pooled ? 1.0f : tv[3];
        v[4] = pooled ? 0.0f : tv[4];
        v[5] = pooled ? 0.0f : tv[5];
        if (text != nullptr) {
            std::memcpy(text + text_at, title, title_bytes);
            text_at += title_bytes;
            std::memcpy(text + text_at, opinion, opinion_bytes);
            text_at += opinion_bytes;
        }
        ++written;
    }

    // The population, once per change.
    static uint32_t reported = 0xFFFFFFFFu;
    if (written != reported) {
        reported = written;
        REXLOG_INFO("Refii world sprites: {} stated icon{} drawn of {} in the table", written,
                    written == 1u ? "" : "s", count);
    }

    return written;
}






/// Are we hosting, and will we admit a joiner? `hosting \t joinable`.
///
/// BOTH BYTES LIVE ON `g_XrnmNetContext` (`*(0x83497490)`), and both are read there by the
/// host's own admit test in `sub_822EDCF0`:
///
///     822edecc  lwz    r11, g_XrnmNetContext@l(r25)
///     822eded0  lbz    r10, 0xF3(r11)      ; hosting
///     822eded8  beq    cr6, loc_822EE5B0   ; not hosting -> refuse
///     822ededc  lbz    r11, 0xF5(r11)      ; joinable
///
/// +0xF3 is the session machinery's to set; +0xF5 is the one the title's own
/// `XboxLive.SetSessionJoinable` writes (`sub_8232CD28` is `*(ctx + 245) = a2`, and 245 = 0xF5).
REFII_HOST_ABI_EXPORT uint32_t RefiiNetHostState(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::memory::Memory* memory = SafeKernelMemory();
    uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t context = NetContext(base);
    if (context == 0u) {
        return 0;
    }

    std::string row;
    Field(row, GuestByteAt(base, context + kNetHostingByte) != 0u ? "1" : "0", '\t');
    Field(row, GuestByteAt(base, context + kNetJoinableByte) != 0u ? "1" : "0", '\n');
    return Emit(row, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Opens or closes our session to joiners — the byte `XboxLive.SetSessionJoinable` writes.
///
/// WRITTEN WHERE THE GAME READS IT rather than through the native: `sub_8232CD28` takes the
/// context bound at native registration, and that binding is not proved to be
/// `g_XrnmNetContext`. The admit test's own base is, so the byte is set there.
REFII_HOST_ABI_EXPORT int RefiiNetSetJoinable(int on) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::memory::Memory* memory = SafeKernelMemory();
    uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t context = NetContext(base);
    if (context == 0u) {
        return 0;
    }

    const uint8_t value = on != 0 ? 1u : 0u;
    return StoreGuest(base, context + kNetJoinableByte, &value, sizeof(value)) ? 1 : 0;
#else
    (void)on;
    return 0;
#endif
}
/// The HeroState version the pump last applied, in full, to player 1 or 2's current body — 0
/// when none has, after a reset, after that body was dropped, or when the guest memory is not
/// up. Godot acks the joiner's HeroState with it; the joiner stops resending at the ack.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroStateApplied(uint32_t player) {
    if (SafeKernelMemory() == nullptr) {
        return 0u;
    }
    return refii::vfs::GuestTickPumpHeroStateApplied(player);
}
}  // extern "C"

// ---------------------------------------------------------------------------
// FABLE 2'S PART OF THE CORE'S LIFECYCLE (host_core.h, TitleCallbacks).
//
// The no-GPU stand-in its D3D needs (ArmGuestNoGpuMode and its neighbours, above) runs from
// here: armed on a 1 ms watcher from initialise, completions published on their own thread,
// and the per-step latches and finishers around the UI thread's work.
namespace {

void Fable2Initialized() {
    remaster::InstallWeaponAugmentationHook();
    // THE FAST-ARM WATCHER. The guest's D3D device creation reads the no-GPU latch ONCE,
    // tens of milliseconds after publishing the device pointer; armed first, boot proceeds —
    // read first, the guest takes the "a GPU exists" path and parks in a ring wait forever.
    // Polled only from the 30 Hz step, the arm lands up to a frame late and loses that race
    // roughly half the time (observed 5 losses / 8 launches in one day). A 1 ms poll on its
    // own thread wins it by construction; the thread exits the moment the latch is armed.
    g_arm_watcher = std::thread([]() {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::minutes(3);
        while (!g_arm_watcher_stop.load(std::memory_order_relaxed) &&
               !g_no_gpu_armed.load(std::memory_order_relaxed) &&
               std::chrono::steady_clock::now() < deadline) {
            ArmGuestNoGpuMode();
            refii::PreciseSleepFor(std::chrono::milliseconds(1));
        }
    });
    const char* completion_env = std::getenv("REFII_COMPLETION_THREAD");
    if (completion_env == nullptr || std::string(completion_env) != "0") {
        // The poll period: 1 ms unless REFII_COMPLETION_PERIOD_MS (Android's
        // android_guest_completion_ms) says 2-8. A longer one reports a completion that much later.
        const char* period_env = std::getenv("REFII_COMPLETION_PERIOD_MS");
        const unsigned long asked = period_env != nullptr ? std::strtoul(period_env, nullptr, 10) : 1ul;
        const auto period = std::chrono::milliseconds(asked >= 1ul && asked <= 8ul ? asked : 1ul);
        g_completion_thread = std::thread([period]() {
            while (!g_completion_stop.load(std::memory_order_relaxed)) {
                SignalGuestFrameCompletions();
                refii::PreciseSleepFor(period);
            }
        });
    }
}

void Fable2BeforeStep() {
    // RE-ASSERT THE RENDER-FENCE HOLD. The world's own Update stamps `engine+176` with the
    // real hand-off time whenever it drains the pending `.engine_level`, so a hold written
    // once at the start of a build is wiped the first time that runs and the guest walks
    // straight into a world Godot has not finished. Costs one pointer walk per step, and
    // only while a build is actually outstanding.
    if (refii_bridge_world_build_pending() != 0) {
        if (auto* memory = SafeKernelMemory()) {
            if (uint8_t* base = memory->virtual_membase()) {
                apply_world_build_hold(base);
            }
        }
    }
}

void Fable2AfterStep() {
    // Cheap until it succeeds, then a single predictable branch.
    ArmGuestNoGpuMode();
    KeepGuestIdleCounterClear();
    SignalGuestFrameCompletions();
    // Runs on Godot's thread, which is the point: the guest thread that needs this is
    // the one spinning, so it cannot pump its own ring.
    RetireGuestSceneStreams();

    if (auto* step_memory = SafeKernelMemory()) {
        if (uint8_t* step_base = step_memory->virtual_membase()) {
            refii::vfs::FinishStuckInGameLoadingScreenFromHost(step_base);
        }
    }
}

void Fable2Shutdown() {
    g_completion_stop = true;
    if (g_completion_thread.joinable()) {
        g_completion_thread.join();
    }

    g_arm_watcher_stop = true;
    if (g_arm_watcher.joinable()) {
        g_arm_watcher.join();
    }
}

/// The device's constant shadows: `sub_8221A880` writes VS cN at dev + 16*(N+120) and
/// `sub_82214E30` PS cN at dev + 16*(N+376), dev = *(0x83360364).
bool Fable2ConstantShadows(const uint8_t* base, uint32_t* out_vs, uint32_t* out_ps) {
    const uint32_t device = GuestWordAt(base, refii::guest::Address(0x83360364u));
    if (device < 0x10000u || device >= 0xC0000000u) {
        return false;
    }
    *out_vs = device + 0x780u;
    *out_ps = device + 0x1780u;
    return true;
}

const bool g_fable2_registered = [] {
    TitleCallbacks callbacks{};
    callbacks.on_initialized = &Fable2Initialized;
    callbacks.before_step = &Fable2BeforeStep;
    callbacks.after_step = &Fable2AfterStep;
    callbacks.on_shutdown = &Fable2Shutdown;
    callbacks.constant_shadows = &Fable2ConstantShadows;
#if defined(REXGLUE_ENABLE_REXNET)
    callbacks.guest_joined_session = &GuestJoinedSession;
#endif
    RegisterTitle(callbacks);
    return true;
}();

}  // namespace

