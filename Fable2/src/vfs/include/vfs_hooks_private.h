#pragma once

#include "loose_asset_hooks.h"
#include "banks/hash_bank_runtime.h"
#include "banks/manifest_resolution.h"
#include "banks/synthetic_bank_runtime.h"
#include "core/guest_state.h"
#include "core/host_asset_reader.h"
#include "frontend/loading_sequence.h"
#include "frontend/post_video.h"
#include "index/asset_aliases.h"
#include "index/concrete_asset_resolution.h"
#include "index/open_pipeline.h"
#include "index/ownership.h"
#include "index/resource_hash_index.h"
#include "index/resource_hash_resolution.h"
#include "index/runtime_alias_cache.h"
#include "index/runtime_tables.h"
#include "resources/resource_wrappers.h"
#include "resources/texture_tracing.h"
#include "streams/host_streams.h"
#include "streams/resource_callbacks.h"
#include "streams/resource_wrapper_handlers.h"
#include "vfs_hook_constants.h"
#include "vfs_hook_types.h"
#include "core/native_lua_hooks.h"
#include "hooks/ppc_externs.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <fstream>
#include <future>
#include <initializer_list>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include <fmt/format.h>
#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>
#include <rex/system/xtypes.h>
#include <tracy/Tracy.hpp>

#include "core/payload_bridge_fable2.h"

#if !defined(REX_FUNC_PROLOGUE)
#define REX_FUNC_PROLOGUE() ((void)0)
#endif

// Logs each vtable call the guest makes on a stream object we handed it, keyed by
// (object, slot). `slot` is the vtable byte offset dispatched through, or -1 if unknown.
inline void NoteRefiiStreamMethod(std::string_view api,
                                  uint32_t stream_guest,
                                  int32_t slot = -1) {
  static std::mutex mutex;
  static std::unordered_map<uint64_t, uint32_t> seen;
  const uint64_t key =
      (static_cast<uint64_t>(stream_guest) << 32) |
      static_cast<uint32_t>(slot);
  uint32_t nth = 0;
  {
    std::lock_guard<std::mutex> lock(mutex);
    nth = ++seen[key];
    if (seen.size() > 4096u) {
      seen.clear();
    }
  }

  // First four calls, then powers of two, so the count stays readable as a frequency.
  if (nth <= 4u || (nth & (nth - 1u)) == 0u) {
    if (slot >= 0) {
      REXLOG_INFO("Refii stream 0x{:08X}: guest called slot +{} ({}) — call #{}",
                  stream_guest, slot, api, nth);
    } else {
      REXLOG_INFO("Refii stream 0x{:08X}: guest called {} — call #{}",
                  stream_guest, api, nth);
    }
  }
}

/// Logs a stream open: the object handed back and the vtable it advertises, which decides
/// every later dispatch.
inline void NoteRefiiStreamOpened(std::string_view api,
                                  uint32_t stream_guest,
                                  uint32_t vtable_guest,
                                  uint64_t size,
                                  std::string_view asset,
                                  uint32_t caller_lr) {
  static std::atomic<uint32_t> opened{0};
  const uint32_t nth = opened.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (nth <= 32u || (nth & (nth - 1u)) == 0u) {
    REXLOG_INFO("Refii stream OPENED #{} api={} object=0x{:08X} vtable=0x{:08X} size={} "
                "caller_lr=0x{:08X} asset={}",
                nth, api, stream_guest, vtable_guest, size, caller_lr, asset);
  }
}

// Runs the guest's own function and returns; the hook body below it is not reached.
#define REFII_VFS_NATIVE_PASSTHROUGH(function_name) \
  do {                                              \
    __imp__##function_name(ctx, base);              \
    return;                                         \
  } while (false)

