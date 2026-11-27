// refii — the payload/host C ABI.
//
// THE PAYLOAD MUST IMPORT NO C++ SYMBOLS. librefii_generated.so is compiled on the
// player's machine by whatever compiler they have; librefii_runtime.so is compiled by us. A
// C++ symbol crossing that line binds both to one C++ ABI, and the two disagree in ways
// no flag fixes: Itanium vs MSVC mangling, libstdc++ vs libc++ std:: layout.
//
// CONTRACT
//   - C linkage, explicit-width scalars only. No bool, no enum, no class type, no
//     std:: type in any signature.
//   - NO OWNERSHIP CROSSES. Caller allocates, caller frees, and a returned string is
//     written into a caller buffer. A MinGW payload and an MSVC host hold separate CRT
//     heaps, so a pointer allocated on one side and released on the other corrupts.
//   - Call sites never name these. Each group is fronted by inline C++ wrappers in the
//     header that already declared it, so the ~729 existing call sites are unchanged.
//
// The wrappers are inline and the entries are cross-module calls either way, so this
// costs nothing: the payload already reached these through the PLT and still does.

#pragma once

// stddef.h too: size_t is not in stdint.h, and this header is included from C++ TUs where
// the only size_t in scope may be std::size_t.
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Stage 2 voice capture. Wrappers: refii::vfs::NoteVoice* in core/audio_state.h.
void refii_bridge_note_voice_created(uint32_t handle, uint32_t sample_rate,
                                     uint32_t channels);
void refii_bridge_note_voice_destroyed(uint32_t handle);
void refii_bridge_note_voice_started(uint32_t handle);
void refii_bridge_note_voice_stopped(uint32_t handle, int32_t immediate);
void refii_bridge_note_voice_volume(uint32_t handle, float linear_volume);
void refii_bridge_note_voice_pitch_octaves(uint32_t handle, float octaves);
void refii_bridge_note_voice_channel_volumes(uint32_t handle, const float* gains,
                                             uint32_t count);
void refii_bridge_note_voice_pcm(uint32_t handle, const void* pcm, uint32_t byte_count);

// Guest step sampler (src/core/guest_step_sampler.cpp). A title's tick pump states the step it
// is starting, on the calling guest thread's own PPCContext and arena base; a host thread walks
// that context's guest back chain once the step has run 250 ms. End closes the step and hands
// back the frames sampled for it (innermost first), 0 when it never stalled.
void refii_bridge_guest_step_begin(const void* ppc_context, const void* guest_base,
                                   uint64_t step);
uint32_t refii_bridge_guest_step_end(uint64_t step, uint32_t* out_frames, uint32_t capacity);

// Guest tick queue. The host POSTS and the title's tick pump DRAINS, so the queue and the
// counters live in refii_runtime (src/core/guest_tick_queue.cpp) and the payload reaches them
// here. Observe returns the tick count BEFORE the increment, so the pump's first tick is 0.
uint64_t refii_bridge_guest_tick_pump_observe_tick(void);
uint64_t refii_bridge_guest_tick_pump_drained(void);

/// The guest's render frame: incremented on the game thread each time the 30 Hz HF pass
/// (`sub_821A61B0`, which writes every character's pose) returns. 0 before the first.
uint32_t refii_bridge_guest_frame(void);
void refii_bridge_note_guest_frame(void);
/// The step time the HF pass that just counted ran for (a title's clock, seconds), kept with its
/// guest frame for the last 32 passes; and the guest frame, fractional between two passes, that a
/// time on that clock falls at (the newest pass's frame past it, the oldest kept before it; -1
/// before any pass). A title stamps a value the guest made for a time between its steps with it.
void refii_bridge_note_guest_step_time(double time);
double refii_bridge_guest_frame_at(double time);
void refii_bridge_guest_tick_pump_note_ran(void);

// The drain protocol, and ONLY from the guest tick thread. Begin takes the whole queue aside
// under the lock and answers how many it took — swapped rather than popped one at a time so
// that a post made WHILE the drain runs lands in the next tick instead of extending this one
// indefinitely, and so no host mutex is held across guest code. Read each by index with the
// usual measure-then-fill convention, defer the ones that could not run yet, and End puts the
// deferred ones back at the FRONT so a later post cannot overtake them.
uint32_t refii_bridge_guest_tick_pump_begin_drain(void);
size_t refii_bridge_guest_tick_pump_taken_action(uint32_t index, char* out, size_t capacity);
void refii_bridge_guest_tick_pump_defer_action(const char* name, size_t length);
void refii_bridge_guest_tick_pump_end_drain(void);

// The name the game shows for the player with this XUID, by RexNet's rule (core/coop_names.cpp):
// a friend's display name, anyone else's pseudonym. 1 when the XUID is a known player.
int32_t refii_bridge_safe_name_for_xuid(uint64_t xuid, char* out, uint32_t capacity);
// The name to show for a name another machine's game sent (a known player's pseudonym → their
// name by the same rule); 0 when it names no known player. And this player's own name.
int32_t refii_bridge_safe_name_for_sent(const char* sent, char* out, uint32_t capacity);
int32_t refii_bridge_own_name(char* out, uint32_t capacity);

// A member left the title's session (core/coop_channel.cpp): Refii's co-op channel forgets the
// peer it learned, so nothing restates to an address whose player has gone.
void refii_bridge_coop_channel_forget(void);

//
int32_t refii_bridge_read_host_asset(const char* path, size_t length,
                                     uint8_t* out, uint32_t capacity, uint32_t* out_size);
int32_t refii_bridge_read_host_asset_prefix(const char* path, size_t length, uint32_t count,
                                            uint8_t* out, uint32_t capacity,
                                            uint32_t* out_size);
int32_t refii_bridge_host_asset_size(const char* path, size_t length, uint32_t* out_size);
int32_t refii_bridge_host_asset_is_override(const char* path, size_t length);
size_t refii_bridge_host_asset_name_for_hash(uint32_t hash, char* out, size_t capacity);

/// The UI data generation: bumped when something an open menu lists changes (an ability level,
/// experience, an item, gold, a drawn weapon); Godot re-reads its open list when it moves.
/// A change also marks the data dirty: the pump takes the hero sample on its next tick rather
/// than at its cadence and bumps again once it is published (`_published`), so the re-read
/// that follows a change sees the changed values.
void refii_bridge_ui_data_changed(void);
int32_t refii_bridge_ui_data_take_dirty(void);
void refii_bridge_ui_data_published(void);
uint32_t refii_bridge_ui_data_generation(void);

void refii_bridge_ensure_alternate_signal_stack(void);

// The debug channel's switch for the guest's own scene draw: -1 follows REFII_GUEST_RENDER,
// 0 off, 1 on. Read by the DrawScene hook; the state is in src/core/guest_render_state.cpp.
int32_t refii_bridge_guest_render_override(void);

// Hard mods' named values, keyed "<mod>.<name>". A hook publishes, Godot reads; Godot sets a
// setting, a hook reads it. Wrappers: refii::mods in core/mod_values.h; state in
// src/core/mod_values.cpp.
void refii_bridge_mod_publish_f32(const char* key, size_t key_length, float value);
int32_t refii_bridge_mod_value_f32(const char* key, size_t key_length, float* out);
void refii_bridge_mod_set_setting_f32(const char* key, size_t key_length, float value);
float refii_bridge_mod_setting_f32(const char* key, size_t key_length, float fallback);

