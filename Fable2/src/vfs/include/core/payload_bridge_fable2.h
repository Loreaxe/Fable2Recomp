// refii — Fable 2's entries of the payload/host C ABI.
//
// The contract is core/payload_bridge.h's, which this includes: C linkage, explicit-width
// scalars, no ownership crossing. These are the entries whose state is Fable 2's (refii_vfs):
// its movies, effects, appearances, menus, co-op screens and sampled pages. The shared entries
// (voice capture, the tick queue, mods, player state, the relay, consent, names, the mouse's
// turn, host assets) are in that header and their state in refii_runtime.

#pragma once

#include "core/payload_bridge.h"

#ifdef __cplusplus
extern "C" {
#endif

void refii_bridge_new_game_result(uint32_t success);
void refii_bridge_new_game_generation(void);

// Bink movie state. Wrappers: refii::vfs::NoteBink* in vfs_hooks_private.h.
void refii_bridge_note_bink_movie_open(uint32_t player, uint8_t* base_for_open);
void refii_bridge_note_bink_movie_closed(uint32_t player);
void refii_bridge_note_bink_movie_rect(uint32_t player, int32_t left, int32_t top, int32_t right,
                                       int32_t bottom);
void refii_bridge_note_bink_video_requested(uint8_t* base, uint32_t data_guest);
void refii_bridge_note_bink_sound(uint8_t* base, uint32_t player, uint32_t sound);
void refii_bridge_note_bink_volume(uint32_t player, uint32_t track_id, float volume);

/// The front end's subtitles setting as the guest last stated it: 1 on, 0 off, -1 unknown.
void refii_bridge_front_end_note_subtitles(int32_t state);

/// Which pause tabs the guest's own predicate (`sub_826B83D0`) answered yes for, bit N = tab N
/// for N in 2..8. The predicate runs in the payload, on the guest thread; the answer is read by
/// the host bridge, so it has to cross here rather than by C++ symbol.
void refii_bridge_note_pause_tabs(uint32_t mask);

/// The pause minimap's quest markers as the guest states them (hooks/gui_pause_hooks.cpp): the
/// pass begins (the table empties), then one note per marker — `token` 0 Quest, 1 QuestExit,
/// 2 QuestGiver (`Foreground.ReusableTokens.<name>`), and the world point it projects.
void refii_bridge_pause_markers_begin(void);
void refii_bridge_pause_marker_note(uint32_t token, float x, float y, float z);

// The effect transforms the guest states, recorded per PSInstance. Wrappers:
// refii::vfs::NoteFx* in vfs_hooks_private.h.
//
// The payload's two setter hooks write and the host bridge reads, so the table lives in
// refii_vfs (src/core/stated_fx_state.cpp) and crosses here. Four lanes each because which
// lane holds x depends on the recompiler's register layout; the host decides.
void refii_bridge_note_fx_position(uint32_t instance, const float* lanes);
void refii_bridge_note_fx_orientation(uint32_t instance, const uint32_t* lanes);

// The targeting highlight colour the guest states. Wrappers: refii::vfs::NoteHighlight* in
// vfs_hooks_private.h. Same direction of travel, same reason — the table is in refii_vfs
// (src/core/target_highlight_state.cpp). RGBA packed big-endian, R in the top byte.
void refii_bridge_note_highlight_colour(uint32_t component, uint32_t rgba);
void refii_bridge_note_highlight_target(void);

// Each renderable's stated appearance. Wrappers: refii::vfs::NoteAppearance / ForgetAppearance
// in vfs_hooks_private.h; the table is in refii_vfs (src/core/entity_appearance_state.cpp).
// Words, as the guest keeps them on CECGraphicAppearance (offsets from the component):
//   0 alpha +112, 1 camera alpha +116, 2 scale +108 (float bits),
//   3..6 tint +80, +84, +88, +92 (float bits, memory order),
//   7 flags: kAppearanceDrawable = +197 non-zero, kAppearanceSaturation = +196 non-zero,
//     kAppearanceNoAlphaDepth = +192 non-zero (the definition's DisableAlphaDepthWrites, vf8
//     `sub_825720A8` 0x825721b8 `stb r3, 0xC0(r31)`; disc `sub_82546D50` 0x82546e60).
enum {
  kAppearanceWords = 8,
  kAppearanceFlags = 7,
  kAppearanceDrawable = 1,
  kAppearanceSaturation = 2,
  kAppearanceNoAlphaDepth = 4,
};
void refii_bridge_note_appearance(uint32_t component, const uint32_t* words);
void refii_bridge_forget_appearance(uint32_t component);

// Each character part's morph targets as the guest states them to `sub_82A48B90`: `pairs` is
// `count` x {target model hash, weight float bits}; count 0 states the part unmorphed.
// Wrapper: refii::vfs::NotePartMorph; table in src/core/part_morph_state.cpp.
enum {
  kPartMorphParts = 24,
  // The guest bounds neither list (`sub_8220DE88` passes every layer it sorted; a part counts
  // every target above 1e-4); these are capacities, and a clamp counts what it dropped.
  kPartMorphTargets = 16,
};
void refii_bridge_note_part_morph(uint32_t entity, uint32_t base_model, const uint32_t* pairs,
                                  uint32_t count);
// The entity left the world; its parts are dropped.
void refii_bridge_forget_part_morphs(uint32_t entity);

// Which of the hero's garments are dyed: the guest's own `sub_826CAFA8` per slot, sampled on
// the GUI tick, ONE ENTRY PER ITEM INSTANCE — the dye is the instance's type-2 record, so two
// copies of one record are dyed apart. kDyeStateWords words per item: 0 the item (guest
// address, the instance), 1 its record hash, 2 flags (bit 0 slot 1 dyed, bit 1 slot 2 dyed,
// bit 2 the item is worn). Worn items first, then the inventory in list order. Table in
// src/core/dye_state.cpp.
enum {
  kDyeStateWords = 3,
  kDyeStateItems = 160,
};
void refii_bridge_note_dye_states(const uint32_t* words, uint32_t count);

// Each character's texture work as the guest states it. Wrappers: refii::vfs::BeginTextureLayers
// / NoteTextureLayers; table in src/core/texture_layer_state.cpp. A layer is kTextureLayerWords
// words: 0 texture id, 1 blend mode (0 opaque, 1 alpha, 2 additive, 3 blend-factor, 4 masked),
// 2 weight (float bits), 3 has-tint, 4 tint bytes B G R A (memory order) — an overlay's spec
// texture id instead —, 5 brightness (float bits), 6 bump texture id (overlays only).
// `kind` 0 is a composite stated to `sub_825B38B0`, layer 0 the base. `kind` 1 is the OVERLAY
// list sent as command type 14 through `sub_8222AAC8`: scars and blend-mode-0 morphs, drawn
// over the finished material; texture id is the overlay's diffuse.
// `kind` 2 is a WHOLE-ENTITY SWAP (`sub_825B7100`, the shadow creature's and component 192's):
// original 0 every diffuse map, 1 every specular map, 2 every normal map; one layer, the
// replacement at weight 1.
// `begin` drops what the entity stated before.
enum {
  kTextureLayerWords = 7,
  kTextureLayersPerTexture = 16,
  kTextureLayerTextures = 40,
};
void refii_bridge_begin_texture_layers(uint32_t entity);
// Publishes the statements since the begin as the entity's layers, in one step.
void refii_bridge_end_texture_layers(uint32_t entity);
// The entity left the world; its layers are dropped.
void refii_bridge_forget_texture_layers(uint32_t entity);
void refii_bridge_note_texture_layers(uint32_t entity, uint32_t original, uint32_t kind,
                                      const uint32_t* layers, uint32_t count);

// The decals the guest states (Fable2/src/vfs/src/hooks/decals.cpp, table in src/core/decal_state.cpp): a ring
// of 50, the oldest replaced. kDecalWords words each: 0 serial (the host's), 1 owner render
// twin or 0, 2 texture name hash, 3..6 position, 7..10 direction (float bits, guest world),
// 11 / 12 half sizes, 13 life, 14 flag.
enum {
  kDecalWords = 15,
};
void refii_bridge_note_decal(const uint32_t* words);
void refii_bridge_clear_decals(void);
void refii_bridge_drop_decals_of(uint32_t owner);

// The FX mesh particle descriptors the guest's effects state (Fable2/src/vfs/src/hooks/fx_meshes.cpp, table in
// src/core/fx_mesh_state.cpp): noted at `sub_82456D58`, dropped when their twin is destroyed.
void refii_bridge_note_fx_mesh(uint32_t descriptor);
void refii_bridge_drop_fx_mesh(uint32_t descriptor);
uint32_t refii_bridge_fx_meshes(uint32_t* out, uint32_t capacity);
// The ALPHA (model command 16) and TINT (model command 17) the guest states to one render twin,
// taken where the command reaches the twin (`Render_ApplyCommandToHandle(handle, command)`, same
// file); forgotten when the twin is destroyed. `alpha_bits` float bits; `tint` 4 lanes of float bits.
void refii_bridge_note_twin_alpha(uint32_t handle, uint32_t alpha_bits);
void refii_bridge_note_twin_tint(uint32_t handle, const uint32_t* tint);
void refii_bridge_forget_twin(uint32_t handle);

// The layers the guest's own dye builder states for ONE inventory item INSTANCE, keyed by the
// item's guest address (the instance its type-2 dye record hangs off) and written as a
// character's composites are (kTextureLayerWords per layer); the menu previews a dyed garment
// through them. Sampled on the GUI tick for every dyed item the hero wears or carries. Table in
// src/core/item_dye_layer_state.cpp.
enum {
  kItemDyeLayerItems = 64,
  kItemDyeLayerWords = 4 * (2 + kTextureLayersPerTexture * kTextureLayerWords),
};
void refii_bridge_set_item_dye_layers(const uint32_t* items, const uint32_t* offsets,
                                      const uint32_t* counts, uint32_t item_count,
                                      const uint32_t* words);

// The pub-game seam. The host claims the guest's pub-game module and posts the fscommands
// Godot's game issues; the pump replacement (Fable2/src/vfs/src/hooks/pubgame_pump.cpp, on the guest's render
// thread) drains them into the guest's own handlers and reports back what the movie would have
// been told. State in src/core/pubgame_state.cpp. `numbers` is 3 doubles, `integers` 2.
int32_t refii_bridge_pubgame_claimed(void);
int32_t refii_bridge_pubgame_take_command(uint32_t* kind, char* text, size_t text_capacity,
                                          double* numbers, int64_t* integers);
void refii_bridge_pubgame_note_event(uint32_t bits);

// Keyed form. The capture is ONE list, so without a key a template sourced `capture:Root`
// takes whatever was spawned last, whoever spawned it — the front end's rows once appeared on
// the real-estate screen. `screen_id` is `manager+4` off the in-game GUI, read at the
// boundary; 0xFFFFFFFF means "no in-game screen states this population" (the front end).
void refii_bridge_menu_spawn_begin_for(uint32_t screen_id);

// Labelled form: String2 (display text) decoded from UTF-16BE at capture.
void refii_bridge_menu_spawn_row_labelled(const char* name, const char* parent, uint32_t id,
                                          int32_t event_type, int32_t rating,
                                          const char* label);

void refii_bridge_guest_tick_pump_note_result(uint32_t addr, uint64_t r3,
                                              const char* note, size_t length);

// Menu capture settle bracket: `spawn_begin` marks the rows partial, `_settle` clears it.
void refii_bridge_menu_settle(void);
// The open GUI's owner: 1 when PLAYER TWO owns it (the guest's `sub_8236E400`), else 0. Set by the
// payload once per guest tick, read by the host bridge (RefiiHostGetMenuOwner).
void refii_bridge_set_menu_owner_two(int32_t two);
int32_t refii_bridge_menu_owner_two(void);
// The player whose expression wheel is open (the record id its open stated), 0 for none: set and
// cleared by the open / close hooks (mod_events.cpp), read by the host
// (RefiiHostGetExpressionWheelPlayer).
void refii_bridge_set_expression_wheel_player(int32_t player);
int32_t refii_bridge_expression_wheel_player(void);
// A region exit refused `player` (1 or 2) because their partner was out of range (the travel
// rule, coop_hero_hooks.cpp). Once per approach; read-and-cleared by the host
// (RefiiHostTakeExitRefused) as a mask, bit 0 player one, bit 1 player two.
void refii_bridge_note_exit_refused(int32_t player);
int32_t refii_bridge_take_exit_refused(void);
// The things whose use prompt answers player two (coop_hero_hooks.cpp), published by the payload
// once a frame; the host copies them out (RefiiHostGetPlayerTwoPrompts). Returns the count.
// The things whose use prompt shows this frame and which players see it (bit 0 player one, bit 1
// player two: the guest's own in-range test run for each), published by coop_hero_hooks.cpp.
void refii_bridge_set_prompt_masks(const uint32_t* things, const uint8_t* masks, int32_t count);
// Online: this machine's player stands waiting at a region exit for their partner (the travel
// rule). Set by the payload once a frame; the host restates it to the peer (RefiiHostGetExitWaiting).
void refii_bridge_set_exit_waiting(int32_t waiting);
// The online joiner used a region exit at this guest position (coop_hero_hooks.cpp, the exit's
// fire on a joiner). Each use bumps the sequence; the host restates the newest to the peer
// (RefiiHostGetExitRequest), whose exit update fires the exit standing there.
void refii_bridge_set_exit_request(float x, float y, float z);
uint32_t refii_bridge_exit_request(float* out_xyz);
int32_t refii_bridge_exit_waiting(void);
int32_t refii_bridge_prompts_two(uint32_t* out, int32_t capacity);
// The things only player two sees (player one's half hides their icon).
int32_t refii_bridge_prompts_one_hidden(uint32_t* out, int32_t capacity);
// Player two's own hold-to-use meter (coop_hero_hooks.cpp): the fraction 0..1 and whether A is held.
void refii_bridge_player_two_hold_state(float fraction, int32_t pressed);
void refii_bridge_player_two_hold(float* fraction, int32_t* pressed);
// Player two's own hold prompt (coop_hero_hooks.cpp): up, its guest world point and title (UTF-8).
void refii_bridge_player_two_hold_prompt(int32_t up, const float* at, const char* title);
int32_t refii_bridge_player_two_hold_prompt_get(float* at, char* title, uint32_t capacity);
// Local co-op: the use prompt text the OTHER player (not the one the guest's prompt is for) would be
// shown, per thing (coop_hero_hooks.cpp, the dry pass): `thing \t player \t title` lines.
void refii_bridge_set_prompt_titles(const char* text);
// Property notices for the host (hooks/src/property_hooks.cpp): kind 0 a rented home fell into
// disrepair (its tenants stop paying), 1 repaired, 2 not enough gold to repair, 3 nothing to repair.
void refii_bridge_property_notice_post(uint64_t uid);
void refii_bridge_property_notice_post_kind(int32_t kind, uint64_t uid);
int32_t refii_bridge_property_notice_take(int32_t* kind, uint64_t* uid);
uint32_t refii_bridge_prompt_titles(char* out, uint32_t capacity);
// Player two's household actions for the co-op peer to run (mod_events.cpp, the core Lua's
// Refii._household): the joiner's estate actions, gifts, furniture and bed for the host, the host's
// kept gifts for the joiner. `<action>:<uid hex>:<value>`, oldest first; take returns the length,
// 0 when none.
void refii_bridge_estate_intent_post(const char* text);
uint32_t refii_bridge_estate_intent_take(char* out, uint32_t capacity);
int32_t refii_bridge_menu_populating(void);

// Saved-game count. The payload samples it by calling the guest's own probe on the tick
// thread; the host reads the answer. Non-zero from _wants_ means "take a fresh sample".
//
// Clear and note are separate, and the pump clears BEFORE it probes: the probe runs guest
// code, and a request the host makes during it has to survive. Clearing afterwards would
// swallow that one and leave the host waiting on an answer that never refreshes.
int32_t refii_bridge_guest_tick_pump_wants_save_count(void);
void refii_bridge_guest_tick_pump_clear_save_count_request(void);
void refii_bridge_guest_tick_pump_note_save_count(int32_t count);
void refii_bridge_guest_tick_pump_request_save_count(void);

// The in-game loading screen the guest stood up, or 0. Shared because the payload's own
// finisher and the host's step finisher must not disagree about which object that is.
uint32_t refii_bridge_resolve_in_game_loading_screen(uint8_t* base);

// Loose-asset and VFS gates. Wrappers: refii::vfs::* in loose_asset_hooks.h.
//
// int32_t rather than bool: a one-byte C++ bool and a C _Bool agree on the ABIs we build
// for, but this header is the one place that must not depend on them continuing to.
int32_t refii_bridge_vfs_loose_asset_override_tracing_enabled(void);
int32_t refii_bridge_vfs_tracing_enabled(void);
int32_t refii_bridge_vfs_verbose_io_tracing_enabled(void);
int32_t refii_bridge_vfs_loose_gameface_binary_overrides_enabled(void);

// STRINGS ARE WRITTEN INTO A CALLER BUFFER, never returned.
//
// Every one of these returns the length the value NEEDS, whether or not it fitted, and
// writes at most `capacity` bytes. A caller that gets back more than it offered retries
// with a bigger buffer — see the wrappers. Returning a std::string instead would allocate
// on one side of the boundary and free on the other, which is the one thing this ABI
// cannot do.
size_t refii_bridge_vfs_loose_asset_root(char* out, size_t capacity);
size_t refii_bridge_vfs_normalize_loose_asset_key(const char* key, size_t length,
                                                  char* out, size_t capacity);

// Search roots are enumerated rather than returned as a container, for the same reason.
uint32_t refii_bridge_vfs_loose_asset_search_root_count(void);
size_t refii_bridge_vfs_loose_asset_search_root(uint32_t index, char* out, size_t capacity);

int32_t refii_bridge_vfs_should_defer_loose_bank_container(const char* path, size_t length);

//
void refii_bridge_publish_hero_derived(const void* sampled);
void refii_bridge_hero_derived_snapshot(void* out);
/// The hero's inventory as the pump last walked it on the guest thread (SampleHeroInventory):
/// four words per record {recordId, count, category, viewed}. A publish whose records differ
/// from the last bumps the UI data generation.
void refii_bridge_publish_hero_inventory(const uint32_t* words, uint32_t count);
/// The same walk with each record's ITEM INSTANCE: five words per record {recordId, count,
/// category, viewed, item} — the item at `node+8`, the guest address the dye tables key on
/// (kDyeStateWords, kItemDyeLayerWords). Publishing it also publishes the four-word snapshot
/// above, in one step, so the two never disagree; an item address is meaningful only until the
/// next publish. The snapshot call returns the record count and copies as many whole records as
/// fit.
void refii_bridge_publish_hero_inventory_items(const uint32_t* words, uint32_t count);
uint32_t refii_bridge_hero_inventory_items_snapshot(uint32_t* out, uint32_t word_capacity);
/// Player 1's or 2's inventory as the pump last walked it (SamplePlayerInventories), whoever's menus
/// are open: four words per record {recordId, count, category, flags}, flags bit 0 viewed and bit 1
/// worn. Publishing never bumps the UI generation (no menu lists it). The snapshot call returns the
/// record count, 0 for any other player, and copies as many whole records as fit.
void refii_bridge_publish_player_inventory(int32_t player, const uint32_t* words, uint32_t count);
uint32_t refii_bridge_player_inventory_snapshot(int32_t player, uint32_t* out, uint32_t word_capacity);
void refii_bridge_publish_quests(const void* entries, uint32_t count);
uint32_t refii_bridge_requested_world_map_region(void);
void refii_bridge_publish_world_map_region(const void* region);

// The rest of the pump's sampled pages, on the same terms as the two above: the payload
// samples on the guest thread and publishes, the host asks for the one row it is showing.
// Each POD goes over as const void* because its definition is in hero_derived.h, which is
// C++; the host casts it back. "sell" is int32_t rather than bool for the reason the
// contract gives at the top of this file.
void refii_bridge_publish_family(const void* members, uint32_t count);
void refii_bridge_publish_real_estate(const void* estate);
void refii_bridge_publish_furniture(const void* furniture);
void refii_bridge_publish_coop_screen(const void* coop);
void refii_bridge_publish_shop_front(const void* front);

/// The highlighted row Godot's list states for an in-game screen (screen 0 clears): what the
/// guest's `g_Menu:GetHighlightedMenuItemData()` reader `sub_826C1B50` answers with instead of
/// asking its hidden Lua menu. Name and label are copied, NUL-terminated, at most 127 bytes.
void refii_bridge_set_stated_highlight(uint32_t screen, int32_t event_type, int32_t id,
                                       const char* name, const char* label);
/// Copies the statement out; false when none stands.
bool refii_bridge_get_stated_highlight(uint32_t* screen, int32_t* event_type, int32_t* id,
                                       char* name, char* label, uint32_t capacity);
/// The name the guest spawned the row with id `id` under, among the LAST rows it spawned: the
/// trailing run of the capture whose event type is `event_type`. The furniture screen's list is
/// spawned onto the capture without a population boundary (its rows follow the previous
/// screen's), so its rows are that trailing run and are not keyed by screen. False when none.
bool refii_bridge_menu_last_spawned_row_name(int32_t event_type, uint32_t id, char* name,
                                             uint32_t capacity);
/// The fold depth of Godot's list for one screen (TU1 in-game id, or 1010 the front end's main
/// menu): how many open folders the highlighted row sits below the root. 0 clears. The guest's
/// `g_Menu:IsHighlightedItemAtRoot()` readers answer "at root" for depth 0 or none stated.
void refii_bridge_set_list_fold_depth(uint32_t screen, uint32_t depth);
uint32_t refii_bridge_list_fold_depth(uint32_t screen);
/// The world map's property row: the UID the host wants sampled, and the page's numbers for it.
uint64_t refii_bridge_requested_property_estate(void);
void refii_bridge_publish_property_estate(uint64_t uid, const void* estate);
uint32_t refii_bridge_requested_quest_travel(void);
void refii_bridge_publish_quest_travel(const void* travel);
uint32_t refii_bridge_requested_shop_price(int32_t* sell);
void refii_bridge_publish_shop_price(const void* price);

// What the guest TELLS a presentation's elements (refii_bridge_told_*) is shared state: its
// entries are in core/payload_bridge.h, its queues in src/runtime/src/core/told_statements.cpp.

void refii_bridge_stage_menu_attribute(const char* name, size_t name_length,
                                       const char* text, size_t text_length,
                                       float number, int32_t is_number);

// The runner the two stagers feed. Pointer and length, not std::string: the chunk text is
// carried verbatim and the host copies it into its own queue.
void refii_bridge_run_menu_chunk(const char* chunk, size_t length, uint32_t presentation);

/// How many rows `presentation`'s current populate stated on its ROOT — `SpawnButton`s since its
/// `DeleteAllSpawnedMenuItems` / `RepopulateMenuFromPopulationScript` / script init, `String4`
/// "Root" or none, one per name, saved and restored by `PushMenuState` / `PopMenuState`. The
/// shipped `CountSpawnedItems` counts exactly `MenuRoot.ChildItems`.
uint32_t refii_bridge_menu_root_rows(uint32_t presentation);

/// The 1-based place, in `presentation`'s current populate, of the row its last `SpawnButton`
/// named — the shipped `GetLastItemIndex` (`NumberOfItems - 1`, the root item taking index 0),
/// which is the index `SetItemEmphasisByIndex` then names. 0 when nothing was spawned.
uint32_t refii_bridge_menu_last_spawn_ordinal(uint32_t presentation);

/// `String2` as `presentation`'s last `GetMenuItemDataByName` wrote it — the named row's text tag
/// in its current populate (`SpawnButton`, then the refresh statements), else "No Text Tag".
/// Taken once; false when that statement has not run since.
bool refii_bridge_menu_by_name_text_tag(uint32_t presentation, char* out, size_t capacity);

// HERO IDENTITY AND PARTNERSHIPS (src/core/hero_partners.cpp), Fable 3's player-to-player marriage
// and business partnership: a hero's id (48 bits, kept with its save; 0 not yet known), made once by
// the machine that plays the hero; a partnership in both partners' stores (1 business, 2 married);
// a building's co-owner (the world's, player one's store) and the gold owed to an absent partner.
uint64_t refii_bridge_hero_id(int32_t player);
void refii_bridge_hero_id_ensure(int32_t player);
int32_t refii_bridge_partner_kind(int32_t player, uint64_t other);
// The partnership between the two heroes in the game (both stores agree), 0 none.
int32_t refii_bridge_partnership(void);
int32_t refii_bridge_partnership_set(int32_t kind);
uint64_t refii_bridge_coowner(uint64_t uid);
void refii_bridge_coowner_set(uint64_t uid, uint64_t id);
int32_t refii_bridge_owed(uint64_t id);
void refii_bridge_owed_add(uint64_t id, int32_t gold);

// ONE-TIME COLLECTABLES PER PLAYER (src/core/coop_collectables.cpp): silver keys by entity address and
// UniqueID; a player's taking is a saved player value. The mask is bit 0 player one, bit 1 player two.
void refii_bridge_collectable_track(uint32_t address, uint64_t uid);
void refii_bridge_collectable_forget(uint32_t address);
uint32_t refii_bridge_collectable_mask(uint64_t uid);
void refii_bridge_collectable_take(int32_t player, uint64_t uid);
// - the host reads (address, mask) pairs of the tracked keys someone has taken; returns the pairs.
uint32_t refii_bridge_collectable_masks(uint32_t* out, uint32_t capacity);

// A VILLAGER'S OPINION OF PLAYER TWO (src/core/coop_opinion.cpp), by the opinion component's
// address: the component's per-hero regions in guest byte order (mod_events.cpp kOpinionRegions),
// starting with +24..+64 (base and current axes) then +176..+236 (the five modifier lists).
#define REFII_OPINION_BLOCK_BYTES 208
int32_t refii_bridge_opinion_two_load(uint32_t component, uint8_t* out);
void refii_bridge_opinion_two_store(uint32_t component, const uint8_t* in);
void refii_bridge_opinion_two_forget(uint32_t component);
// Hero one's slot (the player record `players`'s +108 holder) as our readers must see it on any
// thread: an opinion scope for hero two lends it to hero two for one guest call
// (refii_bridge_hero_slot_lend / _return); a read overlapping a lend answers the real holder.
void refii_bridge_hero_slot_lend(uint32_t real_holder);
void refii_bridge_hero_slot_return(void);
uint32_t refii_bridge_hero_one_holder(const uint8_t* base, uint32_t players);

// PLAYER TWO'S SPLIT-SCREEN HUD (src/core/coop_hud_two.cpp): their targeted title and d-pad
// icons, from the guest's own builders called for hero two (hooks/guest_tick_pump.cpp).
// - the pump states them each tick (known 0 off split screen; icons: up, left, down, right);
void refii_bridge_player_two_hud_state(int32_t known, uint32_t target, const char* title,
                                       const char* const* icons);
// - the host reads the title then the four icon paths, newline-separated, and the targeted
//   entity; returns 1 when stated.
uint32_t refii_bridge_player_two_hud(uint32_t* target, char* out, uint32_t capacity);
// - nonzero while player two's d-pad fill runs (the shared tutorial calls are skipped); the
//   payload's own (the pump sets it, coop_hero_hooks.cpp reads it).
int32_t refii_bridge_dpad_query_two(void);

// THE LOADING SCREEN'S TEXT (src/core/loading_text_state.cpp), as the screen's own calls state it
// (hooks/loading_screen_stated.cpp):
// - made: a constructor ran — its manager, whether it draws the banner, its level name and its
//   text (UTF-8, either may be empty); tips are on in every constructor.
// - gone: the screen's destructor.
// - overlay: the manager's set-text call; taken when that manager made the stated screen.
// - tip: the tag the screen's tip draw resolved, with the index and count it selected from.
// - spinner: one spinner draw.
// - read: six words (tip index, tip count, tip tag, spinner frame = draws less one, tips,
//   banner) and the level name then the overlay as two NUL-terminated strings; 0 with no screen.
void refii_bridge_loading_text_made(uint32_t manager, uint32_t screen, int32_t banner, const char* level,
                                    const char* overlay);
void refii_bridge_loading_text_gone(uint32_t screen);
int32_t refii_bridge_loading_text_overlay(uint32_t manager, const char* text);
void refii_bridge_loading_text_tip(uint32_t screen, uint32_t key, uint32_t index, uint32_t count);
void refii_bridge_loading_text_spinner(uint32_t screen);
int32_t refii_bridge_loading_text_read(uint32_t* out_words, char* text, uint32_t capacity);

#ifdef __cplusplus
}  // extern "C"
#endif
