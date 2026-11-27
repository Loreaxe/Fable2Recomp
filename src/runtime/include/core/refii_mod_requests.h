// refii — the contract of a mod's requests for Godot, shared by every title (implemented in
// src/payload/src/core/mod_requests.cpp, compiled into every title's payload): the options an effect
// and a light take, and the C entries that post effects, lights, boxes, notices, menus, marks,
// meters, fades, HUD and input requests and menu rows on the bus's host-only kinds. Godot acts on
// them (Engine_FxPlayer, Engine_ModLights, Engine_UiModBoxes, Engine_UiModRows); none reads the
// guest. Fable 2's core/refii_mod.h includes this; a Fable 3 native calls the same entries.

#pragma once

#include <cstdint>

/// The host-only kinds these requests cross under (never bus events; posted whatever the mask says).
#ifndef REFII_MOD_HOST_FX_KIND
#define REFII_MOD_HOST_FX_KIND 1001
#endif
#ifndef REFII_MOD_HOST_UI_KIND
#define REFII_MOD_HOST_UI_KIND 1002
#endif
#ifndef REFII_MOD_HOST_MENU_KIND
#define REFII_MOD_HOST_MENU_KIND 1003
#endif

/// The most bytes of rows refii_mod_set_menu_rows carries; past it the rows are cut at the last whole row.
#define REFII_MOD_MENU_ROWS_MAX 32768

/// How a played effect is shown. Null options mean refii_mod_fx_default_options().
typedef struct refii_mod_fx_options {
  /// Seconds before it is stopped; 0 plays until refii_mod_stop_fx (or, with loop 0, until the
  /// effect has run out).
  float duration;
  /// A size multiplier on every position, motion and size of the effect, as a node's scale is.
  float scale;
  /// A colour multiplier on every particle (1, 1, 1 untinted).
  float tint[3];
  /// The instance intensity, an alpha term (1 as authored).
  float intensity;
  /// 1 repeats the effect, 0 plays it once.
  int32_t loop;
  /// From the entity (refii_mod_play_fx_on), along the ENTITY'S OWN axes: an effect on an entity
  /// stands in its frame — the effect's axes are the entity's — and turns with it. An entity with
  /// no drawn body of its own (a vortex, a fireball's shot) is followed by position only, the
  /// effect unrotated and the offset in the guest's world axes.
  float offset[3];
  /// A HELPER POINT of the entity's model (refii_mod_play_fx_on): the name of a `.mdl.gmd`
  /// attachment or node — a sword's "Weapon.Weapon.Top" / "Weapon.Weapon.Hold", a creature's
  /// "Character.Carry.Hand.Right" — and the effect stands there, in the helper's own frame (its
  /// position AND orientation when the gmd states one), `offset` along the helper's axes; on a
  /// rigged body it hangs on the bone the helper hangs from, so it follows the pose. Null or ""
  /// is the entity's own frame. An unknown name is warned once and the entity's frame used.
  /// Under the classic look the helper's REST-pose point is folded into the offset.
  const char* at;
} refii_mod_fx_options;

inline refii_mod_fx_options refii_mod_fx_default_options(void) {
  refii_mod_fx_options options{};
  options.scale = 1.0f;
  options.tint[0] = options.tint[1] = options.tint[2] = 1.0f;
  options.intensity = 1.0f;
  options.loop = 1;
  return options;
}

/// The options of refii_mod_light. Zero-initialise and set what you need.
typedef struct refii_mod_light_options {
  /// From the entity, along its own axes (or the helper's when `at` names one), as
  /// refii_mod_fx_options.offset; ignored for a world point.
  float offset[3];
  /// A helper point of the entity's model, as refii_mod_fx_options.at ("Weapon.Weapon.Top"): the
  /// light stands there, on a rigged body on the helper's bone. Null or "" is the entity's frame.
  const char* at;
  /// 0..1: a waver of the energy (a torch, a brazier); 0 is steady.
  float flicker;
  /// A SPOT LIGHT when `cone` is above 0: the light shines along `direction` — in the entity's own
  /// axes (or the helper's when `at` names one), the world's for a world point; any length — over a
  /// cone `cone` degrees from its axis to its edge (the half-angle; at most 89). Zero: an omni light.
  float direction[3];
  float cone;
  /// The cone's edge falloff, a spot's only: 1 (and 0) is linear from the edge to the axis — the
  /// title's own spot form, 1/(1-cos) — higher tightens the beam. Modern only; the classic list
  /// has the one form.
  float attenuation;
} refii_mod_light_options;