// A mod's TEXT in the culture the game runs in, from the host's lookup (Godot's Engine_ModText:
// the running book, the enabled mods' language/<culture>/text/book.babel.csv, their en-uk rows):
// `text` a bare key or a text with {KEY}s inside. Writes the result NUL-terminated (cut to
// `capacity`) and returns its length; -1 when no host lookup is installed. Any thread — the host
// answers on the caller's. State in src/core/mod_values.cpp, installed by RefiiHostSetModTextLookup.
int32_t refii_bridge_mod_text(const char* text, size_t length, char* out, uint32_t capacity);

// The mod event bus, both ways across the bridge (state in src/core/mod_values.cpp):
// - the payload POSTS an event the host asked for (`fields` is the Lua-table-literal body the
//   bus builds for Lua handlers) and the host drains them in order (Godot scripts' RefiiHost);
// - the host's MASK says which kinds it wants (bit = kind; nothing is queued otherwise):
//   REFII_MOD_HOST_MASK_WORDS words of 64, word w holding kinds 64w..64w+63. Words 0 and 1 arrive
//   through the host's two mask exports (RefiiHostSetModEventMask / ...MaskHigh), the words past
//   them through the tick's command lane (mod_commands.cpp `mask_word`); the verdict mask below
//   has the same shape. A kind at or past 64 * REFII_MOD_HOST_MASK_WORDS is never posted — the
//   catalog's test holds REFII_EVENT_COUNT under it.
// - the host POSTS an input edge (a mod's input.json action, pressed or released, for player 1
//   or 2) and the payload's tick polls them into InputAction events.
#define REFII_MOD_HOST_MASK_WORDS 4
// - a PUBLISHED VALUE that changed (refii_bridge_mod_publish_f32 with a new value or a new key)
//   is posted under this host-only kind as `key='<key>',value=<number>`, while any word of the
//   host's event mask is set (a Godot script is loaded): the host raises value_changed from it.
#define REFII_MOD_HOST_VALUE_KIND 1010
void refii_bridge_mod_event_post(int32_t kind, const char* fields, size_t length);
uint64_t refii_bridge_mod_host_event_mask_word(int32_t word);
void refii_bridge_mod_set_host_event_mask_word(int32_t word, uint64_t mask);
int32_t refii_bridge_mod_input_poll(char* out_name, size_t capacity, int32_t* out_player,
                                    int32_t* out_pressed);
// - the host POSTS a command line (a helper call or a query from a Godot script, the grammar in
//   Fable2/src/vfs/src/hooks/mod_commands.cpp) and the payload's tick runs it with the real guest context; a
//   query's reply is posted back as an event of kind REFII_MOD_HOST_REPLY_KIND, mask or not.
int32_t refii_bridge_mod_command_poll(char* out_line, size_t capacity);
// - a mod's Lua ERROR (init or handler) is posted as text; the host keeps them for the Studio and
//   the debug channel (RefiiHostDrainModErrors), independent of the event mask.
void refii_bridge_mod_error_post(const char* text, size_t length);
// - the HANDLE table (refii_mod_handle): one per session, shared by the payload, which assigns
//   handles, and the host, which turns a script's handle back into its entity.
uint32_t refii_bridge_mod_handle(uint32_t address);
uint32_t refii_bridge_mod_handle_address(uint32_t handle);
/// The entity at `address` is gone: its handle now resolves to 0, and an object that later takes
/// the address gets a new handle.
void refii_bridge_mod_handle_forget(uint32_t address);
// - a Godot script's VERDICT: the host registers a handler the payload calls synchronously, on the
//   guest thread inside the seam, for the kinds in the verdict mask. `fields` is the event's table
//   text; the handler writes "cancel=1,<field>=<number>,…" into `out` and returns its length.
typedef int32_t (*refii_mod_verdict_fn)(int32_t kind, const char* fields, size_t length, char* out, size_t capacity);
void refii_bridge_mod_set_verdict_handler(refii_mod_verdict_fn handler);
refii_mod_verdict_fn refii_bridge_mod_verdict_handler(void);
//   The mask is REFII_MOD_HOST_MASK_WORDS words, as the event mask.
void refii_bridge_mod_set_verdict_mask_word(int32_t word, uint64_t mask);
uint64_t refii_bridge_mod_verdict_mask_word(int32_t word);
// - a verdict's synchronous READ (RefiiHost.read): the payload registers its reader once; the host
//   calls refii_bridge_mod_verdict_read from INSIDE its verdict handler, on the guest thread, while
//   the payload waits in the seam. `line` is "VERB ARGS..." — a read-only query verb and its words,
//   the grammar of `query` in Fable2/src/vfs/src/hooks/mod_commands.cpp without the id — answered at
//   once into `out` as "ok=true|false,<fields>" (the query's reply without its id); returns the
//   length, REFII_MOD_READ_OUTSIDE off the guest thread or outside a verdict (and with no reader),
//   REFII_MOD_READ_REFUSED for a verb that changes something or does not exist.
#define REFII_MOD_READ_OUTSIDE (-1)
#define REFII_MOD_READ_REFUSED (-2)
typedef int32_t (*refii_mod_verdict_read_fn)(const char* line, size_t length, char* out, size_t capacity);
void refii_bridge_mod_set_verdict_reader(refii_mod_verdict_read_fn reader);
int32_t refii_bridge_mod_verdict_read(const char* line, size_t length, char* out, size_t capacity);

// PLAYER STATE (state in src/core/player_state.cpp, the contract in core/player_state.h): a mod's
// per-player values, owned by the machine that plays the hero and the same on both machines.
// `player` is 1 or 2; a key is "<mod>.<name>".
// - read: 1 and the value when the key is set for that player, else 0;
int32_t refii_bridge_player_get(int32_t player, const char* key, size_t key_length, float* out);
// - a TEXT under a key (at most 256 bytes; a key holds a number or a text): its length and the
//   text into `out` (NUL-terminated, cut to `capacity`), -1 when the key holds no text;
int32_t refii_bridge_player_get_text(int32_t player, const char* key, size_t key_length, char* out, uint32_t capacity);
// - write: `add` adds `value` instead of setting it; `saved` keeps the key with the player's save;
void refii_bridge_player_write(int32_t player, const char* key, size_t key_length, float value, int32_t add,
                               int32_t saved);
// - write a text: replaces whatever the key held; `saved` keeps it with the player's save;
void refii_bridge_player_write_text(int32_t player, const char* key, size_t key_length, const char* text, size_t length,
                                    int32_t saved);
// - the payload states which players this machine plays (bit 0 player one, bit 1 player two),
//   every tick; a player gaining an owner here keeps what it holds, one losing it waits for the peer;
void refii_bridge_player_set_owned(uint32_t mask);
uint32_t refii_bridge_player_owned(void);
// - a new hero for `player`: its session values go; its saved values stay until a save load
//   replaces them;
void refii_bridge_player_new_hero(int32_t player);
// - everything the player held goes: `fresh` for a new hero not loaded from a save (a new game —
//   its values may be written into the save it makes), 0 for a player who left;
void refii_bridge_player_clear(int32_t player, int32_t fresh);
// - the mod event bus is running handlers for the game's COPY of something the other co-op
//   machine decides (on 1, off 0, nested): writes are dropped meanwhile, since the deciding machine
//   makes the same ones;
void refii_bridge_player_replica(int32_t on);
int32_t refii_bridge_player_in_replica(void);
// - the game loaded `player`'s hero from the save named `save_name` (its content file name):
//   the saved values become that folder's, or none when it keeps none;
void refii_bridge_player_load_saved(int32_t player, const char* save_name);
// - whether `player`'s saved values were last loaded from the save named `save_name`;
int32_t refii_bridge_player_loaded_from(int32_t player, const char* save_name);
// - the game has just written `player`'s hero into the save named `save_name`: its saved values
//   go into that folder. Returns 1 when written.
int32_t refii_bridge_player_write_saved(int32_t player, const char* save_name);
// - saved BYTES under a key: written beside the save with the saved values and read back with them,
//   and the owner's go to the peer (in parts, when they change and every few seconds). get: the
//   size (the bytes into `out` when `capacity` holds them), -1 when none; set: null `data` drops
//   the key.
int32_t refii_bridge_player_blob_get(int32_t player, const char* key, uint8_t* out, uint32_t capacity);
void refii_bridge_player_blob_set(int32_t player, const char* key, const uint8_t* data, uint32_t size);

