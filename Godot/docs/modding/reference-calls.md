# Calls reference

Every call a mod makes into the game, by surface. The concept chapters explain each family; this
page is the list. The Mod Studio's **⚡ Events & the Mod API** page lists the same calls with a
search box, generated from the catalog.

## Contents

- [Lua and Godot side by side](#lua-and-godot-side-by-side)
- [Lua: the Refii table](#lua-the-refii-table)
- [Native helpers](#native-helpers)
- [Godot: RefiiHost](#godot-refiihost)
  - [Members and signals](#members-and-signals)
  - [Helper calls](#helper-calls)
- [Native: guest memory](#native-guest-memory)

## Lua and Godot side by side

**Both surfaces have the same access** (see [Lua and Godot have the same access](handles-and-players.md#lua-and-godot-have-the-same-access)).
Each row is one meaning: what a Lua mod writes, what a Godot script writes. Where one surface
reads at the call and the other cannot, the other ASKS: a Godot script asks the guest with
`ask(what, args, callback)` (each `query` verb) or `call_lua(expression, callback)`; a Lua mod asks
the host with `Refii.ask_host(what, fn, ...)`. Inside an event both read at the call: a Lua handler's
`Refii.*` reads, a Godot verdict's `read(what, args)` (the query verbs that change nothing). A row with a dash says why that surface has no form
of its own. The table is the catalog's (`ModApi.Twins`); a test fails on an entry of either
surface that has no row.

| Lua | Godot | Why one side has none |
|---|---|---|
| `Refii.on` | `on_verdict` · `signal mod_event` |  |
| `Refii.off` | `remove_verdict` |  |
| `Refii.on("setting_changed")` · `Refii.setting` · `Refii.set_setting` | `signal setting_changed` · `setting` · `set_setting` |  |
| `Refii.signal` · `Refii.on("mod_signal")` | `signal_lua` · `signal mod_signal` |  |
| `Refii.publish` · `Refii.value` | `publish` · `get_value` |  |
| `Refii.publish_text` · `Refii.get_text` · `Refii.has_text` · `Refii.on("value_changed")` | `publish_text` · `get_text` · `has_text` · `signal value_changed` |  |
| `Refii.ask_host` | `ask` · `query` · `signal reply` |  |
| `Refii.health` · `Refii.position` · `Refii.facing` · `Refii.velocity` · `Refii.gold` · `Refii.item_count` · `Refii.experience` · `Refii.ability_level` · `Refii.relation` · `Refii.alive` · `Refii.locked_target` · `Refii.in_cutscene` · `Refii.camera_mode` · `Refii.fov` · `Refii.has_status` · `Refii.speed_multiplier` · `Refii.current_action` · `Refii.action_type` · `Refii.action_name` · `Refii.action_elapsed` · `Refii.action_interruptible` · `Refii.signed_in` · `Refii.save_count` · `Refii.screen` · `Refii.living` · `Refii.spell_in_slot` · `Refii.spell_parts` · `Refii.vortex_held` · `Refii.spell_balance` · `Refii.host_serial` · `Refii.local_serial` · `Refii.time_of_day` · `Refii.definition` · `Refii.name` · `Refii.level_name` · `Refii.player_hero` · `Refii.player_of` · `Refii.player_for` · `Refii.world_here` · `Refii.combat_state` · `Refii.can_save` · `Refii.renown` · `Refii.morality` · `Refii.purity` · `Refii.physique` · `Refii.age` · `Refii.equipped` · `Refii.inventory` · `Refii.expressions` · `Refii.spells_known` · `Refii.sprinting` · `Refii.locomotion` · `Refii.parry` · `Refii.selected_expression` · `Refii.record_string` · `Refii.record_int` · `Refii.record_number` · `Refii.record_bool` · `Refii.record_exists` · `Refii.record_ref` · `Refii.creature_tier` · `Refii.near` · `Refii.line_of_sight` · `Refii.local_hero` · `Refii.coop_mode` · `Refii.menu_player` · `Refii.split_screen` · `Refii.player_value` · `Refii.player_value_text` · `Refii.player_value_hash` · `Refii.player_here` · `Refii.player_dog` · `Refii.focus` · `Refii.opinion` · `Refii.discovered_crime` · `Refii.perceived_renown` · `Refii.uid` · `Refii.appearance` · `Refii.morph_get` | `read` |  |
| — | `run_lua` · `call_lua` · `frontend_lua` · `query lua_int` | Lua IS the game VM: a Lua mod runs its code there directly, and its front-end code as mod/frontend.lua |
| — | `node_for` | a Godot node: Lua has no scene tree (it places with mark, light and play_fx_on on the handle) |
| — | `dress_model` | a Godot node: Lua has no scene tree |
| — | `item_pose` | a Godot Basis: Lua has no scene tree |
| — | `touch_area` | the touchscreen is drawn on: an area is a rectangle of a Godot script's own drawing, and Lua draws nothing |
| — | `input_glyph` | a Godot texture: Lua draws nothing, and reads the same binding as text through ask_host:input_label |
| — | `register_source` · `register_condition` | called by Godot's UI while it builds a list, at once; Lua runs on the guest tick, so it states a list's rows ahead with set_menu_rows (ghosted included) |
| — | `register_command` | Lua's row verb is the command Mod:lua:name[:arg], which calls the Lua function of that name with the row |
| `Refii.log` | — | GDScript's own print and push_warning write Godot's log; Refii.log writes the guest's |
| `Refii.entity` · `Refii.handle` | — | a guest address for game Lua that takes the object itself; a Godot script holds handles only and reaches game Lua through call_lua |
| `require` | — | GDScript's own preload and load |
| `RefiiModErrors` | — | the Mods error list shows every surface's errors; a GDScript error is Godot's own |
| `Refii.run_as_player` · `Refii.acting_player` · `Refii.show_to_player` | — | the player a game-Lua call stands for while it runs; a Godot call names its player outright (every UI call takes one), and call_lua reaches these |
| `Refii.frontend` | — | marks the front end's own Lua state; a Godot script runs in one process across the front end and the game |
| `Refii.signed_in` · `Refii.save_count` · `Refii.screen` | `query frontend` |  |
| `Refii.hash` | `hash` |  |
| `Refii.api_version` | `api_version` |  |
| `Refii.SPELL_*` | `constants` |  |
| `Refii.spell_info` | `spell_info` |  |
| `Refii.now` · `Refii.real_now` · `Refii.after` · `Refii.cancel` | `now` · `real_now` · `after` · `cancel` |  |
| `Refii.entity_value` · `Refii.entity_set` | `entity_value` · `entity_set` |  |
| `Refii.pool` · `Refii.pool_spend` · `Refii.pool_value` | `pool` · `pool_spend` · `pool_value` |  |
| `Refii.in_play` | `in_play` |  |
| `Refii.health` · `Refii.set_health` · `Refii.modify_health` | `query health` · `set_health` · `modify_health` |  |
| `Refii.position` · `Refii.set_position` | `query position` · `set_position` |  |
| `Refii.facing` · `Refii.velocity` · `Refii.set_facing` | `query facing` · `query velocity` · `set_facing` |  |
| `Refii.entity_tint` · `Refii.entity_untint` · `Refii.entity_alpha` · `Refii.entity_scale` · `Refii.entity_saturation_mask` · `Refii.entity_drawable` | `entity_tint` · `entity_untint` · `entity_alpha` · `entity_scale` · `entity_saturation_mask` · `entity_drawable` |  |
| `Refii.lock_target` · `Refii.release_lock` · `Refii.switch_target` · `Refii.locked_target` | `lock_target` · `release_lock` · `switch_target` · `query locked_target` |  |
| `Refii.wheel_switches_target` | `wheel_switches_target` |  |
| `Refii.in_cutscene` | `in_cutscene` · `cutscene_kinds` · `query in_cutscene` |  |
| `Refii.apply_status` · `Refii.clear_status` · `Refii.has_status` | `apply_status` · `clear_status` · `query has_status` |  |
| `Refii.knockback` · `Refii.stagger` | `knockback` · `stagger` |  |
| `Refii.set_speed_multiplier` · `Refii.clear_speed_multiplier` · `Refii.speed_multiplier` | `set_speed_multiplier` · `clear_speed_multiplier` · `query speed_multiplier` |  |
| `Refii.current_action` · `Refii.action_elapsed` · `Refii.action_interruptible` · `Refii.allow_action_interrupt` · `Refii.allow_action_interrupt_at` · `Refii.action_type` · `Refii.action_name` | `query current_action` · `query action` · `allow_action_interrupt` · `allow_action_interrupt_at` |  |
| `Refii.living` · `Refii.alive` | `query living` · `query alive` |  |
| `Refii.spell_in_slot` · `Refii.spell_parts` · `Refii.vortex_held` | `query spell_in_slot` · `query spell_parts` · `query vortex_held` |  |
| `Refii.spell_balance` | `query spell_balance` |  |
| `Refii.host_serial` · `Refii.local_serial` | `query host_serial` · `query local_serial` |  |
| `Refii.cast_spell` · `Refii.spell_serial` | `cast_spell` · `cast_spell_at` · `cast_spell_with` · `spell_serial` |  |
| `Refii.charge_spell` | `charge_spell` |  |
| `Refii.play_sound` · `Refii.play_sound_at` · `Refii.stop_sound` · `Refii.play_music` · `Refii.stop_music` | `play_sound` · `play_sound_at` · `stop_sound` · `play_music` · `stop_music` |  |
| `Refii.play_animation` · `Refii.stop_animation` | `play_animation` · `stop_animation` |  |
| `Refii.modify_opinion` · `Refii.add_deed` · `Refii.opinion` | `modify_opinion` · `add_deed` · `query opinion` |  |
| `Refii.npc_follow` · `Refii.npc_go_to` · `Refii.npc_flee` · `Refii.npc_attack` · `Refii.npc_set_hostile` · `Refii.npc_stop` | `npc_follow` · `npc_go_to` · `npc_flee` · `npc_attack` · `npc_set_hostile` · `npc_stop` |  |
| `Refii.set_time_of_day` · `Refii.time_of_day` · `Refii.set_weather` | `set_time_of_day` · `query time_of_day` · `set_weather` |  |
| `Refii.gold` · `Refii.give_gold` | `query gold` · `give_gold` |  |
| `Refii.item_count` · `Refii.add_item` · `Refii.inventory` · `Refii.equipped` | `query item_count` · `add_item` · `query inventory` · `query inventory_stack` · `query equipped` |  |
| `Refii.experience` · `Refii.give_experience` · `Refii.spend_experience` · `Refii.ability_level` | `query experience` · `give_experience` · `spend_experience` · `query ability_level` |  |
| `Refii.relation` · `Refii.definition` · `Refii.name` · `Refii.level_name` · `Refii.uid` · `Refii.creature_tier` | `query relation` · `query definition` · `query name` · `query level_name` · `query uid` · `query creature_tier` |  |
| `Refii.near` · `Refii.line_of_sight` · `Refii.spawn` | `query near` · `query line_of_sight` · `query spawn` |  |
| `Refii.record_string` · `Refii.record_int` · `Refii.record_number` · `Refii.record_bool` · `Refii.record_exists` · `Refii.record_ref` | `query record_string` · `query record_int` · `query record_number` · `query record_bool` · `query record_exists` · `query record_ref` |  |
| `Refii.renown` · `Refii.morality` · `Refii.purity` · `Refii.modify_renown` · `Refii.modify_morality` · `Refii.modify_purity` · `Refii.physique` · `Refii.age` | `query renown` · `query morality` · `query purity` · `modify_renown` · `modify_morality` · `modify_purity` · `query physique` · `query age` |  |
| `Refii.forgive_crimes` · `Refii.discovered_crime` · `Refii.perceived_renown` · `Refii.focus` | `forgive_crimes` · `query discovered_crime` · `query perceived_renown` · `query focus` |  |
| `Refii.appearance` · `Refii.set_appearance` · `Refii.set_appearance_automatic` | `query appearance` · `set_appearance` · `set_appearance_automatic` |  |
| `Refii.morph_get` · `Refii.morph_set` · `Refii.morph_id` | `morph_get` · `query morph` · `morph_set` · `morph_id` |  |
| `Refii.hero` · `Refii.player_hero` | `hero` · `query hero` |  |
| `Refii.player_of` · `Refii.player_for` · `Refii.player_dog` · `Refii.combat_state` | `query player_of` · `query player_for` · `query player_dog` · `query combat_state` |  |
| `Refii.local_hero` · `Refii.menu_player` · `Refii.coop_mode` · `Refii.world_here` · `Refii.split_screen` | `local_hero` · `menu_player` · `coop_mode` · `world_here` · `split_screen` · `query local_hero` · `query menu_player` · `query coop_mode` · `query world_here` · `query coop_state` |  |
| `Refii.player_here` · `Refii.event_replica` | `player_here` · `query player_here` · `e.replica` |  |
| `Refii.achievement_unlocked` · `Refii.achievements` · `Refii.character_id` | `achievement_unlocked` · `achievements` · `character_id` |  |
| `Refii.player_value` · `Refii.player_set` · `Refii.player_add` · `Refii.player_save` | `player_value` · `player_set` · `player_add` · `player_save` · `query player_value` |  |
| `Refii.player_value_text` · `Refii.player_set_text` · `Refii.player_save_text` · `Refii.player_value_hash` · `Refii.player_save_hash` | `player_text` · `player_set_text` · `player_save_text` · `player_value_hash` · `player_save_hash` · `query player_text` |  |
| `Refii.ask_partner` · `Refii.partner_answer` · `Refii.key_taken` | `ask_partner` · `partner_answer` · `key_taken` |  |
| `Refii.send_to_partner` | `send_to_partner` |  |
| `Refii.can_save` · `Refii.save_game` | `query can_save` · `save_game` |  |
| `Refii.save_snapshot` · `Refii.load_snapshot` · `Refii.ask_host("snapshot_time", fn)` | `save_snapshot` · `load_snapshot` · `snapshot_time` |  |
| `RefiiModSave.<key>` | `save_set` · `save_get` |  |
| `Refii.use_item` · `Refii.equip_weapon` · `Refii.perform_expression` · `Refii.assign_spell` · `Refii.release_expression` | `use_item` · `equip_weapon` · `perform_expression` · `assign_spell` · `release_expression` |  |
| `Refii.expressions` · `Refii.spells_known` · `Refii.selected_expression` | `query expressions` · `query spells_known` · `query selected_expression` |  |
| `Refii.sprinting` · `Refii.end_sprint` · `Refii.set_locomotion` · `Refii.locomotion` · `Refii.set_parry` · `Refii.parry` | `query sprinting` · `end_sprint` · `set_locomotion` · `query locomotion` · `set_parry` · `query parry` |  |
| `Refii.input_claim` · `Refii.ask_host("input_claimed", fn)` · `Refii.ask_host("input_claims", fn)` | `claim_input` · `input_claimed` · `input_claims` |  |
| `Refii.press` · `Refii.tap` · `Refii.ask_host("presses", fn)` | `press` · `tap` · `presses` |  |
| `Refii.attack_mode` | `attack_mode` |  |
| `Refii.camera_mode` · `Refii.camera_override` · `Refii.fov` | `camera_mode` · `camera_override` · `fov` · `query camera_mode` |  |
| `Refii.camera_tune` · `Refii.camera_frame` · `Refii.camera_focus` · `Refii.camera_combat_blend` · `Refii.camera_interest` · `Refii.camera_pitch_hold` · `Refii.camera_lock_frame` · `Refii.camera_mode_allow` | `camera_tune` · `camera_frame` · `camera_focus` · `camera_combat_blend` · `camera_interest` · `camera_pitch_hold` · `camera_lock_frame` · `camera_mode_allow` |  |
| `Refii.camera_offset` · `Refii.camera_pose` · `Refii.camera_stated` · `Refii.camera_zoom` · `Refii.camera_screen_mode` · `Refii.camera_split` · `Refii.camera_view` · `Refii.camera_ray` | `camera_offset` · `camera_pose` · `camera_stated` · `camera_zoom` · `camera_screen_mode` · `camera_split` · `camera_view` · `camera_ray` |  |
| `Refii.camera_shake` | `camera_shake` |  |
| `Refii.hide_part` | `hide_part` |  |
| `Refii.play_fx` · `Refii.play_fx_on` · `Refii.stop_fx` | `play_fx` · `play_fx_on` · `stop_fx` |  |
| `Refii.light` · `Refii.light_remove` · `Refii.ask_host("lights", fn)` | `light` · `light_remove` · `lights` |  |
| `Refii.mark` · `Refii.unmark` · `Refii.world_meter` · `Refii.mark_meter` · `Refii.ask_host("markers", fn)` | `mark` · `unmark` · `world_meter` · `mark_meter` · `markers` |  |
| `Refii.fade` · `Refii.ask_host("fade_state", fn)` | `fade` · `fade_state` |  |
| `Refii.meter` · `Refii.meter_remove` · `Refii.ask_host("meters", fn)` | `meter` · `meter_remove` · `meters` |  |
| `Refii.ask_host("minimap", fn)` · `Refii.ask_host("minimap_point", fn)` | `minimap` · `minimap_point` |  |
| `Refii.ask_host("inventory_items", fn)` · `Refii.ask_host("inventory_action", fn)` · `Refii.ask_host("own_screen", fn)` | `inventory_items` · `inventory_action` · `own_screen` |  |
| `Refii.ask_host("touch_mode", fn)` · `Refii.ask_host("touch_stick", fn)` · `Refii.ask_host("move_stick", fn)` · `Refii.ask_host("input_label", fn)` | `touch_mode` · `touch_stick` · `move_stick` · `input_label` |  |
| `Refii.hud_icon` · `Refii.hud_slide` · `Refii.hud_hold` · `Refii.ask_host("hud_overrides", fn)` | `hud_icon` · `hud_slide` · `hud_hold` · `hud_overrides` |  |
| `Refii.set_menu_rows` · `Refii.open_menu` · `Refii.open_screen` | `set_menu_rows` · `open_menu` · `open_screen` |  |
| `Refii.message_box` · `Refii.on("mod_message_box")` | `message_box` · `signal message_box_answered` |  |
| `Refii.notice` · `Refii.on("mod_notice")` | `notice` · `signal notice_done` |  |
| `Refii.text` | `text` |  |
| `Refii.menu_box` · `Refii.on("mod_menu_box")` | `menu_box` · `signal menu_box_answered` |  |
| `Refii.ask_host("mods", fn)` | `mods` |  |

## Lua: the Refii table

Composed in front of every mod's `mod/init.lua`. Entities are [handles](handles-and-players.md#handles);
hashes are `"0x…"` strings. The title screen's `mod/frontend.lua` has a smaller table: see
[The front-end surface](ui-and-hud.md#the-front-end-surface).

| Call | Meaning |
|---|---|
| `Refii.on(event, function(e) ... end)` | subscribes; `e` is a table of the event's fields; `return true` (or `e.cancel = true`, as a Godot verdict's) cancels where the event allows it. An event raised while a handler runs is delivered nested and live when it has writable fields, else after that chunk returns with `e.deferred = true` (see [Nested and deferred events](events.md#nested-and-deferred-events)) |
| `Refii.off(event, fn)` → bool | takes back a handler `Refii.on` took, as Godot's `remove_verdict` and `disconnect`; true when it stood |
| `Refii.setting(key, default)` | a `settings.json` value with the player's choice over it, read at the call: `RefiiSettings`, composed in at session start and kept current — a change made live (the Options › Mods page, a script's `set_setting`, the Studio's live tune) is written into the running VM's table at the next guest tick, so no reload is needed |
| `require(name)` | the mod's own module `mod/<name>.lua` (see [One file per concern](events.md#one-file-per-concern)): run once on the first `require`, its return value cached — `nil` as `true` — and an error naming the file when there is none |
| `Refii.hero(player)` | the player's hero handle as the bus last stated it, or nil |
| `Refii.now()` · `Refii.real_now()` · `Refii.after(seconds, fn [, key])` → id · `Refii.cancel(key)` | the runtime's game clock (stands still while paused), the raw tick clock, and one-shot timers on the guest tick (see [The runtime](events.md#the-runtime-clock-timers-values-pools)) |
| `Refii.entity_value(e, key [, default])` · `Refii.entity_set(e, key, v)` | a value per entity handle, forgotten at its `entity_removed` |
| `Refii.pool(p, key, { max, floor, regen, delay [, max_key] })` · `Refii.pool_spend(p, key, n)` → bool · `Refii.pool_value(p, key)` → value, max | a regenerating pool in player p's store, ticked where p is played |
| `Refii.in_play(p)` | no pause, menu, level change or load, p's hero standing, no full cutscene for p |
| `Refii.spell_info(type)` → name_tag, icon | a spell type's name tag and wheel icon (a stand-in's when one replaces it) |
| `Refii.SPELL_*` · `ABILITY_*` · `REQUEST_*` · `ACTION_*` · `RELATION_*` · `ATTACK_MODE_*` | the named numbers (see [The runtime](events.md#the-runtime-clock-timers-values-pools)) |
| `Refii.on("setting_changed", fn)` | `{ key, value, previous }` on a live setting change, after `Refii.setting` answers it |
| `Refii.set_setting(key, v)` | sets a mod setting in the running game, as Godot's `set_setting` (asked of the host: in force for native code, Godot and this table's `setting_changed` a tick later) |
| `Refii.get_text(key)` · `Refii.has_text(key)` | a published TEXT as the host states it — every surface's `publish_text`, a Lua mod's own at once — nil / false for none |
| `Refii.on("mod_signal", fn)` | `{ mod, name, payload }`: a Godot script's `signal_lua(name, payload)` on this machine, the other way of `Refii.signal` |
| `Refii.on("value_changed", fn)` | `{ key, value }`: a published number or text changed, whoever published it — what Godot's `value_changed` hears, a tick later |
| `Refii.ask_host(what, fn, ...)` → bool | asks the host for what only Godot holds: `fn(value)` runs on a later tick with the answer (plain data). `what`: `fade_state(p)`, `hud_overrides(p)`, `input_claimed(p, action)`, `input_claims(p)`, `lights()`, `markers()`, `meters()`, `mods()`, `presses(p)`, `snapshot_time(key)`, and the actions `set_setting`, `save_snapshot`, `load_snapshot` |
| `Refii.ask_host` also answers | `minimap(p)` · `minimap_point(x, y, z)` · `inventory_items(p)` · `inventory_action(p, operation, definition)` · `own_screen(p, on)` (the asking mod's) · `touch_mode()` · `touch_stick()` · `move_stick(p)` · `input_label(name)`. Lua gets plain data: `minimap`'s `texture_path` (a `game://` path) in place of its texture, an item's `pieces` as a list of model paths, `touch_stick` and `move_stick` as `{ x, y }` |
| `Refii.save_snapshot(key [, fn])` · `Refii.load_snapshot(key [, fn])` | Godot's snapshots of this machine's player-one save (see [Snapshots](saves-and-sharing.md#snapshots-a-quicksave-and-a-quickload)), asked of the host; `fn(ok)` once done |
| `Refii.publish(key, n)` / `Refii.value(key)` | a mod value Godot reads as `mod:<key>` (HUD pieces, FX recipes); the last value under a key |
| `Refii.log(text)` | a line in the guest log, prefixed `[mod lua]` |
| `Refii.hash(name)` | the game's FNV-1 of a name as a `"0x…"` string: a GDB record's hash, a text tag's key |
| `Refii.health(e)` → current, max · `Refii.set_health(e, v)` · `Refii.modify_health(e, delta, attacker)` | health read, written, or changed through the game's own Modify (death included) |
| `Refii.position(e)` → x, y, z · `Refii.set_position(e, x, y, z)` | a position through the entity's own placement |
| `Refii.facing(e)` → x, y, z · `Refii.velocity(e)` → x, y, z · `Refii.set_facing(e, x, y [, z])` → bool | the game's own `Physics.GetFacingVector` / `GetVelocity` / `SetFacingVector` (a hero turns through `Player.SetFacingVector`); guest axes, z up — the game keeps a facing level (it drops z) and ignores a vector shorter than 1e-4; nil / false for an entity not in the world |
| `Refii.entity_tint(e, r, g, b [, a])` · `Refii.entity_untint(e)` | tints an entity through the game's own appearance setter (see [Tint](gameplay.md#appearance-tint-alpha-scale)); `a` defaults to 1; untint puts back the constructor's 1, 1, 1, 1 |
| `Refii.entity_alpha(e, a)` · `Refii.entity_scale(e, s)` · `Refii.entity_saturation_mask(e, on)` · `Refii.entity_drawable(e, on)` | the other setters of the same renderable (see [Appearance](gameplay.md#appearance-tint-alpha-scale)); reset = 1, 1, false, true |
| `Refii.lock_target(p [, e])` · `Refii.release_lock(p)` · `Refii.switch_target(p, left) -> target` · `Refii.locked_target(p) -> target` | the target lock as the game's LT states it (see [The target lock](gameplay.md#the-target-lock)); `e` nil or 0 is the game's own pick |
| `Refii.in_cutscene([p [, kinds]]) -> inside, kinds` | whether any of `kinds` (bits; nil: all three) holds for player p (nil or 0: either), and every bit holding — 1 full, 2 interactive, 4 look-at, named `Refii.CUTSCENE_FULL` / `_INTERACTIVE` / `_LOOK_AT` (see [Cutscenes](cutscenes.md)) |
| `Refii.apply_status(e, kind, seconds [, level [, source]])` · `Refii.clear_status(e, kind)` · `Refii.has_status(e, kind)` | a status through the spell's own installer: 1 electrified, 2 chaos, 3 burning (see [Status effects](gameplay.md#status-effects)) |
| `Refii.knockback(e, x, y, z [, force])` · `Refii.stagger(e)` | a Force Push's knockdown hit along a direction, or the flinch a 0-damage tap plays (see [Knockback and stagger](gameplay.md#knockback-and-stagger)) |
| `Refii.set_speed_multiplier(p, factor)` · `Refii.clear_speed_multiplier(p)` · `Refii.speed_multiplier(p) -> overridden, factor` | the player component's own movement-speed override (see [Movement speed](gameplay.md#movement-speed)); 0 holds the hero in place; nil without a player component |
| `Refii.current_action(e) -> action` · `Refii.action_elapsed(a) -> seconds` · `Refii.action_interruptible(a)` · `Refii.allow_action_interrupt(a)` | the action an entity's manager runs (nil for none or once stopped), its clock, and the interrupt permission every animation grants (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `Refii.allow_action_interrupt_at(a, seconds)` | the grant once the action's own clock reaches `seconds` — at once when it already has, else on the tick it does; dropped when the action stops first (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `Refii.action_type(a) -> type` · `Refii.action_name(a) -> name` | what an action is: its definition's `Type`, EScriptableAction (0 PLAY_ANIMATION, 1 MELEE_STRIKE, 2 STRAFE — the roll, ...), and that enum's name; nil for an action the factory never saw (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `Refii.living(e)` | in the world and, for a creature, not yet died (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `Refii.spell_in_slot(p, slot) -> type` | the spell held in slot 0..4, -1 for none (the spell manager's slot table, row 0) |
| `Refii.spell_parts(serial) -> table` | what a cast put in the world, by serial: `spell`, `type`, `targeted`, `level`, `caster`, `caster_player`, `vortex`, `anchor`, `swords = { .. }`, `origin = { x, y, z }`, `exploded` — absent parts nil, nil for a cast the factory never saw (see [A live cast](gameplay.md#a-live-cast-its-parts-and-lifecycle)) |
| `Refii.vortex_held(serial) -> { e, .. }` | who the live vortex of a cast holds now, by serial: the vortex's own held list, in its order, each a handle while it is in the world — whether or not it ever hit them; nil when no live vortex carries the serial. `vortex_caught` states each capture and release |
| `Refii.spell_balance(caster, type, level) -> table` | one power (0..4) of a spell as its Targeted class's own data read loads it for the caster's definitions — the same values the cast reads: `status_seconds` and `status_spread`, the status its Start installs on each victim (the installer gets seconds × the victim's own factor + a uniform draw in [0, spread)): Lightning ElectrifyDuration and 5, Fireball CreatureBurnTime and 0, Chaos Duration and 2; `duration`, Vortex's Duration (the twister's life); `swords`, Blades' NumSwords (the swords one cast makes). A field the class does not carry is nil; nil for another type or a caster with no spell manager |
| `Refii.host_serial(serial) -> serial` · `Refii.local_serial(host_serial) -> serial` | one cast, one identity online: the host's serial of this machine's spell, and this machine's spell for the host's serial — the serial itself on the host, alone and in local co-op; nil on the joiner while the cast is not paired (see [One cast, one identity online](gameplay.md#a-live-cast-its-parts-and-lifecycle)) |
| `Refii.play_sound(cue, e [, tag])` · `Refii.play_sound_at(cue, x, y, z [, tag])` · `Refii.stop_sound(e, tag [, fade_ms])` · `Refii.play_music(name)` · `Refii.stop_music(name)` | an audio cue through the game's own cue player, on an entity or at a point, stopped by tag; a music cue through SoundTools' own PlayMusic/StopMusic (see [Sound](gameplay.md#sound)) |
| `Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]])` · `Refii.stop_animation(e)` | a named animation as the shipped AI plays one, a scripted action (see [Animation](gameplay.md#animation)) |
| `Refii.modify_opinion(villager, hero, axis, delta)` · `Refii.add_deed(villager, hero, deed)` | a villager's opinion of a hero moved through the game's own setter, or a deed told through its own message (see [Opinion and deeds](gameplay.md#opinion-and-deeds)); `deed` is a `"0x…"` string (`Refii.hash` of the Deed record's name) |
| `Refii.npc_follow(e, target [, radius])` · `Refii.npc_go_to(e, x, y, z)` · `Refii.npc_flee(e, from)` · `Refii.npc_attack(e, target)` · `Refii.npc_set_hostile(e, other, on)` · `Refii.npc_stop(e)` | orders a creature as the shipped quests do, through the game's own entries in its VM (see [NPC behaviour](gameplay.md#npc-orders)); `other` is an entity — player two's hero is its own |
| `Refii.set_time_of_day(hours)` · `Refii.time_of_day() -> hours` · `Refii.set_weather(theme [, weight [, seconds]])` | the game clock (0..24) through its own setter, read back; the GAME's environment theme blended as the quests do (see [Time and weather](gameplay.md#time-and-weather)); weight defaults to 1, seconds to 1 |
| `Refii.gold(e)` · `Refii.give_gold(e, delta)` | a purse, read and changed through the game's own Modify |
| `Refii.item_count(e, def)` · `Refii.add_item(e, def)` | an inventory; `def` is a `"0x…"` string (`Refii.hash` of the record's name) |
| `Refii.add_item(e, def, n)` → item, added | n (1..100) of a definition, each through the game's own add; the last item's handle (nil for none) and how many were added |
| `Refii.experience(e, pool)` · `Refii.give_experience(e, pool, n)` · `Refii.spend_experience(e, pool, n)` | experience: pools 0 General 1 Strength 2 Skill 3 Will |
| `Refii.ability_level(e, type)` | the level of ability 1..14, or nil |
| `Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir [, options]]])` → spell, serial · `Refii.spell_serial(spell)` → serial | a real spell through the game's factory, no will cost, started at `pos = {x, y, z}` facing `dir` (the caster's own position and facing when absent): Fable 2's targeted classes place from the position along the facing — Lightning draws its bolt from there to the target, Fireball spawns its shot there, Force Push shoves the target away from there — so a cast started ON a victim is Fable III's position cast. `options = { tag = n, damage_scale = f, at = e or {x, y, z} }`: the cast's `tag`, carried by its `spell_created`, `vortex_started`, `hit`, `hit_landed`, `fireball_impact` and `status_applying`, and its hits' damage × `damage_scale` where the hit is decided; `at` places the Targeted cast by its class's own rule for that entity or point, `pos` then the origin and `dir` the facing when the origin stands on it ([Casting at a target or a point](gameplay.md#casting-at-a-target-or-a-point): Fireball and Chaos refuse a point, Slow Time and Raise Dead refuse both, with a mod error). Returns the spell object's handle and its cast serial (the number its hits, `spell_created` and `spell_parts` carry), nil for no cast; `spell_serial` reads a spell handle's serial. The serial, not the handle, is a cast's identity: a spell handle is reused by the next cast of the same class |
| `Refii.relation(a, b)` · `Refii.alive(e)` · `Refii.definition(e)` · `Refii.name(e)` · `Refii.player_of(e)` | the faction rule (1 neutral 2 ally 4 enemy), liveness, the definition hash, the instance name, 1/2/0 |
| `Refii.level_name()` · `Refii.player_hero(p)` · `Refii.local_hero()` · `Refii.menu_player()` | the loaded level, a player's hero, the local hero, the player whose menus are open (1 or 2). The local hero is the guest's own local-player rule: player two's on an online joiner, player one's on a host or alone, and in LOCAL co-op the hero of whoever owns the open menus — both players are local there, so name a player (`Refii.player_hero(p)`) when the answer must not follow the menus |
| `Refii.player_value(p, key [, fallback])` · `Refii.player_set(p, key, v)` · `Refii.player_add(p, key, d [, saved])` · `Refii.player_save(p, key, v)` · `Refii.player_here(p)` | per-player values, the same on both co-op machines; saved ones go home with the hero (see [Player state](handles-and-players.md#the-player-store)) |
| `Refii.achievement_unlocked(p, id)` · `Refii.achievements(p)` → `"id,id,…"` · `Refii.character_id(p)` | whether the hero player p plays has unlocked achievement id, its unlocked ids (ascending), its character id — per character, as the machine playing that hero states them on the co-op channel, so both machines answer alike (see [A player's achievements](handles-and-players.md#a-players-achievements)) |
| `Refii.player_value_text(p, key)` · `Refii.player_set_text(p, key, text)` · `Refii.player_save_text(p, key, text)` · `Refii.player_value_hash(p, key)` · `Refii.player_save_hash(p, key, "0x…")` | TYPED values in the same store: a text (at most 256 bytes) kept exactly, a hash kept as its `"0x%08x"` text — a record hash survives where a float would not; nil for none (see [Typed values](handles-and-players.md#typed-values-texts-and-hashes)) |
| `Refii.player_dog(p)` | a player's dog: player one's, or player two's co-op dog bound to them; nil when none. Use this, never the game's `GetDog()`: inside player two's dog's own AI and item use, `GetDog()` answers that dog |
| `Refii.event_replica()` | true when the event being handled is a copy of something the other co-op machine decides — the game's own copy (verdicts apply) or a relayed one (it already happened): player-state writes are dropped; skip world and body effects in it |
| `Refii.player_for(e)` | the player `e` deals with, 1 or 2: a hero its own, a dog its master's, anything else the nearer living hero (3 m of hysteresis). Defined by the core co-op Lua |
| `Refii.acting_player()` | 1 or 2: who "the hero" (`GetPlayerHero()`) means right now — 2 inside `Refii.run_as_player(2, …)` and in AI acting for player two. Defined by the core co-op Lua |
| `Refii.run_as_player(p, fn, ...)` | runs `fn` with `GetPlayerHero()`, `GetPlayerHenchman()` and `QuestManager.HeroEntity` as player p's (the partner as the henchman), so a shipped script acts for either hero. Defined by the core co-op Lua |
| `Refii.combat_state(p)` → midst, close_to_combat, in_any_combat | player p's combat state, kept per hero (the shipped global is one state). Defined by the core co-op Lua |
| `Refii.show_to_player(p, name, ...)` | runs the function a global path names (`"GUI.DisplayMessageBox"`, `"GUI.AskYesNoQuestion"`, `"TutorialManager.DisplayTutorial"`, …) for player p, on p's screen: online on the machine that plays p (a question waits — call it from a coroutine — and returns the answer p gave), else here as p (split screen: in p's half, on p's pad). Defined by the core co-op Lua. See [Player two's screen](handles-and-players.md#player-twos-screen) |
| `Refii.forgive_crimes(villager, p)` → n · `Refii.discovered_crime(villager, p)` · `Refii.perceived_renown(villager, p)` | a villager's crimes and renown for player p's hero: forgives only p's crimes it knows of (the other hero's stand; how many), whether it has discovered one of p's, the renown it credits p with (nil for no villager). The shipped `Villager.ForgiveHerosCrimes` / `HasHeroCommittedCrimeIHaveDiscovered` / `GetPerceivedRenownOfHero` name no hero; with a partner they answer for the acting player through these |
| `Refii.world_here()` | true where the shared world runs — the AI, every hit's outcome, the NPCs: alone, in local co-op and on an online host; make world effects only there |
| `Refii.split_screen()` | true when local co-op runs on split screen (each local player their own camera) |
| `Refii.opinion(villager, p)` → love, fear, attractiveness, morality, respect | how a villager feels about player p's hero (-1..1); each villager keeps a set per hero, and only player two's deeds move player two's; nil when it holds no opinions |
| `Refii.focus(p)` | the living entity player p's hero is focused on, or nil. The targeting is driven where that hero is played, so ask where `Refii.player_here(p)` holds: on an online joiner `Refii.focus(1)` reads the host hero's copy, not the host player's choice |
| `Refii.wheel_switches_target(e)` | hero `e`'s target lock switches by the mouse wheel this tick — call it each tick the lock holds (the statement holds for the guest tick it is made on and the two after; the Lua `tick` runs every guest tick, so a lock stated from it never lapses). While it holds and the hero is locked under the third-person camera the keyboard and mouse drive, the mouse's movement never switches target and each wheel notch is one switch through the camera's own `Targeting.CheckForBetterTargetInDirection` (down the next target right, up the next target left), waiting on the camera's `ClearToRotate` as a stick flick does, at most one notch waiting. Without it a mouse flick switches. A pad is untouched. Souls Combat's toggled lock uses the sticky form below |
| `Refii.wheel_switches_target(e, on)` | the same, STICKY: `true` states it and the guest tick restates it until `false` or the lock ends — the store a Godot script's call lives in |
| `Refii.uid(e)` | the entity's own 64-bit UniqueID as `"0x…"`: the same on both co-op machines and across saves (a handle is neither), so it keys per-thing player values (`Refii.player_save(p, "mymod.opened." .. Refii.uid(e), 1)`) |
| `Refii.coop_mode()` | 0 alone, 1 local co-op (split or shared screen), 2 online with this machine hosting, 3 online with this machine joined |
| `Refii.key_taken(e, p)` | whether player p has taken the per-player collectable `e` already (silver keys: each hero picks up their own) |
| `Refii.ask_partner(asker, question)` → ticket · `Refii.partner_answer(ticket)` → nil, true, false | puts your own question to the other player (a box on their half, or on the peer's screen online); nil while pending; thirty seconds unanswered is a no; with no partner in the game the answer is yes at once |
| `Refii.renown(e)` · `Refii.morality(e)` · `Refii.purity(e)` · `Refii.age(e)` · `Refii.physique(e)` → fat, height | the hero stats the game itself reports |
| `Refii.appearance(p)` → `{ automatic, tan, fat, height, strength, skill, will, morality, purity, age }` or nil · `Refii.set_appearance(p, { tan = .., .. })` · `Refii.set_appearance_automatic(p, on)` | Murgo's Mirror for player p: the look in force (the stated inputs when manual, the game's own when automatic) · manual with the named inputs, the rest as the hero looks (height -1..1, age 18..60, the rest 0..1) · the game's own look again (true) or the held manual one (false). The Mirror's own look, saved with that player's hero; see [Appearance (Murgo's Mirror)](gameplay.md#appearance-murgos-mirror) |
| `Refii.morph_get(p, morph)` → weight or nil · `Refii.morph_set(p, morph, w)` → bool · `Refii.morph_id("<mod>.<name>")` → `"0x…"` | a mod's own morph (`morphs.json`) for player p: the weight the game draws its records at (the stored one clamped to the declared range, else the default; nil for a morph no enabled mod declares) · set it, clamped, saved with the hero; the hero is redrawn and `morph_changed` raised · its type, as its records' `MorphType` carries it. A morph is `"<mod>.<name>"` or `"0x…"`; see [Mod morphs](gameplay.md#mod-morphs) |
| `Refii.can_save(p)` · `Refii.save_game(p)` → bool | the game's own save for player p: `can_save` is `Player.IsAbleToSaveGame` for p's hero (saving enabled, p's user holding a storage device); `save_game` is `AutoSaveGame(true, true)` where `can_save` allows it, false otherwise — the game's whole save on the next world update, each hero into its own package, `save_written` following per package |
| `Refii.modify_renown(e, delta)` · `Refii.modify_morality(e, delta)` · `Refii.modify_purity(e, delta)` → bool | the hero stats changed through the game's own `Stats.ModifyRenown` / `ModifyMorality` / `ModifyPurity`: the `renown` / `morality` / `purity` events fire, the game clamps, unlocks expressions and gives a co-op partner its renown share. Morality and purity change only on the machine that decides the session (the host, or the one machine alone); false for an entity without stats |
| `Refii.inventory(e)` | `{ {definition = "0x…", count = n, usable = bool}, … }` in the game's order; `usable` is the item's own UsableFromInventory test, what the Items page's use and A button ask (natively `refii_mod_inventory_usable(ctx, base, entity, index)`); `definition` is the item ENTITY record, which carries none of the item's own properties — the category, name and icon are on its InventoryItemComponent: `local c = Refii.record_ref(def, "InventoryItemComponent")`, then `Refii.record_int(c, "Category")`, `Refii.record_string(c, "NameTag")`, `Refii.record_string(c, "IconGraphic")` |
| `Refii.equipped(e)` → melee, ranged | the weapon set's definition hashes; nil for an empty hand |
| `Refii.use_item(p, def)` · `Refii.equip_weapon(p, def)` · `Refii.perform_expression(p, expression)` · `Refii.assign_spell(p, spell)` | the quick menu's own actions for player p's hero, gated as the quick menu is: use an inventory item by definition, equip a weapon the inventory holds (an online joiner's pick goes through the co-op kit to the host — the caller never cares), perform a known expression, put spell type 0..7 on every tier the hero knows; true when performed (see [Items, weapons, expressions and spells](gameplay.md#items-weapons-expressions-and-spells)) |
| `Refii.use_item(p, def, {ungated = true})` (and `equip_weapon`, `assign_spell`) · `Refii.perform_expression(p, expression [, held [, opts]])` · `Refii.release_expression(p)` → bool | the quick actions with options: `ungated` skips the quick menu's gate (the d-pad up and `CanPlayerOpenQuickMenu`) and is still refused for a dead hero and in any cutscene. An expression is a tap by default — started and released at once, the basic form; `held` leaves it unreleased, so an extendable one extends when the game's own one-second countdown runs out and plays on until `release_expression(p)`, the performer's own end that the extend button's release (GameAction 25) calls |
| `Refii.sprinting(p)` · `Refii.end_sprint(p)` → bool | the sprint component's own flag, and the sprint button's release (GameAction 67) as the game runs it: the flag cleared and the sprint movement stopped; a held sprint button sprints again only on its next press. False when not sprinting |
| `Refii.set_locomotion(p, {MaxTurnSpeedStatic = 720, FrictionCoefficient = 9, ..})` → bool · `Refii.locomotion(p, key)` | THIS hero's own copy of its LocomotionData table (each hero's locomotion component copies the global table when it loads one; the game reads that copy): any of the ten fields the game copies (`MinControlMagForMovement`, `MinVelMagForMovement`, `MaxTurnSpeedStatic`, `MaxTurnSpeedRunning`, `MaxSpeed`, `SprintSpeedMultiplier`, `SprintTerminationSpeedFraction`, `FrictionCoefficient`, `MaxSpeedLateralStatic`, `MaxSpeedLateralRunning`), live, held across the game's next copy (indoors/outdoors, a movement-mode change). A locomotion state's own MovementParams (Idle's turn cap) still win while that state is active. `false` as a value stops holding the field and puts back what the game copied. `locomotion` reads the hero's own value |
| `Refii.set_parry(p, window, wedge)` → bool · `Refii.parry(p)` → window, wedge | THIS hero's own copy of HeroCombatBalanceParams (made with the hero; the parry reads it each time): `InputWindowSecondsForParry` (seconds) and `MeleeParryWedgeAngle` (degrees), live, held for that player's next hero; nil leaves one |
| `Refii.expressions(p)` · `Refii.spells_known(p)` · `Refii.selected_expression(p)` | `{ {record = "0x…", id = "0x…", uses = n}, … }` the performer knows (nil without one; `id` is the record, what `perform_expression` takes) · `{ type, … }` the hero can assign (nil without a spell manager) · the record the expression wheel highlights while it is up for player p, or nil |
| `Refii.record_int(def, field)` | a GDB record's integer or enum property, inherited, through the game's typed getter — `Refii.record_int(def, "Category")`, `Refii.record_int(def, "WeaponType")`; nil for a record the database lacks or a property it has not got |
| `Refii.record_ref(def, field)` · `Refii.creature_tier(e)` | the record a reference property (type 6) names, `"0x…"` or nil; a creature's rank bits from its definition (`CREATURE_ELITE` 1, `CREATURE_LIEUTENANT` 2), nil without a creature component (see [Items, weapons, expressions and spells](gameplay.md#items-weapons-expressions-and-spells)) |
| `Refii.charge_spell(p, type, held)` | charges spell `type` through player p's own magic charge — its tiers lent with no `spell_pick`, the player's slots back once the charge is over (see [A live cast](gameplay.md#a-live-cast-its-parts-and-lifecycle)) |
| `Refii.record_number(def, field)` · `Refii.record_bool(def, field)` · `Refii.record_exists(def)` | a record's float (or int / enum) property as a number, its bool property, and whether the record exists, through the same lookup and typed getter; nil / false for a record or property the database lacks |
| `Refii.record_string(def, field)` | a GDB record's string property, inherited, through the game's own getter — `Refii.record_string(def, "NameTag")`, `Refii.record_string(def, "IconGraphic")`; nil for a record the database lacks or a property it has not got |
| `Refii.near(x, y, z, r [, creatures_only])` | handles within a radius, nearest first (at most 2048) |
| `Refii.line_of_sight(x1, y1, z1, x2, y2, z2)` | true when the game's physics sees nothing between |
| `Refii.spawn(def, x, y, z)` | a new entity through the game's own CreateEntityFromIDAtPosition: its handle, or nil |
| `Refii.entity(handle)` · `Refii.handle(entity)` | the game's own Entity userdata for a handle — what the shipped scripts and the game's natives take — and back |
| `Refii.play_fx(name, x, y, z [, options])` · `Refii.play_fx_on(e, name [, options])` | an effect of the particle bank or of an enabled mod's `fx/effects/`, drawn by Godot at a world position (the guest's axes) or riding an entity handle in that entity's own frame (it turns with it; `offset` is along its axes); `options = { duration = s, scale = n, tint = {r, g, b}, intensity = n, loop = false, offset = {x, y, z}, at = "Weapon.Weapon.Top" }` — `at` names a helper point of the entity's model (see [play_fx](effects.md#play_fx)); returns the id, or nil |
| `Refii.stop_fx(id)` | stops a played effect by any surface's id (native, Lua or Godot) |
| `Refii.mark(target, icon, text [, p])` · `Refii.unmark(id)` | a marker on the HUD's world-icon channel over an entity handle or at a point `{x, y, z}` for player p (nil: every player this machine plays): `icon` a game texture path or a mod PNG (`art/x.png`), `text` a line under it; the id `unmark` takes (see [Markers, fades and meters](ui-and-hud.md#markers-fades-and-meters)) |
| `Refii.world_meter(target, value, max [, style [, p]])` → id · `Refii.mark_meter(id, value, max [, style])` · `Refii.mark(target, icon, text, p, { meter = { value, max, style } })` | a bar on the marker channel, alone or as a marker's fill, restated in place by id (see [World meters](ui-and-hud.md#world-meters)) |
| `Refii.fade(p, dir [, seconds])` | fades player p's screen (0: every player played here) `"to_black"` / `"from_black"` (`"out"` / `"in"`) over seconds (nil: at once); the host's own layer, never the game's fade |
| `Refii.meter(id, label, value, max [, p [, style]])` · `Refii.meter_remove(id)` | a HUD meter (a timer, a boss bar) for player p in style `"bar"` (nil), `"boss"` or `"timer"` (the HUD's primitives), or `"generic"`, `"crucible"`, `"price"` (the game's own meter art, see [Markers, fades and meters](ui-and-hud.md#markers-fades-and-meters)); the same id restates it in place |
| `Refii.input_claim(p, action, on)` | claims (or releases) a mod's input action `<mod>.<action>` for player p (0: both): its buttons, triggers and keys leave the pad the guest sees — a chord's modifier too while held — while the mod's own `input_action` events still fire (see [Claims](settings-and-input.md#claims-taking-a-button-from-the-game)); true when posted |
| `Refii.press(p, button, held)` · `Refii.tap(p, button [, ticks])` | PRESSES a pad button on player 1 or 2's pad — the mirror of a claim: OR'd into that player's pad after the claims are withheld, so the guest reads it as the player's own pad, focused or not. `button` is input.json's name (`A` `B` `X` `Y` `LB` `RB` `L3` `R3` `Start` `Back` `DpadUp` …) or `LT` / `RT`; `held` false / 0 releases, a trigger is pulled by it (true / 1 full, 2..255 that much); `tap` holds for at least `ticks` whole guest ticks (default 1, at most 600). Cleared with the claims (see [Presses](settings-and-input.md#presses-pressing-a-button-for-the-player)); true when posted |
| `Refii.camera_mode(p)` · `Refii.fov(p)` | the camera the game has active for player p (`Refii.CAMERA_*`: 2 the third-person camera, 8 first person …; -1 none) and its field of view in degrees (70 the default); `camera_changed` follows each change (see [The camera](cutscenes.md#the-camera)) |
| `Refii.camera_override(p, mode [, {fov, pitch_limit, blend, aim}])` | holds a camera for player p through the game's own per-player hold — `Refii.CAMERA_FIRST_PERSON` or `CAMERA_GAME`, nil lets it go; it yields to a scene's own camera and comes back after, steps aside in full cutscenes and canned actions, and never reaches a save (see [The camera](cutscenes.md#the-camera)); true when stated |
| `Refii.camera_tune(p, {key = number, ..})` | tunes player p's cameras by name: `pitch_speed`, `pitch_min`, `pitch_max`, `move_damping`, `lock_strength`, `lock_turn_speed`; the gun aim's `ranged_tightness`, `ranged_offset`, `ranged_pitch_limit`; the held first-person view's `fp_view`, `fp_fov`, `fp_pitch_limit`, `fp_eye_forward`, `fp_eye_height`, `fp_face`, `fp_scenes`. `false` lets a key go, `nil` every key this mod stated — see [Shaping the camera](cutscenes.md#shaping-the-camera) |
| `Refii.camera_frame(p, {distance, interior_scale, height, focus_height, slot, track, levels})` · `Refii.camera_focus(p, {right, up, ease})` | the exploring camera's framing (one orbit distance, the focus pinned to the hero, the focus's and camera's heights; `levels = "all"` for every explore zoom level) and an over-the-shoulder slide of its focus; `nil` gives the game's own back |
| `Refii.camera_combat_blend(p, weight)` · `Refii.camera_interest(p, {foes, passers})` · `Refii.camera_pitch_hold(p, on [, {min, max, shared}])` | the weight toward the game's combat camera (0 none); whether foes and passers-by pull the frame; the manual pitch held where the stick put it |
| `Refii.camera_lock_frame(p, on [, {strength, tilt}])` · `Refii.camera_mode_allow(p, mode, allow)` | the hero's lock target framed without the look-at button held; whether the game may pick a camera mode for p by itself (`Refii.CAMERA_MAGIC_AIM`) |
| `Refii.camera_offset(p, {right, up, back, yaw, pitch, roll, fov})` | a DISPLAY-ONLY offset of player p's picture: metres along the drawn camera's right, up and back, degrees about its up, right and back, degrees added to the drawn angle; the game's camera, its readers and the save never see it; `nil` lets it go — see [Reading the camera](cutscenes.md#reading-the-camera) |
| `Refii.camera_shake(p, {kind, level, attack, decay, seconds, add, open})` · `Refii.camera_shake(p, {kind, fade})` | shakes player p's camera through the game's own screen shake (`Player.AddScreenShake` / `FadeScreenShake`): `kind` `banshee` `troll` `explosion` `quake` (the default) `magic_charge` `magic_cast` `combat` or the game's number, `level` (1), `attack` / `decay` (0.5) / `seconds` (1), `add` to add to that kind's level, `open` to hold it until faded; `fade` = seconds fades the kind out. Player two's own camera on a split screen, the shared camera on a shared one; `false` where player p's picture is not drawn on this machine — see [Shaking the camera](cutscenes.md#shaking-the-camera) |
| `Refii.camera_pose(p)` · `Refii.camera_stated(p)` | the pose player p's picture is drawn with this frame (`{x, y, z, fx, fy, fz, ux, uy, uz, fov, vfov, cut}`, guest axes, degrees) and the block the game's readers see at its last tick (`{x, y, z, fx, fy, fz, ux, uy, uz, fov, cut}`); nil where none |
| `Refii.camera_zoom(p)` | the exploring camera's zoom level in force: `set`, `level`, `combat`, `string_length`, `cage_radius`, `max_cage_radius`, `height`, `focal_offset`, `fov`, `max_coop_distance`; nil while the camera is not the exploring one |
| `Refii.camera_screen_mode()` · `Refii.camera_split()` | the co-op screen chosen — `"shared"`, `"split_vertical"`, `"split_horizontal"`, `"hybrid"` — and whether two views are drawn now |
| `Refii.camera_view(p)` · `Refii.camera_ray(p [, x, y])` | the part of the window p's picture fills (`x, y, w, h`, 0..1 from the top left) and the line through a point of p's view (0..1 from its top left; the centre when left out): `ox, oy, oz, dx, dy, dz`, guest axes |
| `Refii.hide_part(e, part, on [, p])` | hides a bone of an entity's rig in the drawing (by the name its bone table states, with every bone under it), or shows it again — in player p's view alone (1 or 2), or every view (absent or 0); drawing only, kept across rebuilds (see [Hidden parts](cutscenes.md#hidden-parts)); true when posted |
| `Refii.attack_mode(p)` | the keyboard's attack mode for player p — 1 melee, 2 ranged, 3 magic (`Refii.ATTACK_MODE_*`), set by the mode keys (Fable III's 1, 2, 3); 0 for a player no keyboard plays on this machine; `attack_mode_changed` follows each change (see [The keyboard's attack mode](settings-and-input.md#the-keyboards-attack-mode)) |
| `Refii.hud_icon(p, element, texture)` · `Refii.hud_slide(p, element, slide)` · `Refii.hud_hold(p, piece, on)` | a HUD override by element name for player p (0: both): the texture an element shows (a game texture path or a mod PNG, fitted to the slot; `""` clears), the slide it sits on as the game counts them (0 clears), a piece held visible (see [HUD overrides](ui-and-hud.md#hud-overrides-icons-slides-and-holds)); true when posted |
| `Refii.light(target, color, energy, range [, o])` · `Refii.light_remove(id)` | an omni light on an entity handle (in its own frame, riding the body; `o.at` a model helper, `o.offset` along its axes, `o.flicker` 0..1) or at a point `{x, y, z}`; `color = {r, g, b}`, `energy` 1 a placed lamp's, `range` metres (0: 6); the id `light_remove` takes, or nil (see [Lights](effects.md#lights)) |
| `Refii.send_to_partner(mod, name, payload)` | a mod's own message to the other machine of an online session, raised there as `partner_message` once and in order (see [Partner messages](handles-and-players.md#partner-messages)); true when posted, false alone or in local co-op |
| `Refii.signal(name, payload)` · `Refii.publish_text(key, text)` | to Godot ON THIS MACHINE (the same-machine twin of `send_to_partner`): `name` scoped `"<mod>.<name>"`, the payload any text up to 1024 bytes carried exactly (longer is cut, with a mod error); RefiiHost raises `mod_signal(mod, name, payload)` on its next frame. `publish_text` keeps a text Godot reads with `get_text(key)`; `value_changed(key, value)` fires on a change (see [Lua to Godot on one machine](handles-and-players.md#lua-to-godot-on-one-machine)); true when posted |
| `Refii.message_box(title, text [, accept [, cancel [, p]]])` | the game's own box for player p: a question when `cancel` is given (nil captions keep the box's own), else a notice; returns the id `mod_message_box` carries (see [Boxes](ui-and-hud.md#boxes-messages-notices-and-menu-boxes)) |
| `Refii.notice(title, text [, seconds [, p]])` | a notice for player p (nil: the menu owner here): the HUD's info box in game, queued behind the others; the id `mod_notice` carries (0 shown, -1 never) |
| `Refii.text(text [, a .. h])` | a mod's text in the culture the game runs in: a bare key (`QUICKSAVE_SAVED`) or each `{KEY}` inside a text, through the running book, the mods' `language/<culture>/text/book.babel.csv` and their en-uk rows; `%1` .. `%8` take the arguments; a key nothing knows is returned as written (see [Text keys](gdb-and-text.md#text-keys)) |
| `Refii.menu_box(title, text, options [, p [, cancelable]])` | the game's own menu box for player p: up to 12 options from the table, paged past six; B closes it unless `cancelable` is false; the id `mod_menu_box` carries with the option's index (-1 none) |
| `Refii.open_menu(name [, p])` | opens a mod's menu (`ui/templates.json`) for player p over the list that has the input, or on its own with none up; true when asked (see [A mod's own menus](ui-and-hud.md#a-mods-own-menus)) |
| `Refii.set_menu_rows(menu, rows [, p])` | states a menu list's rows for player p (nil: every player): strings or `{ name, label, command, ghosted }` — what a menu or entry with `"source": "rows"` shows, at once if open; `{}` clears; true when posted. Up to 32768 bytes of rows; past that they are cut at the last whole row and the mod errors list says so (see [Host lists](ui-and-hud.md#host-lists-a-mod-menu-can-source)) |
| `Refii.open_screen(id [, p])` | opens a host-owned screen for player p: the game's, or a screens record a mod states (id 1000+); true when posted |
| `Refii.frontend` | true in the front end's own Lua state (`mod/frontend.lua`) |
| `Refii.signed_in()` · `Refii.save_count()` · `Refii.screen()` | title screen only (`mod/frontend.lua`): the signed-in user, the profile's saves, the current presentation (see [The front-end surface](ui-and-hud.md#the-front-end-surface)) |
| `Refii.api_version` | the API version the chunk was composed for |
| `RefiiModSave.<key>` | state that persists with the save game |
| `RefiiModErrors` | every init and handler error, appended; a handler's error never stops the others |

## Native helpers

All C linkage in `core/refii_mod.h`. `ctx` is the event's or hook's context, `base` guest memory.
Objects are addresses natively.

- Heroes and entities: `refii_mod_player_hero(base, 1|2)`, `refii_mod_local_hero`,
  `refii_mod_player_of(base, entity)` (1, 2 or 0), `refii_mod_player_for(ctx, base, entity)` (who
  "the hero" is for it: a dog its master's, else the nearer living hero; 0 only with no hero),
  `refii_mod_world_here(base)` (this machine runs the shared world: alone, local co-op, the online
  host), `refii_mod_menu_player(base)` (whose book is open, 1 or 2), `refii_mod_component(base, entity, id)`,
  `refii_mod_opinion(base, villager, player, out[5])` (the villager's current axes for that hero),
  `refii_mod_focus(base, player)` (the living entity that hero is focused on, or 0),
  `refii_mod_wheel_switches_target(base, hero)` (that hero's target lock switches by the mouse
  wheel this tick, not by a mouse flick — state it each tick the lock holds; `Refii.wheel_switches_target`),
  `refii_mod_wheel_switches_target_sticky(ctx, base, hero, on)` (the same, restated by the tick until off or the lock ends),
  `refii_mod_coop_mode(base)` (0 alone, 1 local co-op, 2 online hosting, 3 online joined),
  `refii_mod_entity_alive`, `refii_mod_entity_position(ctx, base, entity, out[3])`,
  `refii_mod_set_position(ctx, base, entity, xyz)`, `refii_mod_facing` / `refii_mod_velocity(ctx, base,
  entity, out[3])` and `refii_mod_set_facing(ctx, base, entity, xyz)` (Physics.GetFacingVector /
  GetVelocity / SetFacingVector), `refii_mod_entity_definition` (the GDB
  record hash), `refii_mod_entity_name(base, entity, out, size)`,
  `refii_mod_relation(ctx, base, a, b)` (1 neutral, 2 ally, 4 enemy).
- Tint: `refii_mod_entity_tint(ctx, base, entity, r, g, b, a)`,
  `refii_mod_entity_untint(ctx, base, entity)` (see [Tint](gameplay.md#appearance-tint-alpha-scale)).
- The other appearance setters: `refii_mod_entity_alpha(ctx, base, entity, alpha)`,
  `refii_mod_entity_scale(ctx, base, entity, scale)`, `refii_mod_entity_saturation_mask(ctx, base,
  entity, on)`, `refii_mod_entity_drawable(ctx, base, entity, on)` (see [Appearance](gameplay.md#appearance-tint-alpha-scale)).
- The target lock: `refii_mod_lock_target(ctx, base, player, entity)`, `refii_mod_release_lock(ctx,
  base, player)`, `refii_mod_switch_target(ctx, base, player, left)`, `refii_mod_locked_target(ctx,
  base, player)` (see [The target lock](gameplay.md#the-target-lock)).
- Cutscenes: `refii_mod_in_cutscene(ctx, base, player)` — the `REFII_CUTSCENE_*` bits holding now
  for player 1 or 2 (0: either) (see [Cutscenes](cutscenes.md)).
- Status effects: `refii_mod_apply_status(ctx, base, entity, kind, seconds, level, source)`,
  `refii_mod_clear_status(ctx, base, entity, kind)`, `refii_mod_has_status(base, entity, kind)`;
  kinds `REFII_STATUS_ELECTRIFIED` 1, `REFII_STATUS_CHAOS` 2, `REFII_STATUS_BURNING` 3,
  `REFII_STATUS_VORTEX` 4 (see [Status effects](gameplay.md#status-effects)).
- Knockback and stagger: `refii_mod_knockback(ctx, base, entity, dir, force)`,
  `refii_mod_stagger(ctx, base, entity)` (see [Knockback and stagger](gameplay.md#knockback-and-stagger)).
- Movement speed: `refii_mod_set_speed_multiplier(base, player, factor)`,
  `refii_mod_clear_speed_multiplier(base, player)`, `refii_mod_speed_multiplier(base, player, float*
  out)` (see [Movement speed](gameplay.md#movement-speed)).
- Actions: `refii_mod_current_action(base, entity)`, `refii_mod_action_elapsed(ctx, base, action,
  float* out)`, `refii_mod_action_interruptible(base, action)`, `refii_mod_allow_action_interrupt(base,
  action)`, `refii_mod_allow_action_interrupt_at(ctx, base, action, seconds)`; `refii_mod_entity_living(base, entity)` (see [Actions and commitment](gameplay.md#actions-and-commitment)).
- What an action is: `refii_mod_action_type(base, action)` (EScriptableAction, -1 unknown),
  `refii_mod_action_name(base, action, out, size)` (its enum name),
  `refii_mod_scriptable_action_name(type)` (see [Actions and commitment](gameplay.md#actions-and-commitment)).
- The front end: `refii_mod_signed_in_user(base)` (the signed-in pad's user index, -1 none),
  `refii_mod_save_count(ctx, base)` (the profile's saves on every device, -1 with no front end),
  `refii_mod_frontend_screen(ctx, base, out, size)` (the current presentation's name) — see
  [The front-end surface](ui-and-hud.md#the-front-end-surface).
- The slot table: `refii_mod_spell_in_slot(base, player, slot)` (0..7, -1 none).
- A live cast: `refii_mod_spell_parts(ctx, base, serial, refii_mod_cast_parts* out)` (see [A live cast](gameplay.md#a-live-cast-its-parts-and-lifecycle)). Who a vortex holds: `refii_mod_vortex_held(base, serial, uint32_t* out, int32_t max)` → how many (-1 no live vortex). One power of a spell as its class's data read loads it: `refii_mod_spell_balance(ctx, base, caster, type, level, refii_mod_cast_balance* out)` (`status_seconds`, `status_spread`, `duration`, `swords`; -1 a field the class lacks). One cast's serial on both machines: `refii_mod_host_serial(base, serial)` / `refii_mod_local_serial(base, host_serial)` (0 on the joiner while unpaired).
- Sound: `refii_mod_play_sound_on(ctx, base, entity, cue, tag)`, `refii_mod_play_sound_at(ctx,
  base, xyz, cue, tag)`, `refii_mod_stop_sound(ctx, base, entity, tag, fade_ms)`,
  `refii_mod_play_music(ctx, base, name)`, `refii_mod_stop_music(ctx, base, name)` (see [Sound](gameplay.md#sound)).
- Animation: `refii_mod_play_animation(ctx, base, entity, name, loops, blend_seconds, speed)`,
  `refii_mod_stop_animation(ctx, base, entity)` (see [Animation](gameplay.md#animation)).
- Opinion and deeds: `refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta)`,
  `refii_mod_add_deed(ctx, base, villager, hero, deed)` (see [Opinion and deeds](gameplay.md#opinion-and-deeds)).
- NPC behaviour: `refii_mod_npc_follow(ctx, base, entity, target, radius)`, `refii_mod_npc_go_to(ctx,
  base, entity, xyz)`, `refii_mod_npc_flee(ctx, base, entity, from)`, `refii_mod_npc_attack(ctx, base,
  entity, target)`, `refii_mod_npc_set_hostile(ctx, base, entity, other, on)`, `refii_mod_npc_stop(ctx,
  base, entity)` (see [NPC behaviour](gameplay.md#npc-orders)).
- Time and weather: `refii_mod_set_time_of_day(ctx, base, hours)`, `refii_mod_time_of_day(base,
  &hours)`, `refii_mod_set_weather(ctx, base, theme, weight, seconds)` (see [Time and weather](gameplay.md#time-and-weather)).
- Health and stats: `refii_mod_health(base, entity, &cur, &max)`, `refii_mod_set_health` (a direct
  write), `refii_mod_modify_health(ctx, base, entity, delta, attacker)` (the game's own Modify:
  heals, hurts, kills), `refii_mod_ability_level(base, entity, type)`,
  `refii_mod_experience(base, entity, pool, &out)`, `refii_mod_ability_cost`,
  `refii_mod_give_experience`, `refii_mod_spend_experience`.
- Spells: `refii_mod_cast_spell(ctx, base, caster, type, targeted, target, level, pos, dir)` (a
  real spell through the game's factory, no will cost), `refii_mod_spell_serial`,
  `refii_mod_live_spell(ctx, base, type, caster)`, `refii_mod_cast_spell_with(ctx, base, caster, type,
  targeted, target, level, pos, dir, &options)` (`refii_mod_cast_options { tag, damage_scale, at_kind, at_entity, at_point }`: `at_kind` `REFII_MOD_CAST_AT_ENTITY` / `_POINT` places the cast by its class's rule, see [Casting at a target or a point](gameplay.md#casting-at-a-target-or-a-point)).
- Gold and items: `refii_mod_gold`, `refii_mod_give_gold`, `refii_mod_item_count(base, entity,
  definition)`, `refii_mod_add_item(ctx, base, entity, definition)`, `refii_mod_add_items(ctx, base, entity,
  definition, count, &last)` (1..100, each the game's own add).
- The world: `refii_mod_world`, `refii_mod_level_name(base, out, size)`.
- Stats, physique, inventory, weapons: `refii_mod_renown`, `refii_mod_morality`, `refii_mod_purity`,
  `refii_mod_can_save(ctx, base, player)` / `refii_mod_save_game(ctx, base, player)` (the game's own save),
  `refii_mod_modify_renown(ctx, base, entity, delta)` / `refii_mod_modify_morality` /
  `refii_mod_modify_purity(ctx, base, entity, float delta)` (the game's own Stats.Modify*),
  `refii_mod_physique`, `refii_mod_age`, `refii_mod_inventory_stack(base, entity, index, &def,
  &count)`, `refii_mod_equipped(base, entity, &melee, &ranged)`.
- Murgo's Mirror: `refii_mod_appearance(ctx, base, player, &look)` (`refii_mod_appearance_t { automatic,
  tan, fat, height, strength, skill, will, morality, purity, age }`), `refii_mod_set_appearance(ctx, base,
  player, &inputs)` (a NaN input keeps how it looks), `refii_mod_set_appearance_automatic(ctx, base, player, on)`.
- Mod morphs: `refii_mod_morph_id("<mod>.<name>")`, `refii_mod_morph_list(out, capacity)`,
  `refii_mod_morph_registered(id, &default, &min, &max)`, `refii_mod_morph_weight(player, id, &weight)`,
  `refii_mod_set_morph_weight(player, id, weight)` (the store's `morph.<id>`, clamped, saved with the hero).
- The quick menu's actions, by player: `refii_mod_use_item(ctx, base, player, definition)`,
  `refii_mod_equip_weapon(ctx, base, player, definition)` (an online joiner's pick crosses to the
  co-op kit under `REFII_MOD_HOST_HERO_KIND`), `refii_mod_perform_expression(ctx, base, player,
  expression)`, `refii_mod_assign_spell(ctx, base, player, spell)`, and with flags
  `refii_mod_quick_action(ctx, base, player, kind, record, flags)` (`REFII_QUICK_UNGATED`,
  `REFII_QUICK_HELD`), `refii_mod_release_expression(ctx, base, player)`; the sprint,
  `refii_mod_sprinting(base, player)` / `refii_mod_end_sprint(ctx, base, player)`; per-hero tuning,
  `refii_mod_set_locomotion(ctx, base, player, key, value)` / `refii_mod_locomotion(ctx, base,
  player, key, &value)` and `refii_mod_set_parry(ctx, base, player, window, wedge)` /
  `refii_mod_parry(ctx, base, player, &window, &wedge)`; what they choose from:
  `refii_mod_expression(base, player, index, &record, &uses)`, `refii_mod_spells_known(base,
  player, &mask)`, `refii_mod_selected_expression(base, player)`; and a record's inherited string,
  `refii_mod_record_string(ctx, base, definition, field, out, size)` and its integer or enum,
  `refii_mod_record_int(ctx, base, definition, field, &out)`, its number `refii_mod_record_number(ctx, base,
  definition, field, &out)`, its bool `refii_mod_record_bool(…, &out)`, its reference
  `refii_mod_record_ref(ctx, base, definition, field, &out)` and `refii_mod_record_exists(ctx, base,
  definition)`; a creature's rank `refii_mod_creature_tier(base, entity)`; a chosen spell's charge
  `refii_mod_charge_spell(ctx, base, player, type, held)` (see
  [Items, weapons, expressions and spells](gameplay.md#items-weapons-expressions-and-spells)).
- Space and spawning: `refii_mod_entities_near(ctx, base, xyz, radius, creatures_only, out, max)`,
  `refii_mod_line_of_sight(ctx, base, from, to)`, `refii_mod_spawn(ctx, base, definition, xyz)`.
- Effects (Godot draws them; the guest spawns nothing): `refii_mod_play_fx(name, xyz, options)`,
  `refii_mod_play_fx_on(name, entity, options)`, returning an id,
  and `refii_mod_stop_fx(id)`. `options` is a `refii_mod_fx_options` — `duration` (seconds; 0 until
  stopped, or with `loop` 0 until the effect has run out), `scale`, `tint[3]`, `intensity`, `loop`,
  `offset[3]` (from the entity, along the entity's own axes) — or null for `refii_mod_fx_default_options()`. Any
  thread. `options.at` (or `refii_mod_play_fx_at(name, entity, helper, options)`) names
  a helper point of the entity's model — see [play_fx](effects.md#play_fx).
- The HUD (Godot draws it; the guest is not told): `refii_mod_mark(player, entity, xyz, icon, text)`
  / `refii_mod_unmark(id)`, `refii_mod_world_meter(player, entity, xyz, value, max, style)` /
  `refii_mod_mark_meter(id, value, max, style)` (see [World meters](ui-and-hud.md#world-meters)), `refii_mod_fade(player, to_black, seconds)`,
  `refii_mod_meter(player, id, label, value, max, style)` / `refii_mod_meter_remove(id)` — see
  [Markers, fades and meters](ui-and-hud.md#markers-fades-and-meters). Any thread.
- Input claims, presses and HUD overrides (Godot applies them): `refii_mod_input_claim(player, action, on)`
  — see [Claims](settings-and-input.md#claims-taking-a-button-from-the-game); `refii_mod_press(player, button,
  held)` / `refii_mod_tap(player, button, ticks)` — see [Presses](settings-and-input.md#presses-pressing-a-button-for-the-player); `refii_mod_hud_icon(player,
  element, texture)` / `refii_mod_hud_slide(player, element, slide)` / `refii_mod_hud_hold(player, piece, on)`
  — see [HUD overrides](ui-and-hud.md#hud-overrides-icons-slides-and-holds). Any thread.
- The camera: `refii_mod_camera_mode(player)` (`REFII_CAMERA_*`, -1 none), `refii_mod_camera_override(player, mode,
  fov, pitch_limit, blend, flags)` (0 or less / below 0 the camera's own; flags `REFII_CAMERA_HOLD_AIM`),
  `refii_mod_camera_fov(base, player)` — see [The camera](cutscenes.md#the-camera). Guest thread.
  `refii_mod_camera_state(ctx, base, player, call, arguments)` makes any shape call (`"camera_tune"`,
  `"{pitch_speed=0.09}"`; `"camera_offset"` and `"camera_shake"` too) — see [Shaping the camera](cutscenes.md#shaping-the-camera). Guest tick thread.
  `refii_mod_camera_pose(player, out12)`, `refii_mod_camera_stated(player, out11)`, `refii_mod_camera_view(player,
  out4)`, `refii_mod_camera_screen_mode(split)` (`REFII_CAMERA_SCREEN_*`), `refii_mod_camera_ray(player, x, y, out6)`
  read the camera — see [Reading the camera](cutscenes.md#reading-the-camera). Any thread.
  `refii_mod_hide_part(entity, part, on)` / `refii_mod_hide_part_for(entity, part, on, player)` (1 or 2 that player's view
  alone, 0 every view) — see [Hidden parts](cutscenes.md#hidden-parts). Any thread.
- The keyboard's attack mode: `refii_mod_attack_mode(player)` (1 melee, 2 ranged, 3 magic —
  `REFII_ATTACK_MODE_*` — 0 for a player no keyboard plays here) — see [The keyboard's attack mode](settings-and-input.md#the-keyboards-attack-mode).
  Guest thread.
- Lights: `refii_mod_light(entity, xyz, rgb, energy, range, options)` / `refii_mod_light_remove(id)`
  — an omni light on an entity (its own frame; `options.at` a model helper, `options.offset`,
  `options.flicker`) or at a world point, see [Lights](effects.md#lights). Any thread.
- The partner: `refii_mod_send_to_partner(mod, name, payload)` — a mod's own message to the other
  machine of an online session, raised there as PartnerMessage, see [Partner messages](handles-and-players.md#partner-messages). Any thread.
- Godot on this machine: `refii_mod_signal(name, payload)` (RefiiHost's `mod_signal`) and
  `refii_mod_publish_text(key, text)` (`get_text`, `value_changed`) — `REFII_MOD_SIGNAL_NAME_MAX` 96,
  `REFII_MOD_SIGNAL_PAYLOAD_MAX` 1024; see [Lua to Godot on one machine](handles-and-players.md#lua-to-godot-on-one-machine). Any thread.
- Typed player values: `refii_mod_player_text(player, key, out, capacity)` (the length, -1 for none),
  `refii_mod_player_set_text` / `refii_mod_player_save_text(player, key, text)`,
  `refii_mod_player_hash(player, key, uint32_t* out)` / `refii_mod_player_save_hash(player, key, hash)` —
  `REFII_MOD_PLAYER_TEXT_MAX` 256; see [Typed values](handles-and-players.md#typed-values-texts-and-hashes).
- Handles and hashes: `refii_mod_handle(address)` / `refii_mod_handle_address(handle)` (what Lua
  and Godot hold for an object, and back), `refii_mod_fnv1(text)` (the game's FNV-1 of a name).
- The game VM and the save: `refii_mod_run_game_lua` (any length), `refii_mod_game_lua_int`,
  `refii_mod_lua_generation`, `refii_mod_save_set_int`, `refii_mod_save_get_int`.
- Godot: `refii::mods::Publish / Published / Setting` (mod values and settings).
- Guest memory and calls: `refii::mods::LoadU8/U16/U32/F32`, `StoreU8/U32/F32`, `CallGuest`,
  `CallVirtual`, `Frame`, `GetVector`, `SetVector`.
- The plain tick: `REFII_MOD_ON_TICK(fn)` — the same moment as the Tick event.

## Godot: RefiiHost

`Engine.get_singleton("RefiiHost")` in a mod's `godot/*.gd`. Entities are handles, as in Lua.
See [Godot scripts](events.md#godot-scripts) for signals, verdicts and how queries answer.
An argument written `name = value` may be left off, from the right: `notice(title, text)` is
`notice(title, text, 0, 0)` and `in_cutscene()` is `in_cutscene(0, 7)`.

### Members and signals

| Member | Meaning |
|---|---|
| signal `mod_event(name, e)` | every event; `e` is a Dictionary of the event's fields (int, float, bool or String) |
| signal `<lua_name>(e)` | one per catalog event: `hit`, `spell_launched`, `level_loaded`, ... (the table above) |
| `get_value(key) -> float` | a hard mod's published value (`combat.stamina`); NaN when none. signal `value_changed(key, value)` fires when a published number (any surface's `publish`, a hook's `refii::mods::Publish`) or text changes — `value` a float or a String — so no script polls |
| `get_text(key) -> String` · `has_text(key) -> bool` · `publish_text(key, text) -> bool` · signal `mod_signal(mod, name, payload)` | a published TEXT (`Refii.publish_text`, `refii_mod_publish_text`, or this `publish_text`: at most 1024 bytes, `""` for none) and a mod's SIGNAL on this machine (`Refii.signal("<mod>.<name>", payload)`: `mod` is the name's part before its first dot; also `mod_event("mod_signal", e)`) — see [Lua to Godot on one machine](handles-and-players.md#lua-to-godot-on-one-machine) |
| `setting(key, default) -> float` | a `settings.json` value, the live override first |
| signal `setting_changed(key, value)` | a mod setting changed live (`set_setting`, the Options › Mods page, the debug channel), once in force; only on a real change |
| `set_setting(key, value)` | sets a mod setting in the running game, kept for the session |
| `run_lua(chunk) -> bool` | runs a Lua chunk in the game VM on the next tick, newlines kept; false when the line is over 4 KB |
| `hero(player) -> int` | the player's hero entity as the bus last stated it, or 0. Player 1 is the HOST's hero on both machines: on an online joiner `hero(1)` is the partner |
| `local_hero() -> int` · `coop_mode() -> int` · `menu_player() -> int` · `player_here(player) -> bool` | `Refii.local_hero()`, `Refii.coop_mode()`, `Refii.menu_player()`, `Refii.player_here(p)` as the guest last answered them (asked every frame while a script is loaded, one guest tick behind): this machine's hero (in local co-op the menu owner's), 0 alone 1 local co-op 2 hosting 3 joined, whose menus are open, whether this machine plays player p's hero (both alone and in local co-op, player one hosting, player two joined) |
| `achievement_unlocked(player, id) -> bool` · `achievements(player) -> int[]` · `character_id(player) -> String` | `Refii.achievement_unlocked`, `Refii.achievements`, `Refii.character_id`: the same statement Lua reads, on both co-op machines alike |
| `player_set(p, key, v)` · `player_add(p, key, d, saved)` · `player_save(p, key, v)` | per-player values in the store `Refii.player_set` / `_add` / `_save` write — the same on both co-op machines; saved ones go home with the hero (see [Player state](handles-and-players.md#the-player-store)) |
| `player_value(player, key, fallback = NaN) -> float` · `player_text(player, key) -> String` · `player_value_hash(player, key) -> String` · `player_set_text(p, key, text)` · `player_save_text(p, key, text)` · `player_save_hash(p, key, hash) -> bool` | a player's store value NOW, synchronously (the store a `player:` UI source reads); NaN or the fallback when unset · its typed text and hash NOW, synchronously, as `Refii.player_value_text` / `player_value_hash` read them (`""` for none; the hash as `"0x%08x"`) · typed texts and hashes written into the same store (`query player_text P KEY` also reads them back) — see [Typed values](handles-and-players.md#typed-values-texts-and-hashes) |
| `mods() -> Array[String]` | the enabled mods, in load order |
| signal `mod_menu_row(e)` | a row of a mod's menu was chosen: `menu`, `row`, `player` (1 or 2, whose menu it is), `mod` (the mod that stated the row or its menu, `""` for a shipped row). Raised by Godot where the row is chosen, so it arrives the same frame, on the title screen too |
| `open_menu(name) -> bool` | opens a menu a mod defines in `ui/templates.json` as a list over whichever menu list has the input — the front end, the pause list or a screen's list; B returns to it. False when no list is up (plain gameplay) or no template has that name |
| `message_box(title, text, buttons) -> int` | the game's own box: `buttons` empty or one caption is a notice (one dismiss), two captions a question (A and B, captioned by `buttons[0]` and `buttons[1]`). `title` sits above `text`. Returns an id; the answer comes as `message_box_answered(id, button)`. In game only (the front end has no host box) |
| signal `message_box_answered(id, button)` | `button` 0 for A (a question's accept, a notice's dismiss), 1 for B, -1 only when the box was withdrawn before it was answered. One question and one notice stand at a time, the rest queued in order: a second question is asked once the first is answered, a notice told under a notice follows it |
| `publish(key, value)` | publishes a mod value under `key` (`"<mod>.<name>"`, no spaces): what a HUD piece reads as `mod:<key>`, Lua as `Refii.value(key)`, native code as `refii::mods::Published`. Set on the guest's next tick, the moment every reader sees it |
| `play_fx(name, x, y, z, options) -> int` | an effect of the bank or a mod's `fx/effects/` as a `FableFx` node at a world position (guest axes); `options` is a Dictionary: `duration`, `scale`, `tint` (Color or `[r, g, b]`), `intensity`, `loop` (true); the id, 0 for an unknown effect |
| `play_fx_on(name, handle, options) -> int` | the same on an entity by its HANDLE: it stands in the entity's own frame, `offset` (Vector3 or `[x, y, z]`) along the entity's axes, and moves and turns with it, freed with it; `"at": "Weapon.Weapon.Top"` stands it at that helper point of the model (see [play_fx](effects.md#play_fx)) |
| `stop_fx(id) -> bool` | stops any surface's id; false when nothing plays under it |
| `mark(target, icon, text = "", player = 0) -> int` · `unmark(id) -> bool` · `markers() -> Array` | a marker on the HUD's world-icon channel over an entity handle (int) or at a Vector3 point for player 1 or 2 (0: every player this machine plays); `icon` a game texture path or a mod PNG; the id `unmark` takes; `markers()` lists what stands (id, player, entity, x, y, z, icon, text) |
| `world_meter(target, value, max, style = "", player = 0) -> int` · `mark_meter(id, value, max, style = "") -> bool` | a bar on the marker channel, alone or a marker's fill restated in place (see [World meters](ui-and-hud.md#world-meters)); `mark(target, icon, text, player, options)` takes `{ "meter": { "value", "max", "style" } }` |
| `fade(player, to_black, seconds = 0) -> bool` · `fade_state(player) -> Dictionary` | fades the player's screen to black or back over seconds (the host's own layer over their half, never the game's fade); `fade_state` answers `alpha` (0 clear .. 1 black), `target`, `seconds` |
| `meter(id, label, value, max, player = 0, style = "") -> bool` · `meter_remove(id) -> bool` · `meters() -> Array` | a HUD meter for player 1 or 2 (0: every player played here) in style `bar`, `boss` or `timer` (the HUD's primitives), or `generic`, `crucible`, `price` (the game's own meter art); the same id restates it in place; `meters()` lists what stands in stack order |
| `claim_input(player, action, on = true) -> bool` · `input_claimed(player, action) -> bool` · `input_claims(player) -> Array` | claims (or releases) a mod's input action `<mod>.<action>` for player 1 or 2 (0: both): its buttons, triggers and keys leave the pad the guest sees — a chord's modifier too while held — while the `input_action` signal still fires; `input_claimed` / `input_claims` read the state (see [Claims](settings-and-input.md#claims-taking-a-button-from-the-game)) |
| `press(player, button, held) -> bool` · `tap(player, button, ticks) -> bool` · `presses(player) -> Array` | presses a pad button on player 1 or 2's pad — the guest reads it as the player's own — `held` false / 0 releasing, a trigger pulled by it; `tap` for at least `ticks` guest ticks; `presses` lists what is held (see [Presses](settings-and-input.md#presses-pressing-a-button-for-the-player)) |
| `camera_mode(player) -> int` · `fov(player) -> float` · signal `camera_changed(e)` | the camera the game has active for player 1 or 2 (CAMERA_*; -1 none), as the bus last stated it; the field of view of their live camera in degrees, read at the call (see [The camera](cutscenes.md#the-camera)) |
| `camera_offset(player, values, owner = "")` · `camera_shake(player, values)` · `camera_pose(player)` · `camera_stated(player)` · `camera_zoom(player)` · `camera_screen_mode()` · `camera_split()` · `camera_view(player) -> Rect2` · `camera_ray(player)` / `camera_ray(player, x, y)` | as Lua's, Dictionaries of Vector3s (guest axes) and numbers; `camera_zoom` as the bus last stated it (see [Reading the camera](cutscenes.md#reading-the-camera)) |
| `camera_override(player, mode, options = {}) -> void` | holds a camera for player 1 or 2 (8 first person, 2 the third-person camera, 0 lets go); `options` `{ fov, pitch_limit, blend, aim }` (see [The camera](cutscenes.md#the-camera)) |
| `camera_tune(player, values, owner = "") -> bool` · `camera_frame(player, values, owner = "") -> bool` · `camera_focus(player, values, owner = "") -> bool` · `camera_combat_blend(player, weight, owner = "") -> bool` · `camera_interest(player, values, owner = "") -> bool` · `camera_pitch_hold(player, on, options = {}, owner = "") -> bool` · `camera_lock_frame(player, on, options = {}, owner = "") -> bool` · `camera_mode_allow(player, mode, allow, owner = "") -> bool` | the camera's shape for player 1 or 2, the same calls and keys as Lua's (`values` a Dictionary, `null` lets it go); `owner` names the mod so two scripts' statements stand side by side — see [Shaping the camera](cutscenes.md#shaping-the-camera). Stated on the guest's next tick |
| `hide_part(handle, part, on, player = 0) -> bool` | hides a bone of an entity's rig in the drawing, by handle and bone name, or shows it again — in player 1 or 2's view alone, or every view (0) (see [Hidden parts](cutscenes.md#hidden-parts)) |
| `attack_mode(player) -> int` | the keyboard's attack mode for player 1 or 2, read at the call: 1 melee, 2 ranged, 3 magic; 0 for a player no keyboard plays here; signal `attack_mode_changed(e)` follows each change (see [The keyboard's attack mode](settings-and-input.md#the-keyboards-attack-mode)) |
| `hud_icon(player, element, texture) -> bool` · `hud_slide(player, element, slide) -> bool` · `hud_hold(player, piece, on = true) -> bool` · `hud_overrides(player) -> Dictionary` | a HUD override by element name for player 1 or 2 (0: both): the texture an element shows (a game texture path or a mod PNG, fitted to the slot; `""` clears), the slide it sits on (0 clears), a piece held visible; `hud_overrides` answers `icons`, `slides`, `holds` (see [HUD overrides](ui-and-hud.md#hud-overrides-icons-slides-and-holds)) |
| `light(target, color, energy, range, options = null) -> int` · `light_remove(id) -> bool` · `lights() -> Array` | an omni light on an entity handle (int; its own frame, riding the body) or at a Vector3 / `[x, y, z]` point (guest axes); `color` a Color or `[r, g, b]`, `energy` (1: a placed lamp's), `range` metres (0: 6); `options` `{ offset, at, flicker }` as play_fx_on's `offset` / `at`, `flicker` 0..1; the id `light_remove` takes, 0 for an unknown handle; `lights()` lists what stands (id, entity, r, g, b, energy, range, x, y, z) — see [Lights](effects.md#lights) |
| `send_to_partner(mod, name, payload = "") -> bool` · signal `partner_message(e)` | a mod's own message to the other machine of an online session (the guest command `partner_send`), raised there as `partner_message` — `mod`, `name`, `payload`, `from` — once and in order; false alone or in local co-op, for a bad name or a payload over 256 bytes (see [Partner messages](handles-and-players.md#partner-messages)) |
| `api_version() -> int` | the API version |
| `text(text, args = []) -> String` | a mod's text in the culture the game runs in — the lookup `Refii.text` makes: a bare key or each `{KEY}` inside a text, then `%1`, `%2` … from `args`; returned as written when nothing knows it (see [Text keys](gdb-and-text.md#text-keys)) |
| `notice(title, text, seconds = 0, player = 0) -> id` · signal `notice_done(id, outcome)` | a notice on the channel the game's co-op notices use — in game the HUD's info box for `seconds` (0: its own length), on the front end its dialog — queued behind the others; `outcome` 0 once shown, -1 when it never showed |
| `menu_box(title, text, options, player = 0, cancelable = true) -> id` · signal `menu_box_answered(id, option)` | the game's own menu box, on the front end and in game, for player 1 or 2 (0: the menu owner here): up to 16 options, paged past six; `option` is the index chosen, -1 for B, a box replacing it, or a player played on the other machine |
| `set_menu_rows(menu, rows, player = 0)` | states a menu list's rows for player 1 or 2 (0: every player), replacing the last: what a `ui/templates.json` menu or entry with `"source": "rows"` (or `rows:<list>`) shows, at once if it is open. A row is a string or `{ name, label, command, ghosted }`; `label` a Babel tag or literal, `command` any row verb; `[]` clears. `"source": "values:mod:<key>\|<format>=<label>,..."` lists published values instead, and `inventory:usable`, `expressions` and `spells_known` list what the hero holds (see [Host lists](ui-and-hud.md#host-lists-a-mod-menu-can-source)) |
| `open_screen(screen, player = 0) -> bool` | opens a host-owned screen by id — the `Screen:open:<id>` verb any row can name: the game's (1013 achievements, 1014 multiplayer) or a `ui/templates.json` screens record a mod states (id 1000+). It stands over whichever in-game list opened it — the pause list, a screen's list, a mod menu over either or a mod's own menu — takes the whole pad while it stands, and B returns to that list |
| `register_command(verb, handler) -> bool` | a row verb every list runs (the front end, the pause book, screen lists, mod menus, a screen's own buttons): a row whose command is `verb` — or begins with it when it ends in `:` — calls `handler(argument, menu, row, player)`; `register_command(verb, handler, true)` calls `handler(argument, e)` instead, `e` the row as a `mod_menu_row` Dictionary with its typed `subject_kind`, `definition` and `spell` — the table a `Mod:lua:` function is called with, so a host list's pick arrives typed; false refuses the row. The game's verbs (`PauseTab:`, `Screen:open:`, `Opens:`, `Mod:lua:`, `setting:`, `Inventory:`, `Ability:learn\|unlearn:<1..14>`, `Shop:buy\|sell:<ticket>` …) stay the game's |
| `register_source(name, rows) -> bool` | a ROW SOURCE every list's template may name (`"source": "<name>[:<argument>]"` on an entry or a menu): `rows(argument)` returns an Array of Dictionaries, one row each — `name` (required), `label`, `icon`, `command` (the verb the row runs on A), `rating` (0..10), `ghosted`, `group` (a header the row sits under), `emphasised`, `counted`, `unviewed`. Called whenever the list is built or re-sourced. The game's sources stay the game's |
| `register_condition(name, holds) -> bool` | a ROW CONDITION entries may name in `when`: `holds()` answers whether the rows naming it show, asked when a list is built. The game's conditions stay the game's |
| `on_verdict(event, callable) -> bool` · `remove_verdict(event, callable)` | a [verdict](events.md#verdicts) handler inside the seam |
| `save_snapshot(key) -> bool` · `snapshot_time(key) -> int` · `load_snapshot(key) -> bool` · signal `save_written(e)` | a copy of this machine's player-one save under `key` (`"<mod>.<name>"`), kept beside the save and never in the load list: `save_snapshot` copies the save as it stands on disk — take it on `save_written` (`save`, `player`), after the game's own save; `snapshot_time` answers when it was taken (unix seconds, 0 for none); `load_snapshot` loads it back in the running game through the game's own fail-quest reload and puts the save folder's own files back once the level stands. False before a save is started or written, for a bad key or no snapshot; `load_snapshot` also in co-op and while a load is in flight. See [Snapshots](saves-and-sharing.md#snapshots-a-quicksave-and-a-quickload) |
| `node_for(handle) -> Node3D` | the node Godot draws the entity with, or null; parent to it, never move it |
| `now() -> float` · `real_now() -> float` · `after(seconds, fn, key = "")` · `cancel(key) -> bool` | the game clock and one-shot timers, by Lua's rule (see [The runtime](events.md#the-runtime-clock-timers-values-pools)): counted from the same tick and paused statements, from this host's start, so durations agree with Lua's; `fn` a Callable run on the main thread on the first tick the clock passes now + seconds; a key (a String) replaces the timer under it and is the answer, else the timer's number |
| `entity_value(handle, key, default = null)` · `entity_set(handle, key, value)` | a value per entity handle, forgotten at that entity's `entity_removed`; null clears. Each surface keeps its own values |
| `pool(player, key, options = null) -> bool` · `pool_value(player, key) -> Array` · `pool_spend(player, key, n, callback = null) -> bool` | the Lua runtime's own regenerating pool, declared from Godot (`options` `{ max, floor, regen, delay, max_key }`, numbers), so a pool both surfaces name is one pool; `pool_value` reads `[value, max]` from the player store now (empty for no maximum stated); `pool_spend` spends on the guest's next tick, `callback(taken)` later |
| `ask(what, args, callback) -> int` | `query` with its answer handed to `callback(result)` instead of `reply`: the callable form of every guest read Lua makes at the call |
| `read(what, args) -> Dictionary` | a guest read answered AT ONCE from inside an `on_verdict` handler — the Dictionary `ask`'s callback would get (`ok` and the verb's fields), as a Lua handler's `Refii.*` read. Only the `query` verbs that change nothing (not `spawn`, not `lua_int`); anywhere else `{ ok = false, error }` and nothing is read. See [Reading inside a verdict](events.md#reading-inside-a-verdict) |
| `call_lua(expression, callback) -> bool` | runs a Lua expression once in the game VM and calls `callback({ ok, values })` on a later frame: values its first five results (a number, a text, a boolean or null) — the answering form of `run_lua` |
| `signal_lua(name, payload = "") -> bool` | raises Lua's `mod_signal { mod, name, payload }` in the game VM on the next tick — the other way of `Refii.signal` |
| `set_appearance(player, inputs)` · `set_appearance_automatic(player, on)` · signal `appearance_changed(e)` | Murgo's Mirror: the player's look to manual with the Dictionary's named inputs (`tan`, `fat`, `height`, `strength`, `skill`, `will`, `morality`, `purity`, `age`), the rest as the hero looks · back to the game's own look (true) or the held manual one (false); read it with the query `appearance P`. See [Appearance (Murgo's Mirror)](gameplay.md#appearance-murgos-mirror) |
| `morph_get(player, morph) -> float` · `morph_set(player, morph, weight)` · `morph_id(name) -> String` · signal `morph_changed(e)` | a mod's own morph for the player: the weight read from the store at the call (NaN for one no enabled mod declares) · set, clamped, saved with the hero (the line `morph_set P MORPH W`) · its type as `"0x…"`; the query `morph P MORPH` answers `weight`, `registered`. See [Mod morphs](gameplay.md#mod-morphs) |
| `hash(name) -> String` · `split_screen() -> bool` · `forgive_crimes(villager, player)` | `Refii.hash`, `Refii.split_screen` (as the guest last answered), `Refii.forgive_crimes` |
| `ask_partner(asker, question, callback)` · `partner_answer(ticket, callback)` · `key_taken(handle, player, callback)` · `spell_serial(spell, callback)` · `save_get(key, callback)` | `Refii.ask_partner`, `partner_answer`, `key_taken`, `spell_serial` and a `RefiiModSave` value, run once in the game VM: `callback(value)` on a later frame |
| `world_here() -> bool` | answers at once from the last `coop_state` |

### Helper calls

Lines the guest tick runs; fire-and-forget, silent when there is nothing to act on. Queries answer
later on `reply(id, result)`; every verb is in [Commands and queries](reference-commands.md#godot-queries).

| Member | Meaning |
|---|---|
| `give_gold(entity, delta)` | adds (negative: takes) gold |
| `modify_health(entity, delta, attacker)` | changes health, attributed to `attacker` (0 for none) |
| `set_health(entity, value)` | sets current health |
| `add_item(entity, definition)` | one of an item definition (its GDB record hash) into the inventory |
| `add_item(entity, definition, count)` | `count` (1..100) of it, each through the game's own add |
| `use_item(definition) -> bool` | uses one of an item definition (`"0x…"` or a number) from this machine's hero's inventory, as the Items page's Use does, with no menu open: a potion drinks, food is eaten, `item_used` fires. Nothing happens when the hero carries none or the item states no use; false when the definition does not parse |
| `give_experience(entity, pool, amount)` / `spend_experience(...)` | one experience pool |
| `save_game(player)` | the game's own save where player 1 or 2 may save (`AutoSaveGame(true, true)`); `save_written` follows per package; `query can_save P` answers `able` |
| `modify_renown(entity, delta)` / `modify_morality(entity, delta)` / `modify_purity(entity, delta)` | the hero stats through the game's own Stats.Modify* (the events fire; morality and purity change only where the session is decided) |
| `cast_spell(caster, type, targeted, target, level)` · `cast_spell_at(caster, type, targeted, target, level, pos, dir)` | a cast as `caster`; `target` 0 for none, level 0..4; `cast_spell_at` starts it at `pos` (Vector3, guest axes) facing `dir`, as `refii_mod_cast_spell`'s pos / dir (the line: `cast_spell C T TARGETED TARGET LEVEL [X Y Z [DX DY DZ]]`) |
| `cast_spell_with(caster, type, targeted, target, level, options)` | a cast with `options` `{ tag, damage_scale, pos, dir, at }` (the line `cast_spell_with C T TARGETED TARGET LEVEL TAG SCALE [X Y Z [DX DY DZ]] [at E \| at X Y Z]`): the tag rides the cast's events, its hits deal damage × `damage_scale` where the hit is decided; `at` (an entity handle or a Vector3 point) places the cast by its class's rule, `pos` then the origin ([Casting at a target or a point](gameplay.md#casting-at-a-target-or-a-point)) |
| `set_position(entity, x, y, z)` | moves an entity |
| `set_facing(entity, x, y, z)` | turns an entity through `Physics.SetFacingVector` (a hero's `Player.SetFacingVector`; level: the game drops z); `query facing E` / `query velocity E` answer `x`, `y`, `z` |
| `entity_tint(entity, r, g, b, a = 1)` / `entity_untint(entity)` | tints an entity through the game's own appearance setter (see [Tint](gameplay.md#appearance-tint-alpha-scale)); untint puts back 1, 1, 1, 1 |
| `entity_alpha(entity, alpha)` / `entity_scale(entity, scale)` / `entity_saturation_mask(entity, on)` / `entity_drawable(entity, on)` | the other setters of the same renderable (see [Appearance](gameplay.md#appearance-tint-alpha-scale)) |
| `lock_target(player, entity = 0)` / `release_lock(player)` / `switch_target(player, left)` | the target lock as the game's LT states it (see [The target lock](gameplay.md#the-target-lock)); `query locked_target P` answers the target held |
| `inventory_items(player) -> Array` / `inventory_action(player, operation, definition) -> bool` | what player 1 or 2's hero carries (0 the player whose menus are open here) as the game's own inventory pages show it, and what those pages do to an item; either player's, whoever's menus are open (the other player's action in local co-op only) (see [The inventory as a screen reads it](gameplay.md#the-inventory-as-a-screen-reads-it)) |
| `dress_model(node, mesh)` | gives a MeshInstance3D in a script's own world this mesh and the lit material the game's item previews use (see [A screen of your own](ui-and-hud.md#a-screen-of-your-own)) |
| `item_pose(item, yaw)` | how the game's item page holds an item of `inventory_items(player)` after `yaw` radians of its spin, as a Basis: at 0 its front faces a camera looking down -Z (see [A screen of your own](ui-and-hud.md#a-screen-of-your-own)) |
| `own_screen(player, on, script)` | a mod's screen stands where the pause book would, for player 1 or 2 (0 the menus' player); the screen is the calling script's (`self`) mod's, and only it turns it off; released by the engine when the mod set reloads, the game is left or a level unloads (see [A screen of your own](ui-and-hud.md#a-screen-of-your-own)) |
| `input_glyph(name) -> Texture2D` / `input_label(name) -> String` | the prompt for a mod action (`<mod>.<action>`) or a game pad button (`A`, `Start`, `LT` …) as the game's prompts draw it now: the bound key's keycap on the keyboard and mouse, the bound button in the player's glyph style and pack on a pad; null / empty with nothing bound (see [Prompts for your own actions](settings-and-input.md#prompts-for-your-own-actions)) |
| `touch_area(action, rect) -> bool` / `touch_mode() -> bool` / `touch_stick() -> Vector2` | a touchscreen area that presses a mod action for player one, whether the touchscreen is the device in use, and the on-screen move stick (see [On a touchscreen](settings-and-input.md#on-a-touchscreen)) |
| `move_stick(player) -> Vector2` | player 1 or 2's move stick as the game is handed it: player 1's keys, touch stick and pad, player 2's own pad (see [Settings and input](settings-and-input.md)) |
| `use_item(player, definition)` / `equip_weapon(player, definition)` / `perform_expression(player, expression)` / `assign_spell(player, spell)` | the quick menu's own actions for player 1 or 2's hero (see [Items, weapons, expressions and spells](gameplay.md#items-weapons-expressions-and-spells)); `query expressions P` answers `count`, `e1..eN` (records), `u1..uN` (uses), `query spells_known P` `mask`, `count`, `query selected_expression P` `expression` (ok false with no wheel up), `query record_string DEF FIELD` `value` |
| `use_item(player, definition, options)` / `equip_weapon(player, definition, options)` / `perform_expression(player, expression, held [, options])` / `assign_spell(player, spell, options)` / `release_expression(player)` | the quick actions with options: `{"ungated": true}` skips the quick menu's gate (still refused for a dead hero and in a cutscene) — `use_item(player, definition, {"ungated": true})` is the per-player form of the Items page's `use_item(definition)`; `held` keeps an expression going until `release_expression`. `perform_expression(player, expression, held, options, callback) -> bool` runs Lua's own `Refii.perform_expression` and `callback(performed)` follows on a later frame |
| `charge_spell(player, type, held)` · `charge_spell(player, type, held, callback) -> bool` | charges spell `type` through player 1 or 2's own magic charge (see the Lua row above); with `callback` it runs Lua's own `Refii.charge_spell` and `callback(taken)` follows on a later frame — false when the charge was refused |
| `end_sprint(player)` · `set_locomotion(player, values)` · `set_parry(player, window, wedge)` | the sprint's own end; this hero's own locomotion fields (`{"MaxTurnSpeedStatic": 720}`, `false` stops holding one) and parry window / wedge (negative leaves one) — see the Lua rows above; `query sprinting P` answers `sprinting`, `query locomotion P KEY` `value`, `query parry P` `window`, `wedge` |
| `in_cutscene(player = 0, kinds = 7) -> bool` / `cutscene_kinds(player = 0)` | the cutscene event as this host last heard it (heard while any script is loaded; a new host seeds it with `query in_cutscene P`): whether any of `kinds` holds for player 1 or 2 (0: either), and the bits (see [Cutscenes](cutscenes.md)); `query in_cutscene P` answers `kinds` from the guest now |
| `in_play(player) -> bool` | no pause, menu, level change or load (as this host last heard the paused and level events), the player's hero standing, no full cutscene for them |
| `constants() -> Dictionary` · `spell_info(type) -> Dictionary` | the named numbers (`SPELL_*` …) · a spell type's `name_tag` and `icon` |
| `wheel_switches_target(handle, on = true) -> bool` | hero `handle`'s target lock switches by the mouse wheel, as `Refii.wheel_switches_target` states each tick — STICKY here: a Godot script runs off the guest tick, so the tick restates it (the command `wheel_switches_target E ON`) on every tick from the call until `on = false` or the hero's player no longer holds a lock (the game's own release, or `release_lock`); false for a bad handle |
| `apply_status(entity, kind, seconds, level = 0, source = 0)` / `clear_status(entity, kind)` | a status through the spell's own installer: 1 electrified, 2 chaos, 3 burning (see [Status effects](gameplay.md#status-effects)); `query has_status E KIND` asks |
| `knockback(entity, x, y, z, force = 1)` / `stagger(entity)` | a Force Push's knockdown hit along a direction, or a flinch (see [Knockback and stagger](gameplay.md#knockback-and-stagger)) |
| `set_speed_multiplier(player, factor)` / `clear_speed_multiplier(player)` | the player component's own movement-speed override (see [Movement speed](gameplay.md#movement-speed)); `query speed_multiplier P` answers `overridden`, `factor` |
| `allow_action_interrupt(action)` | grants an action's ALLOW_ACTION_INTERRUPT (see [Actions and commitment](gameplay.md#actions-and-commitment)); `query current_action E` answers `action`, `elapsed`, `interruptible` |
| `allow_action_interrupt_at(action, seconds)` | the grant once the action's own clock reaches `seconds` (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `minimap(player) -> Dictionary` / `minimap_point(x, y, z) -> Vector2` | the current region's map as the game's own minimaps plot it, for player 1 or 2's hero (0 this machine's): `known`, `proper` (it has a real map, so the places are stated), `texture`, `texture_path`, `north` (radians the image turns), `half_size` (from the pause root's file), `hero`, `facing`, `quests` (the other quests' objectives, from the quest log of the hero whose menus this machine shows). The extent is the level's own heightfields', the places the entity feed's. Places are the image's element units around its centre, Y up; `minimap_point` puts a guest world position on the same map (a dog's from `player_dog`, an enemy's from `near`), null with no proper map |
| `query action A` / `query frontend` | `type`, `name`, `elapsed`, `interruptible` — EScriptableAction and its name, the action's clock and its interrupt permission (ok false for an action the factory never saw; see [Actions and commitment](gameplay.md#actions-and-commitment)) · `signed_in`, `saves`, `screen` — the front end's own state (ok false with no front end up; see [The front-end surface](ui-and-hud.md#the-front-end-surface)) |
| `query living E` / `query spell_in_slot P SLOT` / `query spell_parts SERIAL` | `living` · `type` · `spell, type, targeted, level, caster, caster_player, vortex, anchor, sword_count, s1..s8, x, y, z, has_origin, exploded` (ok false for an unknown cast) |
| `query vortex_held SERIAL` | `count, v1..v16` — who the cast's live vortex holds (0 past count; ok false when no live vortex carries the serial) |
| `query spell_balance C T L` | `status_seconds, status_spread, duration, swords` — power L of spell T for caster C as its Targeted class's data read loads it (-1 a field the class lacks; ok false for another type or a caster with no spell manager) |
| `query host_serial SERIAL` / `query local_serial SERIAL` | `serial` — the host's serial of this machine's spell / this machine's spell for the host's (ok false while the cast is not paired) |
| `play_sound(cue, entity, tag = "")` / `play_sound_at(cue, x, y, z, tag = "")` / `stop_sound(entity, tag, fade_ms = 0)` / `play_music(name)` / `stop_music(name)` | an audio cue through the game's own cue player, stopped by tag; a music cue through SoundTools' own bodies (see [Sound](gameplay.md#sound)) |
| `play_animation(entity, name, loops = 1, blend_seconds = 0.1, speed = 1)` / `stop_animation(entity)` | a named animation as the shipped AI plays one (see [Animation](gameplay.md#animation)) |
| `modify_opinion(villager, hero, axis, delta)` / `add_deed(villager, hero, deed)` | a villager's opinion of a hero through the game's own setter, or a deed through its own message (see [Opinion and deeds](gameplay.md#opinion-and-deeds)); `deed` the record's hash |
| `npc_follow(entity, target, radius = 2)` / `npc_go_to(entity, x, y, z)` / `npc_flee(entity, from)` / `npc_attack(entity, target)` / `npc_set_hostile(entity, other, on)` / `npc_stop(entity)` | orders a creature as the shipped quests do, through the game's own entries (see [NPC behaviour](gameplay.md#npc-orders)) |
| `set_time_of_day(hours)` / `set_weather(theme, weight = 1, seconds = 1)` | the game clock through its own setter; the GAME's environment theme blended as the quests do (see [Time and weather](gameplay.md#time-and-weather)); `query time_of_day` answers `hours` |
| `save_set(key, int)` | `RefiiModSave.<key>`, persisted with the save |
| `query(what, args) -> int` | asks; the id the answer carries |
| signal `reply(id, result)` | the answer: `ok` plus the fields of [the query](reference-commands.md#godot-queries) |

## Native: guest memory

For native code reading the game directly (`refii::mods::LoadU8/U16/U32/F32`,
`StoreU8/U32/F32`, `CallGuest`, `CallVirtual`, `Frame`, `GetVector`, `SetVector`). Prefer a helper
where one exists.

Every entity component keeps its owner entity at +4. `refii_mod_component(base, entity, id)` finds
a component by id. Entity: +0x1C definition hash, +0x7C placement, +0x80 name (a pointer to a body
whose first word is the text), +0x90 bit 0x40 in world.

| Id | Component | Fields the API reads |
|---|---|---|
| 3 | graphic appearance | +80..+95 tint {r, g, b, a}, +108 scale, +112 alpha |
| 13 | inventory | +16 list sentinel (node: +0 next, +8 item; item: +4 definition, +12 count), +20 count |
| 20 | creature | +0x18 alive |
| 24 | held items | slots by name |
| 28 | weapon set | +84 melee record, +88 ranged record |
| 31 | hit receiver | the hit dispatch |
| 33 | player | +4 owner |
| 36 | health | +0x14 current, +0x1C max, +0x131 invulnerable, +0x133 can't die |
| 49 | action manager | +0x10 current action |
| 56 | spell manager | +0x80 slot table |
| 57 | stats | +0x110 + 4 × type: ability levels |
| 77 | targeting | +0x14 force lock |
| 81 | purse | +12 gold |
| 133 | faction | +0x14 faction id |
| 164 | experience | +0x10 pools float[4]: General, Strength, Skill, Will |

Hit record: +0x18 knockdown, +0x19 unblockable, +0x38 damage, +0x44 serial, +0x48 level, +0x54
spell type (-1 melee). Spell object: +0x40 level, +0x44 serial.