extern "C" uint32_t refii_mod_play_fx(const char* name, const float* xyz, const refii_mod_fx_options* options);
extern "C" uint32_t refii_mod_play_fx_on(const char* name, uint32_t entity, const refii_mod_fx_options* options);
extern "C" uint32_t refii_mod_play_fx_handle(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options);
extern "C" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper, const refii_mod_fx_options* options);
extern "C" void refii_mod_stop_fx(uint32_t id);
extern "C" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range, const refii_mod_light_options* options);
extern "C" uint32_t refii_mod_light_handle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range, const refii_mod_light_options* options);
extern "C" void refii_mod_light_remove(uint32_t id);
extern "C" int32_t refii_mod_open_menu(int32_t player, const char* name);
extern "C" uint32_t refii_mod_message_box(int32_t player, const char* title, const char* text, const char* accept, const char* cancel);
extern "C" uint32_t refii_mod_notice(int32_t player, const char* title, const char* text, float seconds);
extern "C" int32_t refii_mod_text(const char* text, char* out, uint32_t capacity);
extern "C" uint32_t refii_mod_menu_box(int32_t player, const char* title, const char* text, const char* const* options, int32_t count, int32_t cancelable);
extern "C" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon, const char* text);
extern "C" void refii_mod_unmark(uint32_t id);
extern "C" uint32_t refii_mod_world_meter(int32_t player, uint32_t entity, const float* xyz, float value, float max, const char* style);
extern "C" int32_t refii_mod_mark_meter(uint32_t id, float value, float max, const char* style);
extern "C" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds);
extern "C" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max, const char* style);
extern "C" int32_t refii_mod_meter_remove(const char* id);
extern "C" int32_t refii_mod_input_claim(int32_t player, const char* action, int32_t on);
extern "C" int32_t refii_mod_hud_icon(int32_t player, const char* element, const char* texture);
extern "C" int32_t refii_mod_hud_slide(int32_t player, const char* element, int32_t slide);
extern "C" int32_t refii_mod_hud_hold(int32_t player, const char* piece, int32_t on);
extern "C" int32_t refii_mod_hide_part_for(uint32_t entity, const char* part, int32_t on, int32_t player);
extern "C" int32_t refii_mod_hide_part(uint32_t entity, const char* part, int32_t on);
extern "C" int32_t refii_mod_press(int32_t player, const char* button, int32_t held);
extern "C" int32_t refii_mod_tap(int32_t player, const char* button, int32_t ticks);
extern "C" int32_t refii_mod_set_menu_rows(int32_t player, const char* menu, const char* rows);
extern "C" int32_t refii_mod_open_screen(int32_t player, uint32_t screen);

// The mod lane's partner messages, signals and published texts (moved from Fable 2's mod_api.cpp; both titles
// post through these).
#ifndef REFII_MOD_PARTNER_PAYLOAD_MAX
#define REFII_MOD_PARTNER_PAYLOAD_MAX 256
#define REFII_MOD_PARTNER_NAME_MAX 32
#endif
#ifndef REFII_MOD_SIGNAL_PAYLOAD_MAX
#define REFII_MOD_SIGNAL_PAYLOAD_MAX 1024
#define REFII_MOD_SIGNAL_NAME_MAX 96
#endif
/// A partner message's name and mod: letters, digits, '_', '.', '-', at most REFII_MOD_PARTNER_NAME_MAX;
/// its payload any text up to REFII_MOD_PARTNER_PAYLOAD_MAX bytes.
/// A signal's or published text's payload, and its scoped "<mod>.<name>".

/// A mod's message to THE OTHER MACHINE of an online session, raised there as partner_message
/// (mod, name, payload, from: the player this machine plays). 0 outside an online session (owned is
/// neither 1 nor 2), for a bad word or an over-long payload. Any thread.
extern "C" int32_t refii_mod_send_to_partner(const char* mod, const char* name, const char* payload);
/// "<mod>.<name>" and its payload to Godot on this machine (RefiiHost.mod_signal). 1 when posted.
extern "C" int32_t refii_mod_signal(const char* name, const char* payload);
/// A text Godot reads with get_text(key), a change raising value_changed. 1 when posted.
extern "C" int32_t refii_mod_publish_text(const char* key, const char* text);