// THE EVENT RELAY (state in src/core/event_relay.cpp, the wire in core/event_relay.h): a mod event
// decided on this co-op machine, posted for the peer as the payload's own text (`<kind>:<fields>`,
// no ';', '|' or newline), and the peer's events taken back one at a time (1 and the text in `out`
// while one waits, 0 when none does). The payload decides what is relayed (mod_events.cpp).
void refii_bridge_event_relay_post(const char* text);
int32_t refii_bridge_event_relay_take(char* out, uint32_t capacity);

// THE ROUTE (src/core/event_relay.cpp, the wire in core/event_relay.h): the core co-op Lua's calls
// for the player the peer plays (a box, a question, a camera or HUD change, a Player.* command) and
// the answers coming back, as the Lua's own text (no ';', '|' or newline). Unlike the relay it is
// ordered and acknowledged: every line arrives once, in the order posted, or (the peer gone for
// kRouteLife) not at all. take: 1 and the text in `out` while one waits, 0 when none does.
void refii_bridge_route_post(const char* text);
int32_t refii_bridge_route_take(char* out, uint32_t capacity);
// The route's MOD LANE: a line posted with the prefix `pm:` (a mod's partner message,
// refii_mod_send_to_partner) is kept apart on the receiver and taken here — the mod bus drains it
// on the guest tick (mod_events.cpp DrainPartnerMessages) and the Lua never sees it; the same
// order and once-only as the rest of the route.
int32_t refii_bridge_route_take_mod(char* out, uint32_t capacity);
// The route's CAST LANE: a line posted with the prefix `pc:` (a cast this machine started, or a
// spell one of them made: mod_events.cpp THE CAST PAIRS) is kept apart the same way and taken
// here by the mod bus on the guest tick; the same order and once-only.
int32_t refii_bridge_route_take_cast(char* out, uint32_t capacity);

// PARTNER CONSENT (src/core/coop_consent.cpp): a co-op script asks the other player before doing
// something that changes both players' game. `kind` 1 sleep (detail: the sleep type), 2 sex,
// 0 a mod's own question (its text); `asker` the asking player, 1 or 2.
// - the payload asks (a ticket) and polls the answer: -1 pending, 0 no, 1 yes;
uint32_t refii_bridge_consent_ask(int32_t kind, int32_t asker, int32_t detail);
uint32_t refii_bridge_consent_ask_text(int32_t asker, const char* text);
int32_t refii_bridge_consent_answer(uint32_t ticket);
// - the host takes each new question once (with its text, empty for kinds 1 and 2) and gives its
//   answer.
int32_t refii_bridge_consent_take(uint32_t* ticket, int32_t* kind, int32_t* asker, int32_t* detail,
                                  char* text, uint32_t capacity);
void refii_bridge_consent_give(uint32_t ticket, int32_t yes);

// THE MOUSE'S CAMERA TURN (src/core/mouse_look_state.cpp), taken by Fable2/src/vfs/src/hooks/mouse_look.cpp
// once per camera update of the local player's camera manager, guest tick thread only.
// - take everything the host posted (degrees at the middle sensitivity; right and up positive);
//   the state's sequence goes odd until the matching state call;
void refii_bridge_mouse_look_take(float* out_yaw, float* out_pitch);
// - state what became of it: the yaw and pitch taken (posted degrees), vertical held back below
//   the camera's dead zone (returned to the pending turn), the turn the camera made for it in this
//   update (its own degrees, rightward and upward), its degrees per posted degree (0 keeps the
//   last), and how the host draws a turn not yet taken: `mode` 0 not at all, 1 about the vertical
//   through the focus `pivot_forward` metres along the level line of sight, 2 about the camera's
//   own position, 3 rigidly about the point (`pivot_forward`, `pivot_right`, `pivot_up`) metres
//   along the camera's level forward, level right and world up — the yaw about its vertical, the
//   pitch about its level right axis, elevation bounded at `pitch_limit` degrees — with the ranged
//   aim `aim` (its reticule anchor) turning with the view.
void refii_bridge_mouse_look_state(uint32_t tick, float taken_yaw, float taken_pitch,
                                   float held_pitch, float absorbed_yaw, float absorbed_pitch,
                                   float factor, float pivot_forward, float pivot_right,
                                   float pivot_up, float pitch_limit, int32_t mode,
                                   uint32_t aim);
void refii_bridge_mouse_look_state_ex(uint32_t tick, float taken_yaw, float taken_pitch,
                                      float held_pitch, float absorbed_yaw, float absorbed_pitch,
                                      float factor, float pivot_forward, float pivot_right,
                                      float pivot_up, float pitch_limit, int32_t mode,
                                      uint32_t aim, float head_x, float head_y, float head_z,
                                      uint32_t flags);
// - read the host-authoritative aim forward direction (guest world space) when active (1), else 0;
int32_t refii_bridge_mouse_look_host_aim(float* out_fwd_x, float* out_fwd_y, float* out_fwd_z);
// - whether the mouse is the player's look device (the host's device switch): nonzero while the
//   keyboard and mouse were touched last;
int32_t refii_bridge_mouse_look_device(void);
// - take every mouse-wheel notch the host posted since the last take, rightward positive (a
//   notch down +1, up -1);
int32_t refii_bridge_mouse_look_take_wheel(void);
// - count one target switch handed to a target lock (a mouse flick or a wheel notch), for the
//   debug channel.
void refii_bridge_mouse_look_flicked(void);

// THE MOVIE THE GUEST STATES, for the host to play (core/movie_state.h; Godot's GameVideo). A
// title's Bink hooks state each call as the guest makes it; the host bridge's movie exports read
// it. Every title's player has the same contract: a request (the .bik's bytes, or its path), the
// sound it is opened with, the rect it is drawn in, and two bytes of the player's own — the one
// its decode sets when the movie ends, which the host writes when its own copy ends, and the one
// a skip sets, which the host reads. The title names those bytes; nothing here knows a layout.
// - the source the guest handed its Bink open: a .bik in guest memory (forward or word-swapped,
//   as a loader that byte-swaps on the way in leaves it) or a path naming one;
void refii_bridge_movie_requested(uint8_t* base, uint32_t data_guest);
// - the sound `player` was opened with: 0 silent, else the Bink track ids it plays, at 1.0;
void refii_bridge_movie_sound(uint32_t player, uint32_t sound, const uint32_t* track_ids,
                              uint32_t count);