namespace refii::vfs {
// Bink video frame capture — defined in src/core/bink_frame_state.cpp, which links into
// librefii_vfs. These two hooks compile into the payload instead, so they cross to it
// through the C entries in core/payload_bridge.h rather than by C++ symbol.
inline void NoteBinkMovieOpen(uint32_t player, uint8_t* base_for_open = nullptr) {
  refii_bridge_note_bink_movie_open(player, base_for_open);
}

/// The guest is closing this BINK object. Its caller frees the player immediately afterwards,
/// so the host must stop reading through it — see the note in `refii_bridge_note_bink_movie_closed`.
inline void NoteBinkMovieClosed(uint32_t player) {
  refii_bridge_note_bink_movie_closed(player);
}

/// The guest stated this player's destination rect (`sub_82A41E30`), in back-buffer pixels.
inline void NoteBinkMovieRect(uint32_t player, int32_t left, int32_t top, int32_t right,
                              int32_t bottom) {
  refii_bridge_note_bink_movie_rect(player, left, top, right, bottom);
}
uint32_t BinkFrameBuffersGuestAddress();

/// The movie the guest last handed to BinkOpen. The argument is EITHER a pointer to the
/// already-loaded .bik in guest memory OR a pointer to its path — the title opens the
/// startup logos from a preloaded buffer and later movies by name.
inline void NoteBinkVideoRequested(uint8_t* base, uint32_t data_guest) {
  refii_bridge_note_bink_video_requested(base, data_guest);
}

/// The sound the guest states for the movie it is about to open: the opener's `sound`
/// argument and the track ids of its `BinkSetSoundTrack`. Call BEFORE NoteBinkVideoRequested,
/// so the request the host sees already carries its sound.
inline void NoteBinkSound(uint8_t* base, uint32_t player, uint32_t sound) {
  refii_bridge_note_bink_sound(base, player, sound);
}

/// The guest stated a track's volume (`sub_82A41D88`), linear 0..1.
inline void NoteBinkVolume(uint32_t player, uint32_t track_id, float volume) {
  refii_bridge_note_bink_volume(player, track_id, volume);
}

/// The sound stated for the last requested movie: whether it is audible, its track ids and
/// each one's volume. Returns the track count written, at most `capacity`.
uint32_t RequestedBinkSound(uint32_t* out_sound, uint32_t* out_ids, float* out_volumes,
                            uint32_t capacity);

void RequestedBinkVideo(uint32_t* out_address, uint32_t* out_size,
                        uint32_t* out_generation, uint32_t* out_swapped);

/// The path form of the same request. Empty when the last request carried a buffer.
/// Writes at most `capacity` bytes including the terminator; returns the length written.
uint32_t RequestedBinkVideoName(char* out, uint32_t capacity);

/// Marks the live movie finished, so the guest's startup sequence advances in step with
/// what the host has actually shown. Returns the player retired, or 0.
uint32_t FinishCurrentBinkMovie(uint8_t* base);

/// 0 no movie, 1 the guest still wants it, 2 the guest has ended it (done or skipped).
uint32_t BinkMovieState(uint8_t* base);

/// The destination rect the guest stated for the live movie (bink+8/+12/+16/+20).
uint32_t BinkMovieDestRect(uint8_t* base, float* out_x0, float* out_y0,
                           float* out_x1, float* out_y1);

/// Brings a stuck in-game loading screen down from the host step thread — defined in
/// src/core/guest_tick_pump_state.cpp. Runs when the guest game thread is parked in its own
/// post-cutscene teardown wait and the pump can no longer fire.
void FinishStuckInGameLoadingScreenFromHost(uint8_t* base);

/// The subtitles setting the pump last sampled from the guest: 1 on, 0 off, -1 unknown.
int32_t FrontEndSubtitles();

/// Which pause tabs the guest's own predicate said it would offer, sampled on the GUI tick.
/// Bit N = tab N available, N in 2..8; 0 means it has not been sampled yet.
uint32_t PauseTabsMask();

/// The pause minimap's quest markers the guest stated this open: `out_tokens[i]` 0 Quest,
/// 1 QuestExit, 2 QuestGiver; `out_world` three floats each. Returns how many.
uint32_t PauseMarkers(uint32_t* out_tokens, float* out_world, uint32_t capacity);

// The hero's derived state lives in core/hero_derived.h.

/// One captured menu row; returns the TOTAL row count. Every out parameter is optional;
/// `out_generation` is bumped on every spawn.
uint32_t MenuRowLabel(uint32_t index, char* out_label, size_t label_capacity);

/// The screen the published capture belongs to, or 0xFFFFFFFF for none.
uint32_t MenuScreenId();

/// The in-game screen set as the guest's own calls state it: false while the set is not up or a
/// box holds the modal latch; the screen in TU1 numbering, 31 when none is up. Defined in
/// hooks/guest_tick_pump.cpp; shared so a menu boundary in another TU keys its population by the
/// same stated screen.
bool StatedInGameScreen(uint32_t* out_screen_id);

/// The handler the in-game GUI holds for a screen (TU1 id), by the guest's own lookup
/// `lhGuiManager_FindScreenHandler`; 0 when absent. An object to call or to compare an argument
/// against, never one to read. Defined in hooks/guest_tick_pump.cpp.
uint32_t InGameScreenHandler(PPCContext& ctx, uint8_t* base, uint32_t screen_id);

/// The keeper the shop screen's opener was last handed (hooks/shop_keeper_stated.cpp); 0 before.
uint32_t StatedShopkeeper();

/// Whether the pause root handler's player, as its setter last stated it, is player two
/// (hooks/gui_stated_screens.cpp).
bool StatedPausePlayerTwo();

/// Whether the abilities screen's `+4` (whose book it is), as its setter and its enter last
/// stated it, names player two (hooks/gui_stated_screens.cpp).
bool StatedAbilitiesPlayerTwo();

/// The list key Godot states its front-end main menu's fold under (`RefiiHostSetListFoldDepth`);
/// in-game screens are keyed by their TU1 id, all below 64.
constexpr uint32_t kFrontEndMainMenuList = 1010u;

/// The NAME of the row Godot states as highlighted, when it is stated for the screen the GUI
/// manager names current — the row `sub_826C1B50` answers with. Defined in hooks/gui_menu_hooks.cpp.
/// `id`, when asked for, is that row's id as Godot states it (a shop row's ticket).
bool StatedHighlightName(uint8_t* base, std::string* name, int32_t* id = nullptr);

/// The row whose MenuEvent is being dispatched: while set, it is the highlighted row every reader
/// above answers with, whatever Godot's cursor has moved to since the press. Guest thread only;
/// the dispatcher sets it around the handler call and clears it after.
void SetDispatchedRow(const std::string& name, int32_t event_type, int32_t id);
void ClearDispatchedRow();

/// A presentation parameter written as the cut UI script's `setAttribute(g_Param.element, name,
/// value)` would have written it, into the host store the parameter getters answer from
/// (hooks/gui_menu_hooks.cpp): the runner's answers to `g_Menu:CountSpawnedItems()` /
/// `GetLastItemIndex()` (`Float1`) and `GetMenuItemDataByName()` (`String2`). Written only when
/// `presentation`'s Parameters carries `name`, as the script's write would land.
void AnswerParamNumber(uint32_t presentation, const char* name, float value);
void AnswerParamText(uint32_t presentation, const char* name, const std::string& value);

/// The guest's population ticket for the item `record` on the row registry every list screen
/// shares, as its allocate and clear stated it (the lowest when several rows hold it), or 0 when
/// no row holds it; and that registry, the argument its lookup `sub_82880BC0` takes, 0 before
/// one was stated. Defined in hooks/menu_tickets_stated.cpp.
uint32_t MenuTicketForRecord(uint32_t record);
uint32_t MenuTicketRegistry();

/// Posts one presentation event the host's slide playback stated (`PresentationEvent:` action,
/// `<presentation>:<parent element>:<EVENT>`) to the callback the guest registered for it, on the
/// guest thread. False leaves the action queued (the event ids not interned yet). Defined in
/// hooks/gui_menu_hooks.cpp.
bool PostPresentationEvent(PPCContext& ctx, uint8_t* base, const std::string& spec);

/// States a menu box's close (`@MenuBox.Close <box> <answer>`); called by the one hook on the
/// close (mod_events.cpp) before the guest's body runs. display_box_hooks.cpp.
void StateMenuBoxClose(uint8_t* base, uint32_t box);
uint32_t MenuRows(uint32_t index, char* out_name, size_t name_capacity, char* out_parent,
                  size_t parent_capacity, uint32_t* out_id, int32_t* out_event_type,
                  uint64_t* out_generation, int32_t* out_rating);

// The guest tick pump's shared state — defined in src/core/guest_tick_pump_state.cpp, which
// links into librefii_vfs. The HOST posts and the PAYLOAD drains, so the queue cannot live
// beside the pump hook; see that file. The pump compiles into the payload, so it crosses
// through the C entries in core/payload_bridge.h rather than by C++ symbol.

/// The in-game loading screen the guest stood up, or 0 when none is up.
inline uint32_t ResolveInGameLoadingScreen(uint8_t* base) {
  return refii_bridge_resolve_in_game_loading_screen(base);
}

/// Ticks observed BEFORE this one, so the pump's first tick reads 0.
inline uint64_t GuestTickPumpObserveTick() {
  return refii_bridge_guest_tick_pump_observe_tick();
}
inline uint64_t GuestTickPumpDrained() { return refii_bridge_guest_tick_pump_drained(); }

/// Gives the calling thread an alternate signal stack, once (guest_tick_pump_state.cpp).
inline void EnsureAlternateSignalStack() {
  refii_bridge_ensure_alternate_signal_stack();
}

/// Lifetime for Raise Dead's creation effect, which the guest spawns and never kills (see
/// hooks/spell_fx_lifetime.cpp). The orientation hook in particle_transform.cpp notes the
/// PSInstance it is passed while a watched effect is being created; the guest-tick sweep fades
/// and then destroys what is due.
void NoteSpellFxInstance(uint32_t instance);
void SweepOrphanSpellFx(PPCContext& ctx, uint8_t* base);
/// The guest destroyed `entity` (rex_lhEntity_Destroy): a pending effect on it is dropped, never
/// faded or destroyed again through a pointer the guest has freed.
void ForgetSpellFxEntity(uint32_t entity);

// The effect transforms and the targeting highlight the guest states — defined in
// src/core/stated_fx_state.cpp and src/core/target_highlight_state.cpp, which link into
// librefii_vfs. The PAYLOAD writes and the HOST BRIDGE reads, so the tables cannot live beside
// the hooks that fill them; see those files. The hooks compile into the payload, so they cross
// through the C entries in core/payload_bridge.h rather than by C++ symbol.

/// One effect's stated world position: all four lanes of the setter's vector register.
inline void NoteFxPosition(uint32_t instance, const float* lanes) {
  refii_bridge_note_fx_position(instance, lanes);
}

/// Its stated orientation: the quaternion's four register halves, high word first.
inline void NoteFxOrientation(uint32_t instance, const uint32_t* lanes) {
  refii_bridge_note_fx_orientation(instance, lanes);
}

/// The colour stated for one renderable component, packed big-endian with R in the top byte.
inline void NoteHighlightColour(uint32_t component, uint32_t rgba) {
  refii_bridge_note_highlight_colour(component, rgba);
}

/// An entity became or stopped being a target. Counted only.
inline void NoteHighlightTarget() { refii_bridge_note_highlight_target(); }

/// One renderable's appearance as the guest just stated it; `words` per core/payload_bridge.h.
inline void NoteAppearance(uint32_t component, const uint32_t* words) {
  refii_bridge_note_appearance(component, words);
}

/// The renderable is being constructed or destroyed: whatever was stated for its address is
/// not its own.
inline void ForgetAppearance(uint32_t component) { refii_bridge_forget_appearance(component); }

/// Whether `hit` (the record the hit receiver was handed) is one the mod API built itself —
/// refii_mod_knockback / refii_mod_stagger, in flight on this thread. The HitResponse event is
/// not raised for those: the stagger is already the mod's decision (mod_api.cpp).
bool IsModStatedHit(uint32_t hit);

/// One part's morph targets as the guest just stated them; `pairs` per core/payload_bridge.h.
inline void NotePartMorph(uint32_t entity, uint32_t base_model, const uint32_t* pairs,
                          uint32_t count) {
  refii_bridge_note_part_morph(entity, base_model, pairs, count);
}

/// A decal as the guest stated it, kDecalWords words (core/payload_bridge.h).
constexpr uint32_t kDecalWordCount = kDecalWords;
inline void NoteDecal(const uint32_t* words) { refii_bridge_note_decal(words); }
inline void ClearDecals() { refii_bridge_clear_decals(); }
inline void DropDecalsOf(uint32_t twin) { refii_bridge_drop_decals_of(twin); }
inline void NoteFxMesh(uint32_t descriptor) { refii_bridge_note_fx_mesh(descriptor); }
inline void DropFxMesh(uint32_t descriptor) { refii_bridge_drop_fx_mesh(descriptor); }
inline uint32_t FxMeshes(uint32_t* out, uint32_t capacity) { return refii_bridge_fx_meshes(out, capacity); }
inline void NoteTwinAlpha(uint32_t handle, uint32_t alpha_bits) { refii_bridge_note_twin_alpha(handle, alpha_bits); }
inline void NoteTwinTint(uint32_t handle, const uint32_t* tint) { refii_bridge_note_twin_tint(handle, tint); }
inline void ForgetTwin(uint32_t handle) { refii_bridge_forget_twin(handle); }

/// The entity's texture composites are about to be restated.
inline void BeginTextureLayers(uint32_t entity) { refii_bridge_begin_texture_layers(entity); }
inline void EndTextureLayers(uint32_t entity) { refii_bridge_end_texture_layers(entity); }
/// The entity left the world: its texture layers and part morphs go with it.
inline void ForgetEntityLooks(uint32_t entity) {
  refii_bridge_forget_texture_layers(entity);
  refii_bridge_forget_part_morphs(entity);
}

/// One original texture's layers as the guest just stated them; per core/payload_bridge.h.
inline void NoteTextureLayers(uint32_t entity, uint32_t original, uint32_t kind,
                              const uint32_t* layers, uint32_t count) {
  refii_bridge_note_texture_layers(entity, original, kind, layers, count);
}
inline void GuestTickPumpNoteRan() { refii_bridge_guest_tick_pump_note_ran(); }

/// Records a debug-call/debug-lua outcome where the host's RefiiHostGetDebugCallResult reads it.
inline void NoteDebugCallResult(uint32_t addr, uint64_t r3, const std::string& note) {
  refii_bridge_guest_tick_pump_note_result(addr, r3, note.c_str(), note.size());
}

/// Takes the whole queue aside and answers how many actions came with it.
inline uint32_t GuestTickPumpBeginDrain() {
  return refii_bridge_guest_tick_pump_begin_drain();
}

/// One action from that batch. Measured first, then filled: the name is the front end's own
/// identity for a menu entry and nothing bounds its length, so a fixed buffer would be a
/// silent truncation waiting for a long one.
inline std::string GuestTickPumpTakenAction(uint32_t index) {
  const size_t needed = refii_bridge_guest_tick_pump_taken_action(index, nullptr, 0);
  std::string name(needed, '\0');
  if (needed != 0) {
    refii_bridge_guest_tick_pump_taken_action(index, name.data(), needed + 1);
  }
  return name;
}

/// The action the drain is dispatching right now, for a hook the dispatch reaches on the guest
/// thread (the front end's start-game target names it as its commit); "" outside a dispatch.
/// Defined in guest_tick_pump.cpp.
const std::string& GuestTickPumpDispatching();

/// Holds an action that could not run yet. End puts every deferred one back at the front.
inline void GuestTickPumpDeferAction(const std::string& name) {
  refii_bridge_guest_tick_pump_defer_action(name.c_str(), name.size());
}
inline void GuestTickPumpEndDrain() { refii_bridge_guest_tick_pump_end_drain(); }

inline bool GuestTickPumpWantsSaveCount() {
  return refii_bridge_guest_tick_pump_wants_save_count() != 0;
}
inline void GuestTickPumpClearSaveCountRequest() {
  refii_bridge_guest_tick_pump_clear_save_count_request();
}
inline void GuestTickPumpNoteSaveCount(int32_t count) {
  refii_bridge_guest_tick_pump_note_save_count(count);
}
inline void GuestTickPumpRequestSaveCount() {
  refii_bridge_guest_tick_pump_request_save_count();
}

/// Runs one Lua source chunk (at most 943 bytes) in the GAME VM; false when there is none.
/// Defined in hooks/guest_tick_pump.cpp.
bool RunGameLua(PPCContext& ctx, uint8_t* base, const std::string& chunk);
bool RunNewGameTransition(PPCContext& ctx, uint8_t* base);

/// Runs hard mods' registered tick callbacks. Defined in hooks/mod_api.cpp.
void RunModTicks(PPCContext& ctx, uint8_t* base);

/// Player two's household kept in the host's world (hooks/household_persist.cpp): restored when
/// their hero arrives and stashed while it stays, every tick; stashed again before player one's
/// save is written.
void DriveHouseholds(PPCContext& ctx, uint8_t* base, float seconds);
void StashHouseholdForSave(PPCContext& ctx, uint8_t* base);
/// On an online joiner, hero two's household is a mirror of the host's: its own ticks are off.
bool HouseholdIsMirror(uint8_t* base, uint32_t hero);
/// The host's household changed (a replayed estate action): stash it at the next tick.
void HouseholdChanged();
/// A joiner's estate intent (`<action>:<sign uid hex>:<value>`) run on the host for hero two.
/// Defined in hooks/mod_events.cpp.
bool ReplayEstateIntent(PPCContext& ctx, uint8_t* base, const std::string& text);
/// The DynamicWeather mod event, from the host's "ModWeather:" pump action (the body after the
/// prefix: `<from_host 0|1>|<seconds>|<level>|<weather>|<previous>`). Defined in hooks/mod_events.cpp.
void RaiseDynamicWeather(PPCContext& ctx, uint8_t* base, const std::string& body);

/// Player two's base opinion axes of a villager (comp47), and a seed of them (their first block
/// made when they have none). Defined in hooks/mod_events.cpp.
bool PlayerTwoOpinionAxes(uint32_t component, float* axes);
bool SeedPlayerTwoOpinion(PPCContext& ctx, uint8_t* base, uint32_t component, const float* axes);

/// Steps refii_mod_lua_generation: the game's Lua state was rebuilt or loaded from a save.
void BumpLuaGeneration();
}  // namespace refii::vfs