// ---------------------------------------------------------------------------------------------
// PLAYER STATE: a mod's values for one player, right on both co-op machines.
// ---------------------------------------------------------------------------------------------
//
// Keep anything that belongs to a player here rather than in a mod's own array: a pool, a level,
// a cooldown. `player` is 1 or 2, `key` "<mod>.<name>". The values are THE SAME ON BOTH MACHINES
// of an online session: each player's are owned by the machine that plays that hero, and a write
// on either machine reaches the owner (an add merges, so a spend on the host and a refill on the
// joiner both count). SAVED values go home with the hero: they are written beside the save the
// game writes that hero into — player one's main save, a local player two's own save, an online
// joiner's own save on the joiner's machine — and come back when the game loads the hero from it.
// So a mod written as if for one player is right for both, in every co-op mode.

/// The value, or `fallback` when the player has none under `key`.
extern "C" float refii_mod_player_value(int32_t player, const char* key, float fallback);
/// Whether the player has a value under `key` (written to `out` when not null).
extern "C" int32_t refii_mod_player_get(int32_t player, const char* key, float* out);
/// Sets a session value (kept while the hero is in play, never saved).
extern "C" void refii_mod_player_set(int32_t player, const char* key, float value);
/// Adds to a value (a missing one counts as 0). `saved` keeps the key with the hero's save.
extern "C" void refii_mod_player_add(int32_t player, const char* key, float delta, int32_t saved);
/// Sets a SAVED value: kept with the hero's own save and back when it loads.
extern "C" void refii_mod_player_save(int32_t player, const char* key, float value);

/// TYPED VALUES in the same store, under the same keys and owners: a TEXT (at most
/// REFII_MOD_PLAYER_TEXT_MAX bytes) and a HASH, kept exactly (a 32-bit record hash does not survive a
/// float). A key holds one type: a number written over a text replaces it and the other way round;
/// the number readers answer 0 / the fallback for a text key. A hash is stored as the text "0x%08x",
/// so either reader reads it.
#define REFII_MOD_PLAYER_TEXT_MAX 256
/// The text under `key` into `out` (NUL-terminated, cut to `capacity`); its length, -1 when the
/// player has no text there.
extern "C" int32_t refii_mod_player_text(int32_t player, const char* key, char* out, uint32_t capacity);
/// Sets a session text (null clears to ""). Longer than REFII_MOD_PLAYER_TEXT_MAX is cut.
extern "C" void refii_mod_player_set_text(int32_t player, const char* key, const char* text);
/// Sets a SAVED text: kept with the hero's own save and back when it loads.
extern "C" void refii_mod_player_save_text(int32_t player, const char* key, const char* text);
/// The hash under `key` (a text that reads as a "0x…" or decimal number) into `out`; 1 when there is one.
extern "C" int32_t refii_mod_player_hash(int32_t player, const char* key, uint32_t* out);
/// Sets a SAVED hash (stored as the text "0x%08x").
extern "C" void refii_mod_player_save_hash(int32_t player, const char* key, uint32_t hash);
/// Whether this machine plays that player's hero (both outside an online session; the host plays
/// player one and the joiner player two inside one). Work that runs on its own every tick for a
/// player — a refill, a timer — runs where the hero is played, so it runs once per session.
extern "C" int32_t refii_mod_player_here(int32_t player);
/// Whether the event being handled is the game's COPY of something the other co-op machine
/// decides — a cloned action's animation event, a replicated cast's charge or launch, a hit
/// response predicted for a body played elsewhere, a received experience change. The game runs
/// those on both machines, so a handler runs on both: its verdict (cancel, a written field) applies
/// on both, from the same player values, but PLAYER-STATE WRITES ARE DROPPED during the copy — the
/// deciding machine makes them, so a spend counts once. A handler with an effect on the body itself
/// (holding the hero in place) skips it during the copy; nothing else needs this.
///
/// RELAYED events are copies too. An event the game raises on one machine only, but whose effect
/// belongs on the other — a swing, a roll or an item use where that hero is played (the host's AI
/// must see player two's), a hit landing on the host (the joiner's looks follow its victims) — is
/// sent by refii over the co-op channel and dispatched on the other machine as a replica, its
/// actor that machine's copy of the hero (or the entity of the same UniqueID). Its objects of the
/// deciding machine's own (a hit record, an ability, an action, an item) are 0, cancel and writes
/// do nothing (it already happened), and it arrives a network moment later. The catalog marks
/// each relayed event; a handler that makes a world or body effect skips the copy.
extern "C" int32_t refii_mod_event_replica(void);