// - a track's linear volume, 0..1, as the guest restates it;
void refii_bridge_movie_volume(uint32_t player, uint32_t track_id, float volume);
// - `player` is open and live: its done byte (guest address), and its skip byte (0 when its
//   class has none). Drops whatever rect was stated for the player's last movie;
void refii_bridge_movie_open(uint32_t player, uint32_t done_byte, uint32_t skip_byte);
// - the skip byte of the live `player`, stated by an owner that learns it after the open;
void refii_bridge_movie_skip_byte(uint32_t player, uint32_t skip_byte);
// - the rect the guest draws `player` in, in its own 2D screen space;
void refii_bridge_movie_rect(uint32_t player, int32_t left, int32_t top, int32_t right,
                             int32_t bottom);
// - `player` is closing; the host stops reading through it before the guest frees it.
void refii_bridge_movie_closed(uint32_t player);

// THE UI CUT'S SEAM (src/runtime/src/core/gui_native_calls.cpp; C++ API core/gui_native_calls.h).
// A title's hook on its game Lua VM's `luaD_precall` recognises a `GUI.*` native by the
// IMPLEMENTATION pointer behind the thunk and posts the call with its arguments; its hook on the
// VM's registration names each implementation first. The shared host exports drain the calls and
// hold the host's answers to predicates the host owns.
// - a `GUI` registration binds `implementation` under `name`;
void refii_bridge_note_gui_native_registration(uint32_t implementation,
                                               const char* name, size_t length);
// - the first name bound to `implementation` (0 when it is not a GUI native);
int32_t refii_bridge_gui_native_name_for(uint32_t implementation,
                                         char* out, size_t capacity, size_t* out_length);
// - whether any GUI registration has landed, so the call hook skips the boot storm;
int32_t refii_bridge_gui_natives_registered(void);
// - the host's answer for `implementation`: 0 none, 1 boolean, 2 float, 3 suppress;
uint32_t refii_bridge_gui_native_answer_for(uint32_t implementation, uint32_t* out_bits);
// - the implementation whose thunk is about to run on this thread, and its read-back;
void refii_bridge_note_gui_native_entry(uint32_t implementation);
uint32_t refii_bridge_current_gui_native_entry(void);
// - one intercepted call, flattened by core/gui_native_calls.h's blob codec.
void refii_bridge_post_gui_native_call(const uint8_t* blob, size_t length);

// WHAT A TITLE'S GUEST TELLS ITS PRESENTATIONS' ELEMENTS (src/runtime/src/core/told_statements.cpp;
// the host side's C++ API core/told_statements.h). `kind` 1 slide entry, 2 paused, 3 attribute,
// 4 active, 5 texture; `a`/`b` per kind. A text is its UTF-16 units, copied.
void refii_bridge_told_push(uint32_t kind, uint32_t presentation, uint32_t root_hash, uint32_t ordinal,
                            uint32_t a, uint32_t b);
void refii_bridge_told_text_push(uint32_t presentation, uint32_t root_hash, uint32_t ordinal,
                                 const uint16_t* units, uint32_t length);
// A headless presentation (the shared headless core, core/headless_presentation.h): its serial and
// file at creation, then each element the guest names, by id (1..), its parent's id (0 the root)
// and name hash; and its destroy, which takes it off the registry's live list.
void refii_bridge_told_headless_presentation(uint32_t presentation, uint32_t serial, const char* file,
                                             uint32_t length);
void refii_bridge_told_headless_element(uint32_t presentation, uint32_t serial, uint32_t id,
                                        uint32_t parent_id, uint32_t name_hash);
void refii_bridge_told_headless_gone(uint32_t presentation, uint32_t serial);
// The guest's write through one of a presentation's two gates: `gate` 0 the scene draw, 1 the
// per-frame update; `parked` non-zero when the guest switched it off. Ignored for a presentation
// that is not on the live list.
void refii_bridge_told_headless_parked(uint32_t presentation, uint32_t gate, uint32_t parked);
// The screen id the guest loaded a live presentation for, in the title's own numbering.
void refii_bridge_told_headless_screen(uint32_t presentation, uint32_t screen);
// The screen the guest's screen setter says is up now, in the title's own numbering; 0xFFFFFFFF
// (refii::vfs::kNoStatedScreen) when it says none is. `_now` reads the last one stated.
void refii_bridge_stated_screen(uint32_t screen);
uint32_t refii_bridge_stated_screen_now(void);
// The rest of a title's screen-set state, as its own calls state it: whether the in-game screen
// set exists at all, the modal latch and id its openers set (1 and 0 when no box is up), and the
// tab its tab strip was last shown on. `_pause` reads them back and returns whether the set is up.
void refii_bridge_stated_screens_up(uint32_t up);
void refii_bridge_stated_modal(uint32_t mode, uint32_t id);
void refii_bridge_stated_tab(uint32_t tab);
uint32_t refii_bridge_stated_pause(uint32_t* modal_mode, uint32_t* modal_id, uint32_t* tab);
// ONE WORD A TITLE'S OWN CALL STATED, by key (0..31); 0 until stated. The keys are the shell's
// (Godot/src/host/HostBridge.cs StatedValue): 1 the abilities screen's assignment mode (0|1),
// 2 the vtable of the game-flow command on top of the stack, 3 that stack's depth; the loading
// screen's 4 fixed art, 5 text-only variant, 6 quit fade.
// The option levels (Fable 2): 7 gamma, 8 sfx, 9 music, 10 speech as float bits, 11 the speaker
// mode, 12 which of 7..11 are stated (bit 0 gamma .. bit 4 speaker).
// The front end (Fable 2): 13, one word — bit 0 the front-end GUI exists, bits 1-2 its active
// screen (0 none, 1 title, 2 main menu), bit 3 the title takes A, bits 4-5 the screen whose
// warning is raised (0 none), bits 6-7 the pad the title took A from, bits 8-15 the title's mode,
// 16-23 the main menu's, 24-31 the Load Game card.
// The rumble switch (Fable 2): 14 — bit 1 the setter has run, bit 0 the switch it set.
// The weapon switches the menu has pending (Fable 2): 15 the melee item, 16 the ranged item (record
// ids, 0 none). The HUD experience counter (Fable 2): 17 — bit 0 shown for hero one, bit 1 hero two.
// The front-end game-flow mode (Fable 2): 18 — 1 from its enter to its exit.
// The world held (Fable 2): 19 — 1 when the last game-flow tick ran no world sim tick; 2 when the world
// ran under a pause all the same (the player's own pause in an online session); 0 in play.
// The abilities book's discard gate (Fable 2): 20 — the highlighted row's record id with bit 0 replaced by
// the guest's gate `sub_826C3A30` for it (1 open); 0 when no row has been gated.
// The co-op leash (both titles, refii_bridge_coop_tether_stated): 21 the distance the camera last stated
// to Debug.SetLimitCharacterDistance and 22 the heroes' head-to-head separation, float bits; 23 a serial,
// one more each statement (0 before the first). Fable 3's couch: 24 — 1 while player two plays on this
// screen (a shared-screen session with a key-2 Player, Fable3/src/hooks/src/coop_camera_hooks.cpp).
// Player two's half's GUI.SetShouldDrawPlayerIcons (both titles, Refii._partner_player_icons): 25 — 1 off, 2 on,
// 0 before one was stated; stated by player two's camera and, in local co-op, by every statement that is no
// camera's (a quest's). The hero marker's record update takes it while split (each title's coop_player_icons.cpp).
void refii_bridge_stated_value(uint32_t key, uint32_t value);
uint32_t refii_bridge_stated_value_now(uint32_t key);
// The core camera chunk's statement of the co-op leash each camera tick (Refii._coop_tether_stated):
// writes keys 22, 21, then 23.
void refii_bridge_coop_tether_stated(float distance, float separation);

// THE VIEW THE SHELL DRAWS (src/runtime/src/core/stated_view.cpp): the camera the shell rebuilt
// from the statements below, stated back every frame (RefiiHostStateCamera) for a title's hooks
// that read it natively.
// - position, up and forward (the guest's world axes) and the horizontal field of view in
//   radians, `snap` non-zero when the guest cut rather than blended;
void refii_bridge_camera_stated(const float* position, const float* up, const float* forward,
                                float horizontal_fov, int32_t snap);
// - the last stated camera, 11 floats (position, up, forward, horizontal fov, snap); 0 before
//   any was stated.
int32_t refii_bridge_camera_read(float* out);
// THE HOST'S WORLD BUILD: set while Godot builds the level the guest is loading; a title's hook
// on the guest's own preload gate reads it, so the guest waits in its loading screen.
void refii_bridge_set_world_build_pending(int32_t pending);
int32_t refii_bridge_world_build_pending(void);

// THE CAMERA A TITLE STATES (src/runtime/include/core/camera_statements.h for the record and the
// call ids): its hooks state each camera setter, activation, easing step and push in the guest's
// order; the shell reads them back by sequence.
// - one record of refii::camera::kRecordWords words; the runtime writes word 0 (the sequence) and
//   word 22 (the guest frame), the rest is copied;
void refii_bridge_camera_statement(const uint32_t* words);
// - the depth of the camera hook the calling thread is inside (0 outside any), raised by enter and
//   lowered by leave; a statement made inside another names the outer's depth plus one;
uint32_t refii_bridge_camera_enter(void);
void refii_bridge_camera_leave(void);
// - up to `max_records` records whose sequence is after `after`, oldest first, into `out_words`;
//   returns how many. A first record more than one past `after` means older ones were overwritten.
uint32_t refii_bridge_camera_statements_read(uint32_t after, uint32_t* out_words, uint32_t max_records);

// WHAT THE STATEMENTS SAY PER PLAYER (stated_view.cpp, taken as each record is stated, guest thread):
// - the block the guest's own getter last built for player `player`'s manager (the kGuestBlock records,
//   keyed by the kPush that names the manager's player-map key): position, forward, up (guest axes),
//   horizontal fov in radians, the cut byte; with no manager keyed `player` the manager this machine
//   draws (one shared camera). 1 when one is held.
int32_t refii_bridge_camera_stated_block(int32_t player, float* out11);
// - the camera events the statements carry, oldest first, refii::camera::kEventWords words each, up to
//   `max_events`; returns how many and forgets them. Kinds refii::camera::kEventCut (the block's cut byte
//   rose), kEventBlend (an InterpolateFor with ticks > 0 in the update), kEventTick (the update's push,
//   with the block the readers see); each is the player of the push that ends the update.
uint32_t refii_bridge_camera_events_take(uint32_t* out_words, uint32_t max_events);

// THE VIEWS THE SHELL DRAWS (stated_view.cpp; RefiiHostStateCameraViews, every drawn frame): the screen
// (mode REFII camera screen 0 shared, 1 split side by side, 2 split stacked, 3 hybrid; split 1 while two
// views are drawn) and per player the pose drawn and the part of the window it fills,
// refii::camera::kViewWords words (core/camera_views.h lays them out, and the events' words).
void refii_bridge_camera_views_stated(const float* words, uint32_t count);
// - player `player`'s view, kViewWords floats; 0 when no picture of that player is drawn here.
int32_t refii_bridge_camera_view_read(int32_t player, float* out_words);
// - the screen mode; `split` (may be null) 1 while two views are drawn. -1 before the shell stated one.
int32_t refii_bridge_camera_screen_read(int32_t* split);

// THE DISPLAY OFFSET A MOD STATES (camera_offset, resolved in the game VM by the camera store): player
// `player`'s, seven floats (right, up, back in metres along the drawn camera's own axes; yaw, pitch,
// roll and fov in degrees); `on` 0 clears it. Read by the shell for the drawn frame only: never stated
// to the guest, never saved.
void refii_bridge_camera_offset_set(int32_t player, const float* seven, int32_t on);
int32_t refii_bridge_camera_offset_read(int32_t player, float* out7);