/// LUA AND GODOT SEE HANDLES, NOT ADDRESSES. The game's Lua number is a 32-bit float, so a
/// guest address above 2^24 is not exact there; every entity or object the bus hands to Lua or
/// to a Godot script is a HANDLE: a small integer assigned to that address for the session (0 for
/// 0). These convert both ways; a native mod needs them only to talk to those two sides.
extern "C" uint32_t refii_mod_handle(uint32_t address);
extern "C" uint32_t refii_mod_handle_address(uint32_t handle);

/// The game's own 32-bit FNV-1 of a name (basis 0x811C9DC5): a GDB record's hash from its name,
/// a text tag's hash as the TextManager keys it.
extern "C" uint32_t refii_mod_fnv1(const char* text);


// ---------------------------------------------------------------------------------------------
// THE GUEST TICK AND THE GAME VM, for native mods on every title.
// ---------------------------------------------------------------------------------------------
struct PPCContext;

/// A mod's per-tick callback: the guest's context (a copy, safe to use for guest calls), guest
/// memory, and the real seconds since the last tick (capped at a quarter second across a hitch).
/// Runs on the guest tick thread every game tick, menus included.
typedef void (*refii_mod_tick_fn)(PPCContext* ctx, uint8_t* base, float seconds);

extern "C" void refii_mod_register_tick(refii_mod_tick_fn fn);

/// Registers `fn` (a refii_mod_tick_fn) when the game module loads:
///   static void Tick(PPCContext* ctx, uint8_t* base, float seconds) { ... }
///   REFII_MOD_ON_TICK(Tick);
#define REFII_MOD_ON_TICK(fn) \
  static const int refii_mod_on_tick_##fn = (refii_mod_register_tick(&fn), 0)

/// Runs every registered tick callback with `seconds` (the title's guest tick calls it once a tick).
void refii_mod_run_ticks(PPCContext* ctx, uint8_t* base, float seconds);

/// Runs a Lua source chunk of any length in the GAME VM — the shipped game scripts' and
/// mods' Lua. True when it ran without error. Guest tick thread only (a tick callback or a hook).
extern "C" int32_t refii_mod_run_game_lua(PPCContext* ctx, uint8_t* base, const char* chunk);

/// STATES ONE VALUE OF `player`'s CAMERA (1 or 2) for the mods: the camera mod API's shape calls
/// (camera_tune, camera_frame, camera_focus, camera_combat_blend, camera_interest, camera_pitch_hold,
/// camera_lock_frame, camera_mode_allow), the display offset (camera_offset) and the game's own screen shake
/// (camera_shake: "{kind='quake',level=0.6,seconds=0.4}"), made in the game VM as `Refii.<call>(player, <arguments>)`
/// with the owner "native". `call` is the call's name; `arguments` what follows the player, written as
/// the Lua call writes it ("{pitch_speed=0.09}", "0", "true,{shared=true}", "nil" to let it go; empty
/// or null for none). What is stated is kept in the game VM as plain values, applied by the title's
/// core camera chunk where its camera scripts read their own, and never reaches a save. 1 when the
/// call ran. Guest tick thread only.
extern "C" int32_t refii_mod_camera_state(PPCContext* ctx, uint8_t* base, int32_t player, const char* call,
                                          const char* arguments);