// THE WORLD A TITLE STATES (src/runtime/src/core/stated_world.cpp): what its hooks see the guest's
// own entry points called with; its host bridge reads each back for the shell.
// - a level manager's level, up at its loader's success and down at its unload, by the manager
//   the guest passed (several stand at once; Fable 3's front end keeps a GUI world and the game).
void refii_bridge_level_stated(uint32_t manager, int32_t up);
int32_t refii_bridge_level_is_up(uint32_t manager);
// - an effect, by the handle the guest keys it on: created with its effect name hash, started,
//   given a render scene, placed (the four VMX lanes of the position / orientation argument, in
//   the recompiler's lane order), killed, or killed with every effect of a scene.
void refii_bridge_fx_created(uint32_t handle, uint32_t name_hash);
void refii_bridge_fx_started(uint32_t handle);
void refii_bridge_fx_scene(uint32_t handle, uint32_t scene);
void refii_bridge_fx_position(uint32_t handle, const float* lanes);
void refii_bridge_fx_orientation(uint32_t handle, const float* lanes);
void refii_bridge_fx_killed(uint32_t handle);
void refii_bridge_fx_scene_killed(uint32_t scene);
// A kill with a fade-out over `seconds`, stamped with the guest frame it was stated on.
void refii_bridge_fx_fade(uint32_t handle, float seconds);
// The effect's colour as its setters state it: red, green and blue together, alpha alone, and
// its size scale. A create resets all four lanes and the scale to 1.
void refii_bridge_fx_colour(uint32_t handle, float red, float green, float blue);
void refii_bridge_fx_alpha(uint32_t handle, float alpha);
void refii_bridge_fx_scale(uint32_t handle, float scale);
// A spline setter's point list (x y z each, guest world space, at most
// REFII_STATED_FX_SPLINE_POINTS kept), read back by handle; a particle flush (every live particle
// of the effect's standard emitters dies, emission goes on), counted; the saturation mask turned
// on for the effect's standard emitters. A create clears all three.
#define REFII_STATED_FX_SPLINE_POINTS 32u
void refii_bridge_fx_spline(uint32_t handle, const float* points, uint32_t count);
uint32_t refii_bridge_fx_spline_read(uint32_t handle, float* out, uint32_t max_points);
void refii_bridge_fx_flush(uint32_t handle);
void refii_bridge_fx_saturation_mask(uint32_t handle);
// The entity that plays the effect and the entity that one was attached to, as the title's emitter hooks state them.
void refii_bridge_fx_owner(uint32_t handle, uint32_t owner, uint32_t attached);
//   Read back as rows of REFII_STATED_FX_WORDS words: handle, name hash, sequence (the last
//   statement's), flags, four position lanes, four orientation lanes, the owner entity, the entity it
//   was attached to, the fade-out's seconds (float bits) and the guest frame it was stated on
//   (both 0 until a fade is stated), the colour's red, green, blue and alpha and the size scale
//   (float bits, 1 until stated), the flushes stated. Returns the rows written;
//   the count is the statements since boot (0 until a hook has stated one).
#define REFII_STATED_FX_WORDS 22u
#define REFII_STATED_FX_STARTED 1u
#define REFII_STATED_FX_PLACED 2u
#define REFII_STATED_FX_ORIENTED 4u
#define REFII_STATED_FX_FADING 8u
#define REFII_STATED_FX_SPLINE 16u
#define REFII_STATED_FX_SATURATION 32u
uint32_t refii_bridge_fx_rows(uint32_t* out_words, uint32_t word_capacity);
uint32_t refii_bridge_fx_statement_count(void);
// - a decal, in the 15 words Fable 2's decal hook states (the shell's RefiiHostGetDecals row
//   head): [0] serial, assigned here, [1] owner twin, [2] texture name hash, [3..6] position,
//   [7..10] direction, [11] [12] half sizes, [13] life, [14] flag. The guest keeps 50, a new one
//   replacing the oldest; a clear drops them all, an owner's teardown drops its own. Read oldest
//   first; `out_serial` moves whenever the set changed.
#define REFII_WORLD_DECAL_WORDS 15u
void refii_bridge_world_decal_stated(const uint32_t* words);
void refii_bridge_world_decals_cleared(void);
void refii_bridge_world_decals_dropped(uint32_t owner);
uint32_t refii_bridge_world_decals_read(uint32_t* out, uint32_t capacity, uint32_t* out_serial);
// - a displacement quad: a render object of type 29 (the drunk warp's full-screen quad in both
//   titles), in the 14 words its message 35 carries — [0..3] x0 y0 x1 y1 (ints, pixels), [4] z,
//   [5] rotation, [6..13] the four corners' uv — keyed by the id the scene allotted the object,
//   with the scene it was added to. Removed by that id. Read back as rows of 2 + 14 words
//   [id, scene, words...].
#define REFII_WORLD_QUAD_WORDS 14u
void refii_bridge_world_quad_stated(uint32_t id, uint32_t scene, const uint32_t* words);
void refii_bridge_world_quad_removed(uint32_t id);
uint32_t refii_bridge_world_quads_read(uint32_t* out, uint32_t capacity);
// - an FX mesh particle's render object: the id the scene allotted it, with the guest address of
//   the descriptor the effect's mesh renderer added it from (the host reads the descriptor and
//   the twin the id names). Removed by that id. Ids at or past REFII_WORLD_FX_MESH_IDS are not
//   kept. Read back as [id, descriptor] pairs, ascending id; returns the pairs written.
#define REFII_WORLD_FX_MESH_IDS 65536u
void refii_bridge_world_fx_mesh_stated(uint32_t id, uint32_t descriptor);
void refii_bridge_world_fx_mesh_removed(uint32_t id);
uint32_t refii_bridge_world_fx_meshes_read(uint32_t* out_pairs, uint32_t pair_capacity);
// GUI scenes: a scene the guest presents in place of the world (Fable 3's map tables), keyed by an
// opaque holder the title's hooks choose. Implemented in src/core/stated_scene_objects.cpp.
// - an object: a mesh render object added to the holder's scene, keyed by the id the scene allotted
//   it, in REFII_SCENE_OBJECT_WORDS words — [0] model hash [1] flags [2] record id [3] message
//   class [4..7] orientation quaternion x y z w [8..10] position x y z [11] uniform scale
//   [12..15] tint r g b a [16] alpha (floats as bits). Patched in place by the title's per-object
//   commands (first word, count); removed by id, or all of a holder's when it is dropped. Read back
//   as rows of 1 + REFII_SCENE_OBJECT_WORDS words [id, words...] for one holder; `out_serial`
//   moves whenever the set (not a transform) changed.
// - the camera the holder's scene is presented through, REFII_SCENE_CAMERA_WORDS words — [0..2]
//   position [3..6] orientation quaternion x y z w [7] vertical fov in radians [8] near [9] far.
// - presented: whether the guest presents the holder's scene now. The read answers the holder
//   presented last (0 for none) with its camera.
#define REFII_SCENE_OBJECT_WORDS 17u
#define REFII_SCENE_CAMERA_WORDS 10u
void refii_bridge_scene_object_stated(uint32_t holder, uint32_t id, const uint32_t* words);
void refii_bridge_scene_object_patch(uint32_t id, uint32_t first, const uint32_t* words, uint32_t count);
void refii_bridge_scene_object_removed(uint32_t id);
void refii_bridge_scene_holder_dropped(uint32_t holder);
void refii_bridge_scene_camera_stated(uint32_t holder, const uint32_t* words);
void refii_bridge_scene_presented(uint32_t holder, int32_t on);
uint32_t refii_bridge_scene_presented_read(uint32_t* out_camera_words, int32_t* out_camera_stated);
uint32_t refii_bridge_scene_objects_read(uint32_t holder, uint32_t* out, uint32_t capacity, uint32_t* out_serial);

// THE APPEARANCE A TITLE STATES (src/runtime/src/core/stated_appearance.cpp): what its hooks see
// a character's draw virtual hand its renderer, read back by its host bridge in Fable 2's rows
// (Fable2/src/hostbridge/src/host_bridge.cpp: RefiiHostGetEntityPartMorphs / -TextureLayers).
// - a part's morph targets: `pairs` is `count` x {target model hash, weight float bits}, the
//   list the guest built after its own entry {base, 1 - sum}; count 0 states the part unmorphed.
//   Read back per morphed part as [base, count, count x (target, weight bits)].
#define REFII_PART_MORPH_PARTS 24u
#define REFII_PART_MORPH_TARGETS 16u
void refii_bridge_part_morph_stated(uint32_t entity, uint32_t base_model, const uint32_t* pairs,
                                    uint32_t count);
uint32_t refii_bridge_part_morphs_read(uint32_t entity, uint32_t* out, uint32_t capacity,
                                       uint32_t* out_generation, uint64_t* out_statements);
// - a character's texture work, per ORIGINAL texture, in Fable 2's layer words: 0 texture, 1 blend
//   mode, 2 weight (float bits), 3 has-tint, 4 tint bytes B G R A (an overlay's spec texture),
//   5 brightness (float bits), 6 bump texture. Kinds as Fable 2 states them (0 composite with layer 0 the base, 1 overlay,
//   2 whole-entity swap) plus 3, a COLOUR MASK (Fable 3's dye: two layers, each the mask texture
//   at weight 1 with a dye colour as the tint and its DyeBrightness as the brightness). A rebuild
//   is staged between `begin` and
//   `end` and published whole, so a read never sees half of one.
#define REFII_TEXTURE_LAYER_WORDS 7u
#define REFII_TEXTURE_LAYERS_PER_TEXTURE 16u
#define REFII_TEXTURE_LAYER_TEXTURES 40u
#define REFII_TEXTURE_LAYER_KIND_COLOUR_MASK 3u
void refii_bridge_texture_layers_begin(uint32_t entity);
void refii_bridge_texture_layers_end(uint32_t entity);
void refii_bridge_texture_layers_stated(uint32_t entity, uint32_t original, uint32_t kind,
                                        const uint32_t* layers, uint32_t count);
//   Read back as [original, count | kind << 16, count x REFII_TEXTURE_LAYER_WORDS] per texture;
//   `kind_mask` bit k keeps kind k. `out_statements` is the run total of statements.
uint32_t refii_bridge_texture_layers_read(uint32_t entity, uint32_t* out, uint32_t capacity,
                                          uint32_t kind_mask, uint32_t* out_generation,
                                          uint64_t* out_statements);

// THE CLOTH A TITLE STATES (src/runtime/src/core/stated_cloth.cpp): each cloth piece's drawn
// vertices as its hook sees the guest's draw publish them, keyed by the piece (the guest's sim
// item), read back by its host bridge per entity.
// - a publish: the render instance's handle the piece belongs to, the record slot of its proxy in
//   that instance, flags (REFII_CLOTH_SKINNED: the instance's records are the skinned list), the
//   guest render frame it was published on, the guest frame (fractional, refii_bridge_guest_frame_at)
//   of the instant the guest posed it for, and `vertex_count` vertices of REFII_CLOTH_VERTEX_FLOATS
//   floats each — position xyz 1, normal xyz 0, tangent xyzw — in the model's own space, as the
//   guest writes its vertex buffer. The last REFII_CLOTH_POSES poses are kept, newest first; a
//   publish for the same instant replaces that pose, one for an earlier instant starts over.
// - a drop: the piece is gone (its sim item destroyed).
#define REFII_CLOTH_VERTEX_FLOATS 12u
#define REFII_CLOTH_MAX_VERTICES 8192u
#define REFII_CLOTH_SKINNED 1u
#define REFII_CLOTH_POSES 4u
void refii_bridge_cloth_published(uint32_t key, uint32_t handle, uint32_t slot, uint32_t flags,
                                  uint32_t frame, double pose_frame, const float* vertices,
                                  uint32_t vertex_count);
void refii_bridge_cloth_dropped(uint32_t key);
//   Read back: the pieces of any of `handles`, as rows of REFII_CLOTH_PIECE_WORDS words — key,
//   handle, slot, flags, vertex count, frame, sequence (moves per publish), the poses kept, 0 —
//   and one pose's vertices by `index` (0 the newest), with its guest frame, returning the vertex
//   count, 0 when the piece or pose is not there.
#define REFII_CLOTH_PIECE_WORDS 9u
uint32_t refii_bridge_cloth_pieces(const uint32_t* handles, uint32_t handle_count,
                                   uint32_t* out_words, uint32_t max_pieces);
uint32_t refii_bridge_cloth_vertices(uint32_t key, uint32_t index, float* out_floats,
                                     uint32_t max_vertices, double* out_pose_frame,
                                     uint32_t* out_sequence);
uint64_t refii_bridge_cloth_publish_count(void);

// THE CLASSIC PARTICLE STREAMS A TITLE STATES (src/runtime/src/core/stated_classic_particles.cpp):
// each particle group's stream as its hook sees the guest's own writer lay it into the frame's
// vertex buffer, keyed by the sim data it was written from, and the render object each sim data
// was last posted to; read back by its host bridge per render object.
// - written: `data` (the sim's particle data), its kind (1 quads, 2 trails, 3 double trails), the
//   writer's count n and ring count c (0 for quads), and `float_count` floats — quads n x 12 (the
//   sim's records), trails (c + 3) x 8 (the points the writer laid down). No floats: the writer
//   wrote nothing this frame, so the guest draws none.
// - bound: render object `id` was posted a message naming `data`.
// - removed: render object `id` is gone.
//   Read back: the bound ids, and per id meta = {kind, n, c, float count} with the floats when
//   they fit `capacity`; returns 1 when copied, 0 otherwise (meta filled whenever the id is known).
#define REFII_CLASSIC_PARTICLE_MAX_FLOATS (8192u * 12u)
void refii_bridge_classic_particle_written(uint32_t data, uint32_t kind, uint32_t n, uint32_t c,
                                           const float* floats, uint32_t float_count);
void refii_bridge_classic_particle_bound(uint32_t id, uint32_t data);
void refii_bridge_classic_particle_removed(uint32_t id);
uint32_t refii_bridge_classic_particle_ids(uint32_t* out_ids, uint32_t capacity);
int32_t refii_bridge_classic_particle_read(uint32_t id, uint32_t* out_meta, float* out,
                                           uint32_t capacity);

// THE WORLD ICONS AND CHOICE RECORDS A TITLE STATES (src/runtime/src/core/stated_world_icons.cpp),
// keyed by the guest's icon object and record, read back by its host bridge in the order first
// stated.
// - an icon stated: its Init, Update or ring tick returned. `visible` is the Update's own argument
//   (1 drawn, 0 hidden this tick), -1 to keep the last one (an Init or ring tick: not drawn until
//   an Update says so); an Update also takes the step last stated. Words: REFII_WORLD_ICON_CLASS_*,
//   type, texture id, colour 0xRRGGBBAA,
//   entity, flags (REFII_WORLD_ICON_*; bit 31 is the table's), ring progress 0..300 (Fable 2: a
//   pooled icon's slot, else 0xFFFFFFFF), player.
//   Values: anchor x y z, scale, seconds left, age, ring speed, 0. Text: name NUL title NUL
//   opinion, `text_length` bytes.
// - an icon removed: the registry erased it, or it was destroyed.
// - a step stated: the title's guest began HF step `step` (any increasing count), stated by its
//   tick pump before that step's registry tick.
// - a choice stated: created, or restated after the input handler. Words: action, player uid
//   words +8 and +12, entity, kind, flags (REFII_EMOTION_CHOICE_*). Values: position x y z, manual
//   speed. Text: Name NUL Text NUL RecordName.
// - a choice removed: its deleter ran.
//   Read back: rows of words, values and three NUL-terminated strings per entry, stopping at the
//   first entry whose strings do not fit `text_capacity` (a null `out_text` reads no strings and
//   never stops); returns the rows written. Any null out pointer skips that part. An icon's
//   REFII_WORLD_ICON_VISIBLE is set when an Update drew it in the step last stated or the one
//   before: the guest draws an icon only inside such an Update, and its tick runs none while a GUI
//   is up.
#define REFII_WORLD_ICON_WORDS 8u
#define REFII_WORLD_ICON_VALUES 8u
#define REFII_WORLD_ICON_FLAGS_WORD 5u
#define REFII_WORLD_ICON_CLASS_3D 1u
#define REFII_WORLD_ICON_CLASS_EMOTION 2u
#define REFII_WORLD_ICON_CLASS_HUD_ELEMENT 3u
#define REFII_WORLD_ICON_PERSISTENT 1u
#define REFII_WORLD_ICON_FADING 2u
#define REFII_WORLD_ICON_IMAGE_LIVE 4u
#define REFII_WORLD_ICON_RING_ON 8u
#define REFII_WORLD_ICON_RING_COMPLETE 16u
#define REFII_WORLD_ICON_RING_MANUAL 32u
#define REFII_WORLD_ICON_VISIBLE 0x80000000u
#define REFII_EMOTION_CHOICE_WORDS 6u
#define REFII_EMOTION_CHOICE_VALUES 4u
#define REFII_EMOTION_CHOICE_HOLDABLE 1u
#define REFII_EMOTION_CHOICE_MANUAL 2u
#define REFII_EMOTION_CHOICE_HELD 4u
void refii_bridge_world_icon_stated(uint32_t icon, int32_t visible, const uint32_t* words,
                                    const float* values, const char* text, uint32_t text_length);
void refii_bridge_world_icon_removed(uint32_t icon);
void refii_bridge_world_icons_step(uint64_t step);
uint32_t refii_bridge_world_icons_read(uint32_t* out_icons, uint32_t* out_words, float* out_values,
                                       char* out_text, uint32_t text_capacity, uint32_t max_icons);
void refii_bridge_emotion_choice_stated(uint32_t record, const uint32_t* words, const float* values,
                                        const char* text, uint32_t text_length);
void refii_bridge_emotion_choice_removed(uint32_t record);
uint32_t refii_bridge_emotion_choices_read(uint32_t* out_records, uint32_t* out_words,
                                           float* out_values, char* out_text,
                                           uint32_t text_capacity, uint32_t max_choices);