/// THE CAMERA'S READ CALLS, the same values Lua's Refii.camera_* and Godot's RefiiHost.camera_* answer
/// (src/runtime/include/core/camera_views.h). Guest axes; angles in degrees. Any thread.
/// The REFII camera screens camera_screen_mode answers.
#define REFII_CAMERA_SCREEN_SHARED 0
#define REFII_CAMERA_SCREEN_SPLIT_VERTICAL 1
#define REFII_CAMERA_SCREEN_SPLIT_HORIZONTAL 2
#define REFII_CAMERA_SCREEN_HYBRID 3
/// The pose `player`'s picture is drawn with this frame: position, forward, up, fov (horizontal), vfov
/// (the drawn view's vertical angle), cut (1 / 0). 0 when no picture of that player is drawn here.
extern "C" int32_t refii_mod_camera_pose(int32_t player, float* out12);
/// The block the game's readers see for `player` at its last tick (its camera manager's getter; the
/// shared camera's on a shared screen): position, forward, up, fov (horizontal), cut. 0 before one.
extern "C" int32_t refii_mod_camera_stated(int32_t player, float* out11);
/// The part of the window `player`'s picture fills: x, y, width, height (0..1 from the top left).
extern "C" int32_t refii_mod_camera_view(int32_t player, float* out4);
/// The co-op screen chosen (REFII_CAMERA_SCREEN_*), -1 before the shell stated one; `split` (may be
/// null) 1 while two views are drawn.
extern "C" int32_t refii_mod_camera_screen_mode(int32_t* split);
/// The line through point (x, y) of `player`'s view (each 0..1 from its top left; 0.5, 0.5 the
/// centre), from the drawn camera: origin, then the unit direction. 0 when not drawn here.
extern "C" int32_t refii_mod_camera_ray(int32_t player, float x, float y, float* out6);

/// The integer value of the Lua expression `expr` in [lo, hi], by a binary search of assertions —
/// a handful of chunk runs, so read it when it can have changed (see refii_mod_lua_generation),
/// not every tick. False when the expression is not a number in that range.
extern "C" int32_t refii_mod_game_lua_int(PPCContext* ctx, uint8_t* base, const char* expr,
                                          int32_t lo, int32_t hi, int32_t* out);

/// Changes every time the game's Lua state is rebuilt (a new session's startup scripts) or a
/// save's Lua state is loaded into it. A mod keeping state in the save re-reads it on a change.
extern "C" uint32_t refii_mod_lua_generation(void);

/// Writes `RefiiModSave.<key> = value` in the game VM: an integer that persists with the save
/// game (Engine_ModLua's preamble carries the table through GetSaveTable / LoadScriptSave).
/// `key` is a Lua identifier. True when written. Guest tick thread only.
extern "C" int32_t refii_mod_save_set_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t value);

/// Reads `RefiiModSave.<key>` in [lo, hi] (a binary search of chunk runs — read it on a
/// refii_mod_lua_generation change, not every tick). False when absent or out of range.
extern "C" int32_t refii_mod_save_get_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t lo, int32_t hi,
                                          int32_t* out);

/// MODS' OWN MORPH TYPES. The weight function `sub_822DA028(CECMorph, type)` weighs types 1..18 and
/// answers 0 for 11, 13 and every type at or above 19 (its bound, TU1 0x822DA274..7C); the vertex,
/// texture, skeletal and particle readers pass a record's `MorphType` to it unchecked. A mod declares
/// morphs in `morphs.json` (name, label, default, min, max, step); each one's type is
/// refii_mod_morph_id("<mod>.<name>"), and a GDB record carrying that type — a MorphTargets pair, a
/// TextureMorphs layer, a SkeletalMorphs item, a ParticleMorphs item — is drawn at the player's weight
/// for it, which the morph hook answers for either player's hero, manual look or automatic.
/// The weights are the player store's `morph.<id>` (id in decimal), saved with the hero's save and
/// the same on both co-op machines; the registry (id, default, range) is what Godot read from the
/// enabled mods.
/// The type of a morph named "<mod>.<name>": FNV-1 of the name in lower case, the top bit cleared,
/// plus 0x100 when under it (never a shipped or special type).
extern "C" uint32_t refii_mod_morph_id(const char* scoped);
/// The registered mod morphs' ids into `out` (up to `capacity`), in the mods' load order; the count.
extern "C" int32_t refii_mod_morph_list(uint32_t* out, int32_t capacity);
/// Whether `id` is a registered mod morph; its default and range into the outputs that are not null.
extern "C" int32_t refii_mod_morph_registered(uint32_t id, float* out_default, float* out_min, float* out_max);
/// Player `player`'s weight for mod morph `id` into `out`: the stored weight clamped to the range,
/// else the default. False for an unregistered id or an invalid player.
extern "C" int32_t refii_mod_morph_weight(int32_t player, uint32_t id, float* out);
/// Sets player `player`'s weight for mod morph `id`, clamped to its range and saved with the hero. A
/// write on the machine that does not play the hero reaches the one that does. False for an
/// unregistered id or an invalid player. The morph hook redraws that hero on its next tick and
/// raises MorphChanged.
extern "C" int32_t refii_mod_set_morph_weight(int32_t player, uint32_t id, float weight);