// THE HUD ELEMENT REGISTRIES AND THE SCREEN FADE A TITLE STATES (src/runtime/src/core/stated_hud.cpp).
// - an element filed: `kind` (the title's registry kind, REFII_HUD_KIND_*) and `registry` name the
//   registry, `hash` the name it is filed under, `source` the element it was filed from (0 when
//   none), `value` / `mode` its constructor's.
// - a source unfiled: the registry erases the name an element filed from `source` is under —
//   every element under it.
// - a registry destroyed: every name it holds leaves.
// - a text stated: every element under (registry, hash) shows `text` (UTF-8, `length` bytes).
// - a value stated: the element's value.
//   Read back: one row per name of `kind`, in the order first filed — a TEXT name only once a text
//   was stated for it — : the hash, the first element's value, mode and NUL-terminated text (cut at a UTF-8 boundary to fit; the strings stop once
//   `text_capacity` is full). Returns the names held, which may exceed `max_rows`.
// - a fade started: colours as the fader stores them (four bytes each), progress 0 or 1 (instant),
//   `rate` its progress per second. Run on the host's clock from the guest's first advance of it.
// - a fade stepped: the guest's own advance ran; `progress` after it, `advanced` whether it moved,
//   `locked` the lock it tested. A hold, a resume, the completion and a lock change rebase the
//   host's run on the guest's progress.
//   Read back: the colours, the progress now and the lock; returns the fader, 0 before any.
#define REFII_HUD_KIND_METER 1u
#define REFII_HUD_KIND_PULSER 4u
#define REFII_HUD_KIND_TEXT 6u
void refii_bridge_hud_element_filed(uint32_t kind, uint32_t registry, uint32_t hash, uint32_t element,
                                    uint32_t source, float value, uint32_t mode);
void refii_bridge_hud_source_unfiled(uint32_t registry, uint32_t source);
void refii_bridge_hud_registry_destroyed(uint32_t registry);
void refii_bridge_hud_text_stated(uint32_t registry, uint32_t hash, const char* text, uint32_t length);
void refii_bridge_hud_value_stated(uint32_t element, float value);
uint32_t refii_bridge_hud_read(uint32_t kind, uint32_t* out_hashes, float* out_values, uint32_t* out_modes,
                               char* out_text, uint32_t text_capacity, uint32_t max_rows);
void refii_bridge_screen_fade_started(uint32_t fader, const uint8_t* from, const uint8_t* to, float progress,
                                      float rate);
void refii_bridge_screen_fade_stepped(uint32_t fader, float progress, int32_t advanced, int32_t locked);
uint32_t refii_bridge_screen_fade_read(uint8_t* out_from, uint8_t* out_to, float* out_progress,
                                       uint32_t* out_locked);

// THE AIM RETICULES A TITLE STATES (src/runtime/src/core/stated_hud.cpp): one record per player
// (0, 1) and slot (0 spell target, 1 ranged aim), as its setter left it.
// - stated: `active`, and while active the values (REFII_RETICLE_VALUES: point x y in guest
//   pixels from the screen centre, y up; lock progress; RGBA as drawn; the world anchor x y z, 0
//   when none; the guest's backbuffer height) and words (REFII_RETICLE_WORDS: mode, frames since
//   the mode changed).
//   Read back: 1 and the values and words while the record is active, else 0.
#define REFII_RETICLE_VALUES 11u
#define REFII_RETICLE_WORDS 2u
void refii_bridge_reticle_stated(uint32_t player, uint32_t slot, int32_t active, const float* values,
                                 const uint32_t* words);
int32_t refii_bridge_reticle_read(uint32_t player, uint32_t slot, float* out_values, uint32_t* out_words);

// THE GAME STATE A TITLE STATES FOR THE SHELL (src/runtime/src/core/stated_game_state.cpp).
// THE LOADING SCREEN, one at a time:
// - created: `manager` made `screen` of `kind` (the title's numbering).
// - destroyed: `screen` is gone.
// - art: the screen decided its art, `name` (`length` bytes) held in `body`.
// - updated: the screen's own update ran: its cover opacity, stop requested, finished.
//   Read back: 1 while a screen is up, with the manager, screen, fade, requested, done, kind, body
//   and the NUL-terminated art name ("" before the decision; cut to fit).
void refii_bridge_loading_screen_created(uint32_t manager, uint32_t screen, uint32_t kind);
void refii_bridge_loading_screen_destroyed(uint32_t screen);
void refii_bridge_loading_screen_art(uint32_t screen, uint32_t kind, uint32_t body, const char* name,
                                     uint32_t length);
void refii_bridge_loading_screen_updated(uint32_t screen, float fade, int32_t requested, int32_t done);
int32_t refii_bridge_loading_screen_read(uint32_t* out_manager, uint32_t* out_screen, float* out_fade,
                                         uint32_t* out_requested, uint32_t* out_done, uint32_t* out_kind,
                                         uint32_t* out_body, char* out_name, uint32_t name_capacity);
// THE GAME MODE STACKS, keyed by the stack object: after a push, a pop or the stack's own run, its
// top mode, that mode's vtable and the depth (0 = empty: top and vtable 0).
//   Read back: 1 and the last statement for `stack`, 0 when it never stated.
// THE GUI SCREEN: the id the GUI's screen manager holds, stated by each of its writers.
//   Read back: 1 and the id once stated, 0 before.
void refii_bridge_mode_stack_stated(uint32_t stack, uint32_t top, uint32_t top_vtable, uint32_t depth);
int32_t refii_bridge_mode_stack_read(uint32_t stack, uint32_t* out_top, uint32_t* out_vtable, uint32_t* out_depth);
void refii_bridge_gui_screen_stated(uint32_t screen_id);
int32_t refii_bridge_gui_screen_read(uint32_t* out_screen_id);
// THE CULTURES: the installed culture table as its builder left it — `count` rows, `codes` and
// `names` each `count` NUL-terminated strings back to back (`codes_length` / `names_length`
// bytes; codes ASCII, names UTF-8), `current` the row in use, -1 for none.
// - selected: the row now in use.
//   Read back: the count (0 before the table is stated), the current row (-1 for none) and the
//   names NUL-separated (stopping at the first that does not fit); the code of the current row,
//   returning its length, 0 when there is none or it does not fit.
void refii_bridge_cultures_stated(uint32_t count, int32_t current, const char* codes, uint32_t codes_length,
                                  const char* names, uint32_t names_length);
void refii_bridge_culture_selected(uint32_t index);
uint32_t refii_bridge_cultures_read(int32_t* out_current, char* out_names, uint32_t names_capacity);
uint32_t refii_bridge_culture_code_read(char* out, uint32_t capacity);

// THE OPTIONS A TITLE STATES (src/runtime/src/core/stated_options.cpp): one value per option, by
// REFII_OPTION_*, from two kinds of writer:
// - applied: an applier the running game uses was called with it (a volume setter's argument,
//   the gamma applier's input);
// - profiled: the player's saved settings were read or written with it (the boot load, a save).
//   Read back: each option's applied value, else its profiled one, into `out_values` (up to
//   `capacity`); returns the mask of options stated either way (bit = option).
#define REFII_OPTION_GAMMA 0u
#define REFII_OPTION_SFX 1u
#define REFII_OPTION_MUSIC 2u
#define REFII_OPTION_VOICE 3u
#define REFII_OPTION_SPEAKER 4u
#define REFII_OPTION_SUBTITLES 5u
#define REFII_OPTION_COUNT 6u
void refii_bridge_option_applied(uint32_t option, float value);
void refii_bridge_option_profiled(uint32_t option, float value);
uint32_t refii_bridge_options_read(float* out_values, uint32_t capacity);

#ifdef __cplusplus
}  // extern "C"
#endif
