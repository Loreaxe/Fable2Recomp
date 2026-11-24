using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using Refii.Host;
using Refii.Save;

namespace Refii.Mods;

/// <summary>
/// THE SINGLETON A MOD'S GDSCRIPT TALKS TO: <c>Engine.get_singleton("RefiiHost")</c>, registered
/// for the life of a game session (<see cref="ModGodot"/>). Every mod event the payload's bus
/// posts is drained once per frame and emitted as the signal named by the event's Lua name
/// (<c>hit</c>, <c>spell_launched</c>, <c>level_loaded</c> ... the catalog's
/// <see cref="ModApiEvent.LuaName"/>) with one Dictionary argument of the event's fields, and
/// as <c>mod_event(name, e)</c> for every kind. The host asks the bus only for the kinds some
/// script is connected to: the mask is recomputed from the live connections each frame, and
/// holds only the engine's own kinds when no mod script is loaded.
/// </summary>
/// <remarks>
/// Member names are snake_case on purpose: Godot exposes C# members to GDScript under their
/// C# names, so this is what a script sees. The signal delegates are declared statically (the
/// generator needs them) and named after the catalog; <see cref="SignalNames"/> is the list a
/// test holds to <see cref="ModApi.Events"/>, in kind order. A Dictionary value is an int
/// (entity ids, players, kinds), a float, a bool or a String; an entity is a number, compared
/// with <c>hero(player)</c>.
/// <para>
/// No member takes a C# default parameter: Godot's script bridge registers none and dispatches
/// by name and argument count, so a GDScript call that leaves one off finds no method. Each
/// documented short form (<c>notice(title, text)</c> for <c>notice(title, text, seconds = 0,
/// player = 0)</c>) is an overload of its own, held to the catalog and the guide by RefiiHostTests.
/// </para>
/// </remarks>
[GlobalClass]
public partial class RefiiHost : Node
{
	/// <summary>Every event, with its name: the one connection for a script that filters itself.</summary>
	[Signal] public delegate void mod_eventEventHandler(string name, Godot.Collections.Dictionary e);

	/// <summary>A <see cref="query"/>'s answer: the id the query returned, and <c>ok</c> plus the results.</summary>
	[Signal] public delegate void replyEventHandler(long id, Godot.Collections.Dictionary result);

	// One signal per catalog event, in kind order; the delegate name minus "EventHandler" is the
	// signal name GDScript connects to. Held to ModApi.Events by RefiiHostTests.
	[Signal] public delegate void tickEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hero_readyEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hero_goneEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void lua_readyEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void save_loadedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void game_actionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void ability_requestEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void melee_swingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void roll_can_startEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void roll_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void blockEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void anim_eventEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hitEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hit_landedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hit_responseEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_chargeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_charge_levelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_launchEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_launchedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_createdEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void experienceEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void entity_diedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hero_downedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void damageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void level_loadedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void level_unloadingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void game_savingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void item_addedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void item_usedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void held_itemEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weapon_equippedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void ability_levelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void goldEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void relationEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void input_actionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void quest_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void quest_endedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void quest_objectiveEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dialogueEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void entity_spawnedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void entity_removedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void expression_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void renownEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void moralityEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void purityEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void marriageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dog_trickEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void ranged_shotEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void status_effectEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void target_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void opinion_deedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void opinion_modifiedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void time_of_dayEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weather_themeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void sleep_menuEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void travelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void fast_travelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void menu_openedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void menu_closedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void menu_screenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weather_regionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void sleep_chosenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void slow_timeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void summonedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void crimeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void achievementEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void collectable_takenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void partner_consentEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void property_boughtEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void property_soldEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void rent_paidEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void property_rentedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void property_conditionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void divorceEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void widowedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void child_bornEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void gift_givenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void gift_acceptedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void engagedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void followEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void furniture_upgradedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void redecoratingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void household_budgetEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void tracked_statEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void partnershipEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void household_restoredEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dynamic_weatherEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_menu_rowEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_message_boxEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_noticeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_menu_boxEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_abilityEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_pickEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void partner_messageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void look_at_buttonEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void vortex_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void vortex_endedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void fireball_impactEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void pausedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void start_gameEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void cutsceneEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void health_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void status_applyingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void lock_lostEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void attack_mode_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void item_removedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void expression_wheelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_endedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_charge_endedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void melee_swing_endedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void action_move_interruptEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void camera_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void vortex_caughtEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void appearance_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void morph_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void guild_sealsEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void treasuryEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void kingdom_decisionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void chest_openedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void item_boughtEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void item_soldEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void shop_priceEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dig_spot_dugEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void demon_doorEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void drinkEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void sanctuaryEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void sanctuary_requestEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weapon_level_upEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weapon_augment_unlockedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weapon_upgrade_readyEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hand_holdingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hand_holding_requestEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void interaction_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void interaction_resultEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void interaction_modeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void relationship_stageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dog_digEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dog_leadingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dog_trick_learnedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dog_learnedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dive_spot_usedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void vomitEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void job_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void job_levelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void job_earningsEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void job_workedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void job_multiplierEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void job_summaryEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void guild_messageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void title_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void title_unlockedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void chapter_progressEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void scar_addedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void scar_removedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void age_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hero_stageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void clothing_wornEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void clothing_removedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void clothing_dyedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void pub_game_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void pub_game_finishedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void village_wealthEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void gargoyle_destroyedEventHandler(Godot.Collections.Dictionary e);

	/// <summary>A <see cref="message_box"/>'s answer: the id it returned, and the button's index (0 is A, 1 is B; -1 when another box replaced it).</summary>
	[Signal] public delegate void message_box_answeredEventHandler(long id, long button);

	/// <summary>A <see cref="notice"/> has gone: the id it returned, 0 once shown, -1 when it never showed.</summary>
	[Signal] public delegate void notice_doneEventHandler(long id, long outcome);

	/// <summary>A <see cref="menu_box"/>'s answer: the id it returned and the chosen option's index (-1 for none).</summary>
	[Signal] public delegate void menu_box_answeredEventHandler(long id, long option);

	/// <summary>
	/// The save_written event (kind 103): a save package written, or a failed write — <c>save</c>
	/// (its folder name, <c>Hero000</c>), <c>player</c> (1, or 2 for player two's own package) and
	/// <c>ok</c>. Raised after the host stamped the package's sidecar (the guest posts that first),
	/// so a <see cref="save_snapshot"/> of the new save can be taken from it.
	/// </summary>
	[Signal] public delegate void save_writtenEventHandler(Godot.Collections.Dictionary e);

	/// <summary>
	/// A mod's SIGNAL on this machine (<c>Refii.signal(name, payload)</c>, <c>refii_mod_signal</c>): the
	/// mod (the scoped name's part before its first dot), the rest of the name, and the payload text
	/// exactly as sent (at most 1024 bytes). Also raised through <c>mod_event("mod_signal", e)</c> with
	/// <c>mod</c>, <c>name</c>, <c>payload</c>. Godot only; the next frame after the call.
	/// </summary>
	[Signal] public delegate void mod_signalEventHandler(string mod, string name, string payload);

	/// <summary>
	/// A published value CHANGED: a number (<c>publish</c> on any surface, a hook's
	/// refii::mods::Publish — the runtime posts each change while a script is loaded) or a text
	/// (<c>publish_text</c>). <paramref name="value"/> is a float or a String. Fires only on a change,
	/// so a script needs no poll.
	/// </summary>
	[Signal] public delegate void value_changedEventHandler(string key, Variant value);

	/// <summary>
	/// A mod setting changed live (<see cref="set_setting"/>, the Options › Mods page, the debug
	/// channel's <c>mod-setting</c>): its key and new value, once the value is in force. Godot only;
	/// Lua hears <c>setting_changed</c>, native code reads the value at the call.
	/// </summary>
	[Signal] public delegate void setting_changedEventHandler(string key, float value);

	/// <summary>The per-event signal names, in kind order: the static twin of the catalog's Lua names.</summary>
	public static readonly string[] SignalNames =
	{
		"tick", "hero_ready", "hero_gone", "lua_ready", "save_loaded", "game_action",
		"ability_request", "melee_swing", "roll_can_start", "roll_started", "block", "anim_event",
		"hit", "hit_landed", "hit_response", "spell_charge", "spell_charge_level", "spell_launch",
		"spell_launched", "spell_created", "experience", "entity_died", "hero_downed", "damage",
		"level_loaded", "level_unloading", "game_saving", "item_added", "item_used", "held_item",
		"weapon_equipped", "ability_level", "gold", "relation", "input_action", "quest_started",
		"quest_ended", "quest_objective", "dialogue",
		"entity_spawned", "entity_removed", "expression_started", "renown", "morality", "purity",
		"marriage", "dog_trick", "ranged_shot", "status_effect", "target_changed", "opinion_deed",
		"opinion_modified", "time_of_day", "weather_theme", "sleep_menu", "travel", "fast_travel",
		"menu_opened", "menu_closed", "menu_screen", "weather_region", "sleep_chosen", "slow_time", "summoned",
		"crime", "achievement", "collectable_taken", "partner_consent",
		"property_bought", "property_sold", "rent_paid", "property_rented", "property_condition", "divorce", "widowed", "child_born", "gift_given", "gift_accepted", "engaged", "follow", "furniture_upgraded", "redecorating", "household_budget", "tracked_stat", "partnership", "household_restored",
		"dynamic_weather",
		"mod_menu_row",
		"mod_message_box",
		"mod_notice", "mod_menu_box",
		"mod_ability", "spell_pick",
		"partner_message",
		"look_at_button", "vortex_started", "vortex_ended", "fireball_impact",
		"paused", "start_game",
		"cutscene",
		"health_changed", "status_applying", "save_written", "lock_lost",
		"attack_mode_changed",
		"item_removed", "expression_wheel",
		"spell_ended", "spell_charge_ended", "melee_swing_ended", "action_move_interrupt",
		"camera_changed",
		"vortex_caught",
		"appearance_changed",
		"morph_changed",
		"guild_seals",
		"treasury",
		"kingdom_decision",
		"chest_opened", "item_bought", "item_sold", "shop_price", "dig_spot_dug", "demon_door", "drink",
		"sanctuary", "sanctuary_request", "weapon_level_up", "weapon_augment_unlocked", "weapon_upgrade_ready", "hand_holding",
		"hand_holding_request", "interaction_started", "interaction_result", "interaction_mode", "relationship_stage", "dog_dig",
		"dog_leading", "dog_trick_learned", "dog_learned", "dive_spot_used", "vomit", "job_started",
		"job_level", "job_earnings", "job_worked", "job_multiplier", "job_summary", "guild_message",
		"title_changed", "title_unlocked", "chapter_progress", "scar_added", "scar_removed", "age_changed",
		"hero_stage", "clothing_worn", "clothing_removed", "clothing_dyed", "pub_game_started", "pub_game_finished",
		"village_wealth", "gargoyle_destroyed",
	};

	public const string SingletonName = "RefiiHost";

	/// <summary>The <c>lua_ready</c> event's kind (REFII_EVENT_LUA): every mod's init.lua has run again.</summary>
	public const int LuaReadyKind = 3;
	public const string ModEventSignal = "mod_event";

	/// <summary>The cutscene event's kind: the host hears it while any script is loaded, for <see cref="in_cutscene"/>.</summary>
	private const int CutsceneKind = 100;

	/// <summary>The camera_changed event's kind: the host hears it while any script is loaded, for <see cref="camera_mode"/>.</summary>
	private const int CameraChangedKind = 112;

	/// <summary>The kinds <see cref="in_play"/> is composed from: paused, level_unloading, level_loaded.</summary>
	private const int PausedKind = 98;
	private const int LevelUnloadingKind = 25;

	/// <summary>The tick's kind: its seconds drive <see cref="now"/> and the <see cref="after"/> timers.</summary>
	private const int TickKind = 0;

	/// <summary>The entity_removed kind: it forgets that handle's <see cref="entity_value"/>s.</summary>
	private const int EntityRemovedKind = 40;

	/// <summary>
	/// The kinds the host keeps in the mask while any script is loaded: hero_ready and hero_gone feed
	/// <see cref="hero"/>, cutscene feeds <see cref="in_cutscene"/>, paused and the level edges feed
	/// <see cref="in_play"/> and <see cref="now"/>, the tick feeds <see cref="now"/>.
	/// </summary>
	private static readonly ModEventMask HeroKinds = new ModEventMask().With(TickKind).With(1).With(2).With(CutsceneKind)
		.With(PausedKind).With(LevelUnloadingKind).With(LevelLoadedKind).With(CameraChangedKind);

	/// <summary>The game clock and the <see cref="after"/> timers, on the tick and paused statements Lua's runtime counts.</summary>
	private readonly ModClock<Callable> _clock = new();

	/// <summary>The <see cref="entity_value"/> store, forgotten per handle at entity_removed.</summary>
	private readonly ModEntityStore<Variant> _entityStore = new();

	/// <summary>The callables of <see cref="ask"/>'s queries in flight, by id.</summary>
	private readonly Dictionary<long, Callable> _askCallbacks = new();

	/// <summary>The answers <see cref="call_lua"/> waits for, by id.</summary>
	private readonly Dictionary<long, Action<ModLuaLane.Reply>> _luaCalls = new();
	private long _nextLuaCall;

	/// <summary>A Lua handler subscribed to value_changed: published numbers are stated to Lua too.</summary>
	private bool _luaWatchesValues;

	/// <summary>Published values to state to Lua at the end of the frame (texts always, numbers while watched).</summary>
	private readonly List<(string Key, object Value, bool Text)> _luaValues = new();

	/// <summary>The pools a Godot script declared, restated to a new VM: (player, key) to the chunk and the max key.</summary>
	private readonly Dictionary<(int Player, string Key), (string Chunk, string MaxKey)> _pools = new();

	/// <summary>The guest's last coop_state answer for <see cref="split_screen"/>.</summary>
	private bool _splitScreen;

	/// <summary>The camera mode per player as the bus last stated it (camera_changed, seeded by the <c>camera_mode</c> queries a new host sends); absent is -1.</summary>
	private readonly Dictionary<long, int> _cameraMode = new();

	/// <summary>Ids of the two <c>camera_mode</c> queries a new host sends, answered into <see cref="_cameraMode"/>.</summary>
	private readonly Dictionary<long, long> _cameraSeedQueries = new();

	/// <summary>The pause modes and the level load as the bus last stated them, for <see cref="in_play"/>.</summary>
	private readonly ModPlayState _play = new();

	/// <summary>The cutscene statements as the bus last stated them (the cutscene event, seeded by the <c>in_cutscene</c> queries a new host sends).</summary>
	private readonly ModCutscene _cutscene = new();

	/// <summary>Ids of the two <c>in_cutscene</c> queries a new host sends, answered into <see cref="_cutscene"/>.</summary>
	private readonly Dictionary<long, long> _cutsceneSeedQueries = new();

	/// <summary>The kind a query's reply arrives under (REFII_MOD_HOST_REPLY_KIND); never an event.</summary>
	public const int ReplyKind = 1000;

	/// <summary>The kind a published number's change arrives under (REFII_MOD_HOST_VALUE_KIND, core/payload_bridge.h).</summary>
	public const int ValueKind = 1010;

	/// <summary>A command line's limit: the tick drains into <c>char line[4096]</c> (mod_commands.cpp).</summary>
	public const int CommandLimit = 4096;

	private long _nextQueryId;

	/// <summary>Signal per kind, from the catalog; null where the catalog names a signal this class does not declare.</summary>
	private readonly StringName?[] _signalByKind;
	private readonly StringName[] _nameByKind;
	private readonly Dictionary<long, long> _hero = new();
	private ModEventMask _mask;
	private bool _maskSent;

	/// <summary>The guest's last <c>coop_state</c> answer: <see cref="local_hero"/>, <see cref="coop_mode"/>, <see cref="menu_player"/>.</summary>
	private long _localHero;
	private int _coopMode;
	private int _menuPlayer = 1;

	/// <summary>The id of the <c>coop_state</c> query in flight, answered into the fields above (0: none).</summary>
	private long _coopStateQuery;

	/// <summary>Frames the query in flight has waited; a line the guest never ran (a session change) is asked again.</summary>
	private int _coopStateWait;
	private const int CoopStateRetryFrames = 120;

	/// <summary>The drain's records and each record's fields, kept: _Process reads them and holds neither past the record.</summary>
	private readonly List<(int Kind, string Fields)> _drained = new();
	private readonly List<KeyValuePair<string, object>> _plain = new();

	/// <summary>mod_event's name per kind, made on first use: a Variant of text costs a tracked object each time.</summary>
	private readonly Variant[] _eventNames;

	/// <summary>The argument lists of an event's emits; the emit reads them and keeps nothing.</summary>
	private readonly Variant[] _emitOne = new Variant[1];
	private readonly Variant[] _emitTwo = new Variant[2];

	/// <summary>The node the loaded scripts live under; no children means no script is loaded.</summary>
	public Node? ScriptsRoot { get; set; }

	/// <summary>The enabled mods, in load order, as <see cref="mods"/> reports them.</summary>
	public string[] EnabledMods { get; set; } = Array.Empty<string>();

	public RefiiHost()
	{
		_signalByKind = new StringName?[ModApi.Events.Count];
		_nameByKind = new StringName[ModApi.Events.Count];
		_eventNames = new Variant[ModApi.Events.Count];
		foreach (ModApiEvent e in ModApi.Events)
		{
			_nameByKind[e.Kind] = new StringName(e.LuaName);
			if (HasSignal(e.LuaName))
			{
				_signalByKind[e.Kind] = _nameByKind[e.Kind];
			}
			else
			{
				GD.PushError($"[mods] godot: RefiiHost declares no signal '{e.LuaName}' (kind {e.Kind}); " +
					"it reaches scripts through mod_event only");
			}
		}
	}

	/// <summary>Ids of the two hero queries a new host sends, answered into <see cref="_hero"/>.</summary>
	private readonly Dictionary<long, long> _heroSeedQueries = new();

	/// <summary>
	/// A host made after the heroes were announced (a "Reload mods" builds a new one) asks for them:
	/// hero_ready fires only when a hero changes, so it would otherwise answer 0 until then.
	/// </summary>
	public override void _Ready()
	{
		// The save gate's verdict on start_game is always installed (ModSaveGate).
		PushVerdictMask();
		for (long player = 1; player <= 2; player++)
		{
			long id = query("hero", new Godot.Collections.Array { player });
			if (id != 0)
			{
				_heroSeedQueries[id] = player;
			}

			// A scene already playing when this host was made raised its edge before the host heard.
			long cutscene = query("in_cutscene", new Godot.Collections.Array { player });
			if (cutscene != 0)
			{
				_cutsceneSeedQueries[cutscene] = player;
			}

			// The camera the player already had when this host was made: camera_changed fires on a change only.
			long camera = query("camera_mode", new Godot.Collections.Array { player });
			if (camera != 0)
			{
				_cameraSeedQueries[camera] = player;
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// Verdicts: handlers that run INSIDE the game's seam and can cancel or change the event
	// ---------------------------------------------------------------------------------------

	private static readonly object VerdictLock = new();
	private static readonly Dictionary<int, List<Callable>> Verdicts = new();

	/// <summary>The one callback the payload holds; a static field keeps the delegate alive.</summary>
	private static readonly HostBridge.ModVerdictFn VerdictCallback = OnVerdict;

	private static bool _verdictInstalled;

	/// <summary>True on the guest thread while its verdict handlers run: where <see cref="read"/> answers.</summary>
	[ThreadStatic]
	private static bool _inVerdict;

	/// <summary>
	/// Registers a VERDICT handler for an event (its Lua name, e.g. "hit"): <c>handler(e)</c> runs
	/// on the GAME'S thread inside the seam, before the game acts, with the same dictionary the
	/// signal carries. Change a writable field in <c>e</c> (<c>e.damage *= 2</c>, <c>e.knockdown =
	/// true</c>) or set <c>e.cancel = true</c> / return true to refuse, where the event allows it.
	/// The handler must only compute and read (<see cref="read"/> answers the game's state at once
	/// there): no nodes, no scene tree (use call_deferred for that), and it should be quick — the game
	/// waits for it. Returns false for an unknown event.
	/// </summary>
	public bool on_verdict(string eventName, Callable handler)
	{
		ModApiEvent? e = ModApi.Find(eventName);
		if (e is null)
		{
			GD.PushWarning($"[mods] godot: on_verdict: no event '{eventName}'");
			return false;
		}

		lock (VerdictLock)
		{
			if (!Verdicts.TryGetValue(e.Kind, out List<Callable>? list))
			{
				list = new List<Callable>();
				Verdicts[e.Kind] = list;
			}

			list.Add(handler);
		}

		PushVerdictMask();
		return true;
	}

	/// <summary>Removes a verdict handler registered with <see cref="on_verdict"/>.</summary>
	public void remove_verdict(string eventName, Callable handler)
	{
		ModApiEvent? e = ModApi.Find(eventName);
		if (e is null)
		{
			return;
		}

		lock (VerdictLock)
		{
			if (Verdicts.TryGetValue(e.Kind, out List<Callable>? list))
			{
				list.RemoveAll(c => c.Equals(handler));
			}
		}

		PushVerdictMask();
	}

	private static void PushVerdictMask()
	{
		var mask = new ModEventMask();
		lock (VerdictLock)
		{
			foreach ((int kind, List<Callable> list) in Verdicts)
			{
				if (list.Count != 0)
				{
					mask = mask.With(kind);
				}
			}
		}

		// The engine's own verdict: the save gate refuses a start whose save needs filtering.
		mask = mask.With(ModSaveGate.StartGameKind);
		if (!mask.IsZero && !_verdictInstalled)
		{
			HostBridge.SetModVerdictHandler(VerdictCallback);
			_verdictInstalled = true;
		}

		HostBridge.SetModVerdictMask(mask);
		if (mask.IsZero && _verdictInstalled)
		{
			HostBridge.SetModVerdictHandler(null);
			_verdictInstalled = false;
		}
	}

	private static void ClearVerdicts()
	{
		lock (VerdictLock)
		{
			Verdicts.Clear();
		}

		PushVerdictMask();
	}

	/// <summary>
	/// The payload's call, on the guest thread: every handler for the kind runs on one dictionary;
	/// what they changed in a writable field, and a cancel, go back as "name=value" pairs.
	/// </summary>
	private static int OnVerdict(int kind, IntPtr fields, UIntPtr length, IntPtr output, UIntPtr capacity)
	{
		try
		{
			Callable[] handlers;
			lock (VerdictLock)
			{
				handlers = Verdicts.TryGetValue(kind, out List<Callable>? list) ? list.ToArray() : Array.Empty<Callable>();
			}

			string text = Marshal.PtrToStringUTF8(fields, (int)length) ?? string.Empty;
			List<KeyValuePair<string, object>> plain = ParseFieldsPlain(text);
			var before = new Dictionary<string, object>();
			foreach ((string key, object value) in plain)
			{
				before[key] = value;
			}

			// The engine's own verdict first: the save gate (start_game), which no script sees.
			// Before it, the save being started is named for the snapshots, and a quickload's hold a
			// crash left behind is put back, so the game reads the player's own files.
			SnapshotsOnStartGame(kind, plain);
			bool cancel = ModSaveGate.Verdict(kind, plain);
			if (handlers.Length == 0)
			{
				if (!cancel)
				{
					return 0;
				}

				byte[] refusal = Encoding.UTF8.GetBytes("cancel=1");
				int m = Math.Min(refusal.Length, (int)capacity);
				Marshal.Copy(refusal, 0, output, m);
				return m;
			}

			Godot.Collections.Dictionary e = ParseFields(text);
			_inVerdict = true;
			try
			{
				foreach (Callable handler in handlers)
				{
					if (handler.Target is { } target && !GodotObject.IsInstanceValid(target))
					{
						continue;
					}

					Variant result = handler.Call(e);
					if (result.VariantType == Variant.Type.Bool && result.AsBool())
					{
						cancel = true;
					}
				}
			}
			finally
			{
				_inVerdict = false;
			}
			// Godot reads e through each handler; see Refii.Core.Engine_ArrayMeshes.
			GC.KeepAlive(e);

			if (e.TryGetValue("cancel", out Variant cancelled) && cancelled.VariantType == Variant.Type.Bool && cancelled.AsBool())
			{
				cancel = true;
			}

			var answer = new StringBuilder();
			if (cancel)
			{
				answer.Append("cancel=1");
			}

			if (kind >= 0 && kind < ModApi.Events.Count)
			{
				foreach (ModApiField f in ModApi.Events[kind].Fields.Where(f => f.Writable && f.Name != "cancel"))
				{
					if (!e.TryGetValue(f.Name, out Variant now))
					{
						continue;
					}

					double? value = now.VariantType switch
					{
						Variant.Type.Bool => now.AsBool() ? 1 : 0,
						Variant.Type.Int => now.AsInt64(),
						Variant.Type.Float => now.AsDouble(),
						_ => null,
					};
					if (value is null)
					{
						continue;
					}

					double old = before.TryGetValue(f.Name, out object? o) ? o switch
					{
						bool b => b ? 1 : 0,
						long l => l,
						double d => d,
						_ => double.NaN,
					} : double.NaN;
					if (value.Value != old)
					{
						if (answer.Length != 0)
						{
							answer.Append(',');
						}

						answer.Append(f.Name).Append('=').Append(value.Value.ToString("R", CultureInfo.InvariantCulture));
					}
				}
			}

			if (answer.Length == 0)
			{
				return 0;
			}

			byte[] bytes = Encoding.UTF8.GetBytes(answer.ToString());
			int n = Math.Min(bytes.Length, (int)capacity);
			Marshal.Copy(bytes, 0, output, n);
			return n;
		}
		catch (Exception ex)
		{
			GD.PushError($"[mods] godot: verdict handler failed: {ex.Message}");
			return 0;
		}
	}

	public override void _ExitTree()
	{
		ClearVerdicts();
		ModHostQueries.Clear();
		_clock.Clear();
		_entityStore.Clear();
		_askCallbacks.Clear();
		_luaCalls.Clear();
		Refii.Fx.Engine_FxPlayer.StopAll();
		Refii.Lighting.Engine_ModLights.Clear();
		if (_maskSent)
		{
			HostBridge.SetModEventMask(new ModEventMask());
			_maskSent = false;
		}
	}

	/// <summary>Once per frame: the mask from the live connections, then every event posted since the last frame.</summary>
	public override void _Process(double delta)
	{
		ModEventMask mask = ComputeMask();
		if (mask != _mask || !_maskSent)
		{
			_mask = mask;
			_maskSent = true;
			HostBridge.SetModEventMask(mask);
		}

		// A quickload whose level never stood (a load the guest refused): the folder goes back anyway.
		if (_staged is not null && Time.GetTicksMsec() - _stagedAt > StagedTimeoutMs)
		{
			FinishSnapshotLoad($"no level in {StagedTimeoutMs / 1000} s");
		}

		// One question a frame while a script is loaded: the co-op state the sync getters answer.
		bool scripts = ScriptsRoot is not null && IsInstanceValid(ScriptsRoot) && ScriptsRoot.GetChildCount() != 0;
		if (scripts && (_coopStateQuery == 0 || ++_coopStateWait > CoopStateRetryFrames))
		{
			_coopStateQuery = QueryCoopState();
			_coopStateWait = 0;
		}

		HostBridge.DrainModEvents(_drained);
		foreach ((int kind, string fields) in _drained)
		{
			// A save package the guest wrote: its sidecar is stamped with the mod set (ModSaveGate).
			if (kind == ModSaveGate.SaveWrittenKind)
			{
				ModSaveGate.OnSaveWritten(fields);
				Godot.Collections.Dictionary written = SaveWrittenFields(fields);
				if (written["player"].AsInt64() == 1 && written["save"].AsString().Length != 0)
				{
					lock (SnapshotLock)
					{
						_currentSave = written["save"].AsString();
					}
				}

				// The signal is the save_written event's, which the guest posts after this.
				continue;
			}

			// A quickload's level stands: the save folder gets its own files back.
			if (kind == LevelLoadedKind && _staged is not null)
			{
				FinishSnapshotLoad("the level is up");
			}

			// A save's Lua table loaded: a filtered load drops the missing mods' keys now.
			if (kind == ModSaveGate.SaveLoadedKind)
			{
				ModSaveGate.OnSaveLoaded();
			}

			// An effect a native or Lua mod asked Godot to play or stop: drawn here, never an event.
			if (kind == Refii.Fx.Engine_FxPlayer.RequestKind)
			{
				Refii.Fx.Engine_FxPlayer.Handle(fields);
				continue;
			}

			// The received-item card the guest's own entry points stated: Godot's UI, never an event.
			if (kind == Refii.Ui.Engine_UiReceiveItemsSurface.StateKind)
			{
				Refii.Ui.Engine_UiReceiveItemsSurface.Stated(fields);
				continue;
			}

			// A menu or a message box a native or Lua mod asked for: Godot's UI, never an event.
			if (kind == Refii.Ui.Engine_UiModRequest.Kind)
			{
				// A signal, a published text or the Lua lane rides the same kind: Godot's, never UI.
				List<KeyValuePair<string, object>> request = ParseFieldsPlain(fields);
				if (ModSignals.IsOurs(request))
				{
					HandleSignal(request, fields);
				}
				else
				{
					Refii.Ui.Engine_UiModBoxes.Handle(fields);
				}

				continue;
			}

			// A published number changed (the runtime's post): value_changed, never an event.
			if (kind == ValueKind)
			{
				ParseFieldsPlain(fields, _plain);
				if (ModSignals.TryValueChange(_plain, out string changedKey, out double changedValue))
				{
					EmitSignal(SignalName.value_changed, changedKey, changedValue);
					if (_luaWatchesValues && double.IsFinite(changedValue))
					{
						_luaValues.Add((changedKey, changedValue, false));
					}
				}

				continue;
			}

			// The title's own lines (Fable 2: an online joiner's weapon pick a mod made, into its
			// co-op kit, which carries it to the host); never an event.
			if (Refii.Core.Engine_TitleGame.Active.HandleBusLine(kind, fields))
			{
				continue;
			}

			// A mod menu's rows or a host screen a native or Lua mod asked for: Godot's UI, never an event.
			if (kind == Refii.Ui.Engine_UiModRows.Kind)
			{
				Refii.Ui.Engine_UiModRows.Handle(fields);
				continue;
			}

			// A query's answer: the id goes back as the signal's first argument, never to mod_event.
			if (kind == ReplyKind)
			{
				// The host's own questions (ModHostQueries: a host list's) are answered there, never to a script.
				ParseFieldsPlain(fields, _plain);
				if (ModHostQueries.Take(_plain))
				{
					continue;
				}

				long id = TryField(_plain, "id", out Variant idValue) ? idValue.AsInt64() : 0;
				if (id != 0 && id == _coopStateQuery)
				{
					_coopStateQuery = 0;
					_localHero = TryField(_plain, "local_hero", out Variant local) ? local.AsInt64() : 0;
					_coopMode = TryField(_plain, "coop_mode", out Variant mode) ? mode.AsInt32() : 0;
					_menuPlayer = TryField(_plain, "menu_player", out Variant owner) ? owner.AsInt32() : 1;
					_splitScreen = TryField(_plain, "split_screen", out Variant splitValue) && splitValue.AsBool();
					continue;
				}

				Godot.Collections.Dictionary result = ToDictionary(_plain);
				result.Remove(FieldKey("id"));

				// An ask's answer goes to its callable, never to reply.
				if (_askCallbacks.Remove(id, out Callable answered))
				{
					if (answered.Target is not { } askOwner || IsInstanceValid(askOwner))
					{
						answered.Call(result);
					}

					GC.KeepAlive(result);
					continue;
				}

				if (_cameraSeedQueries.Remove(id, out long cameraPlayer))
				{
					if (!_cameraMode.ContainsKey(cameraPlayer) && result.TryGetValue("mode", out Variant seededMode))
					{
						_cameraMode[cameraPlayer] = seededMode.AsInt32();
					}

					continue;
				}

				if (_cutsceneSeedQueries.Remove(id, out long cutscenePlayer))
				{
					_cutscene.Stated(cutscenePlayer, result.TryGetValue("kinds", out Variant kinds) ? kinds.AsInt32() : 0);
					continue;
				}

				if (_heroSeedQueries.Remove(id, out long seededPlayer))
				{
					long seeded = result.TryGetValue("hero", out Variant heroValue) ? heroValue.AsInt64() : 0;
					if (seeded != 0 && !_hero.ContainsKey(seededPlayer))
					{
						_hero[seededPlayer] = seeded;
					}

					continue;
				}

				EmitSignal(SignalName.reply, id, result);
				GC.KeepAlive(result);
				continue;
			}

			if (kind < 0 || kind >= _nameByKind.Length)
			{
				continue;
			}

			// THE MODS' LUA RELOADED (lua_ready): the claims, presses and HUD overrides the last chunk
			// held go with it, so a reloaded mod starts from nothing claimed, pressed or overridden.
			if (kind == LuaReadyKind)
			{
				ModInput.ClearClaims();
				ModInputPresses.Clear();
				Refii.Ui.Engine_UiHudOverrides.Clear();
				Refii.Entities.Engine_HiddenParts.Clear();
			}

			ParseFieldsPlain(fields, _plain);
			TrackHero(kind, _plain);
			TrackCutscene(kind, _plain);
			TrackCamera(kind, _plain);
			TrackPlay(kind, _plain);
			TrackRuntime(kind, _plain);
			EmitEvent(kind, _plain);
		}

		FlushLuaValues();
		Refii.Fx.Engine_FxPlayer.Pump(delta);
		Refii.Lighting.Engine_ModLights.Pump(delta);
	}

	/// <summary>
	/// One event to its signal and to mod_event, both handed one Dictionary. What the emits are
	/// handed is released here, not left to a finalizer: a handler gets its own reference, so a
	/// script keeping its Dictionary keeps that.
	/// </summary>
	private void EmitEvent(int kind, List<KeyValuePair<string, object>> plain)
	{
		Godot.Collections.Dictionary e = ToDictionary(plain);
		Variant argument = e;
		if (_signalByKind[kind] is { } signal)
		{
			_emitOne[0] = argument;
			EmitSignal(signal, _emitOne);
			_emitOne[0] = default;
		}

		if (_eventNames[kind].VariantType == Variant.Type.Nil)
		{
			_eventNames[kind] = _nameByKind[kind].ToString();
		}

		_emitTwo[0] = _eventNames[kind];
		_emitTwo[1] = argument;
		EmitSignal(SignalName.mod_event, _emitTwo);
		_emitTwo[1] = default;
		argument.Dispose();
		e.Dispose();
	}

	/// <summary>A field's value as the Dictionary would hold it: the last of its name, and false for none.</summary>
	private static bool TryField(List<KeyValuePair<string, object>> plain, string key, out Variant value)
	{
		for (int i = plain.Count - 1; i >= 0; i--)
		{
			if (plain[i].Key == key)
			{
				value = FieldValue(plain[i].Value);
				return true;
			}
		}

		value = default;
		return false;
	}

	/// <summary>
	/// The runtime's statements: a tick counts into the clock (the game clock only while no paused
	/// mode holds, as Lua's) and runs the timers it made due, before the tick's signal; entity_removed
	/// forgets that handle's values.
	/// </summary>
	private void TrackRuntime(int kind, List<KeyValuePair<string, object>> e)
	{
		if (kind == TickKind)
		{
			double seconds = TryField(e, "seconds", out Variant s) ? s.AsDouble() : 0;
			foreach (Callable run in _clock.Tick(seconds, _play.AnyMode))
			{
				if (run.Target is { } target && !IsInstanceValid(target))
				{
					continue;
				}

				run.Call();
			}
		}
		else if (kind == EntityRemovedKind && TryField(e, "entity", out Variant entity))
		{
			_entityStore.Forget(entity.AsInt64());
		}
	}

	private void TrackHero(int kind, List<KeyValuePair<string, object>> e)
	{
		if (kind != 1 && kind != 2)
		{
			return;
		}

		long player = TryField(e, "player", out Variant p) ? p.AsInt64() : 0;
		long hero = TryField(e, "hero", out Variant h) ? h.AsInt64() : 0;
		if (kind == 1)
		{
			_hero[player] = hero;
		}
		else if (_hero.TryGetValue(player, out long current) && current == hero)
		{
			_hero.Remove(player);
		}
	}

	/// <summary>The paused event and the level edges into <see cref="_play"/>.</summary>
	private void TrackPlay(int kind, List<KeyValuePair<string, object>> e)
	{
		switch (kind)
		{
			case PausedKind:
				_play.Paused(TryField(e, "mode", out Variant mode) ? mode.AsInt32() : 1,
					TryField(e, "paused", out Variant paused) && paused.AsBool());
				break;
			case LevelUnloadingKind:
				_play.Unloading();
				ModScreen.ReleaseFor(this);
				break;
			case LevelLoadedKind:
				_play.Loaded();
				break;
		}
	}

	/// <summary>The cutscene event into <see cref="_cutscene"/>: kinds is every statement holding after the edge.</summary>
	private void TrackCamera(int kind, List<KeyValuePair<string, object>> e)
	{
		if (kind == CameraChangedKind && TryField(e, "player", out Variant p) && TryField(e, "mode", out Variant m))
		{
			_cameraMode[p.AsInt64()] = m.AsInt32();
		}
	}

	private void TrackCutscene(int kind, List<KeyValuePair<string, object>> e)
	{
		if (kind == CutsceneKind)
		{
			_cutscene.Stated(TryField(e, "player", out Variant p) ? p.AsInt64() : -1,
				TryField(e, "kinds", out Variant k) ? k.AsInt32() : 0);
		}
	}

	/// <summary>
	/// Bit k set when some script is connected to kind k's signal; every bit when one is
	/// connected to mod_event; the hero kinds whenever a script is loaded at all; the engine's own
	/// kinds always.
	/// </summary>
	private ModEventMask ComputeMask()
	{
		// The engine's own want: a filtered load drops the missing mods' RefiiModSave keys on
		// save_loaded (ModSaveGate), scripts or none.
		// A quickload in flight waits for level_loaded to put the save folder back (load_snapshot).
		// camera_changed feeds camera_mode, which the core's head hider reads (Engine_FirstPersonHead).
		ModEventMask own = ModSaveGate.WantsSaveLoaded ? new ModEventMask().With(ModSaveGate.SaveLoadedKind) : new ModEventMask();
		own = own.With(CameraChangedKind);
		if (_staged is not null)
		{
			own = own.With(LevelLoadedKind);
		}

		if (ScriptsRoot is null || !IsInstanceValid(ScriptsRoot) || ScriptsRoot.GetChildCount() == 0)
		{
			return own;
		}

		ModEventMask mask = ModSaveGate.WantsSaveLoaded ? HeroKinds.With(ModSaveGate.SaveLoadedKind) : HeroKinds;
		if (_staged is not null)
		{
			mask = mask.With(LevelLoadedKind);
		}

		// entity_value's store forgets a handle at its entity_removed, handler or none.
		if (_entityStore.Count != 0)
		{
			mask = mask.With(EntityRemovedKind);
		}

		// HasConnections, not the connection list: this runs per signal per frame, and the list is an
		// Array of Dictionaries each time.
		if (HasConnections(SignalName.mod_event))
		{
			for (int kind = 0; kind < _nameByKind.Length; kind++)
			{
				mask = mask.With(kind);
			}

			return mask;
		}

		for (int kind = 0; kind < _signalByKind.Length; kind++)
		{
			if (_signalByKind[kind] is { } signal && HasConnections(signal))
			{
				mask = mask.With(kind);
			}
		}

		return mask;
	}

	// ---- the surface a script calls -------------------------------------------------------

	/// <summary><c>REFII_MOD_API_VERSION</c>.</summary>
	public int api_version() => ModApi.Version;

	/// <summary>A hard mod's published value by key (<c>combat.stamina</c>); NaN when its hook has published none.</summary>
	public float get_value(string key) =>
		HostBridge.TryGetModValue(key, out float value) ? value : float.NaN;

	/// <summary>The last text published under <paramref name="key"/> (<c>Refii.publish_text</c>, <c>refii_mod_publish_text</c>, <see cref="publish_text"/>); "" when none has been.</summary>
	public string get_text(string key) => ModSignals.Text(key ?? string.Empty) ?? string.Empty;

	/// <summary>Whether a text has been published under <paramref name="key"/>.</summary>
	public bool has_text(string key) => ModSignals.Text(key ?? string.Empty) is not null;

	/// <summary>
	/// Publishes a text under <paramref name="key"/> ("&lt;mod&gt;.&lt;name&gt;", at most 96) for every Godot
	/// reader: get_text answers it at once and value_changed fires when it changed. At most 1024
	/// bytes; false for a bad key, a longer text, or a new key past the store's 4096.
	/// </summary>
	public bool publish_text(string key, string text)
	{
		if (!ModSignals.SetText(key ?? string.Empty, text ?? string.Empty, out bool changed))
		{
			return false;
		}

		if (changed)
		{
			EmitSignal(SignalName.value_changed, key!, text ?? string.Empty);
			_luaValues.Add((key!, text ?? string.Empty, true));
		}

		return true;
	}

	/// <summary>
	/// A player's store value NOW (1 or 2), synchronously — the store <c>Refii.player_value</c> reads
	/// and a <c>player:</c> UI source resolves (Engine_UiValueSources); NaN when unset.
	/// </summary>
	public float player_value(long player, string key) =>
		player is 1 or 2 && Refii.Ui.Engine_UiValueSources.Number(
			Refii.Ui.Engine_UiValueSources.PlayerPrefix + (key ?? string.Empty),
			Refii.Ui.Engine_UiValueSource.Live((int)player)) is { } value
			? value
			: float.NaN;

	/// <summary>The same, with <paramref name="fallback"/> when unset.</summary>
	public float player_value(long player, string key, float fallback) =>
		player_value(player, key) is var value && !float.IsNaN(value) ? value : fallback;

	/// <summary>
	/// The keyboard's attack mode for <paramref name="player"/> (1 or 2), read at the call: 1 melee, 2
	/// ranged, 3 magic (<see cref="ModApi.AttackModeMeaning"/>); 0 for a player no keyboard plays here.
	/// </summary>
	public int attack_mode(long player) => Refii.Game.GameInput.AttackModeOf((int)player);

	/// <summary>
	/// The camera the game has active for <paramref name="player"/> (1 or 2), as ECameraMode
	/// (<c>constants()</c>' CAMERA_*: 2 the third-person camera, 8 first person, ...), as the bus last
	/// stated it (<see cref="ModApi.CameraModeMeaning"/>), heard with or without a script loaded;
	/// -1 before any is stated, and for a player whose camera runs on the other machine. One guest
	/// tick behind, like every signal.
	/// </summary>
	public int camera_mode(long player) => _cameraMode.TryGetValue(player, out int mode) ? mode : -1;

	/// <summary>
	/// Holds a camera for <paramref name="player"/> (1 or 2) through the game's own per-player hold
	/// (<see cref="ModApi.CameraOverrideMeaning"/>): <paramref name="mode"/> 8 first person or 2 the
	/// third-person camera, 0 lets it go; <paramref name="options"/> <c>fov</c>, <c>pitch_limit</c>,
	/// <c>blend</c> (seconds), <c>aim</c> (true: the gun aim's own camera takes over while aiming).
	/// Stated on the guest's next tick.
	/// </summary>
	public void camera_override(int player, int mode, Godot.Collections.Dictionary options)
	{
		float Option(string key, float fallback) =>
			options.TryGetValue(key, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : fallback;
		bool aim = options.TryGetValue("aim", out Variant a) && a.AsBool();
		Post($"camera_override {player} {mode} {Num(Option("fov", 0f))} {Num(Option("pitch_limit", 0f))} {Num(Option("blend", -1f))} {(aim ? 1 : 0)}");
	}

	/// <summary>The short form, the camera's own field of view, pitch and blend.</summary>
	public void camera_override(int player, int mode) => camera_override(player, mode, new Godot.Collections.Dictionary());

	/// <summary>
	/// The field of view of <paramref name="player"/>'s live camera in degrees, read at the call — the
	/// horizontal 4:3 angle the game's cameras state (70 the default), from the block this machine
	/// draws that player's view from; 0 for a player with no camera here (<see cref="ModApi.CameraFovMeaning"/>).
	/// </summary>
	public float fov(int player) =>
		HostBridge.GetPlayerCamera(player) is { } camera && float.IsFinite(camera.HorizontalFov) && camera.HorizontalFov > 0f
			? Mathf.RadToDeg(camera.HorizontalFov)
			: 0f;

	/// <summary>
	/// Hides a part of an entity in the drawing (<paramref name="on"/>) or shows it again, by the
	/// HANDLE an event gave the script (<see cref="ModApi.HidePartMeaning"/>): <paramref name="part"/>
	/// a bone of its rig by the name the bone table states, in <paramref name="player"/>'s view alone
	/// (1 or 2) or in every view (0). False for no handle, no name or any other player.
	/// </summary>
	public bool hide_part(long handle, string part, bool on, int player) =>
		handle > 0 && handle <= uint.MaxValue && Refii.Entities.Engine_HiddenParts.Set((uint)handle, part, on, player);

	/// <summary><see cref="hide_part(long, string, bool, int)"/> in every view.</summary>
	public bool hide_part(long handle, string part, bool on) => hide_part(handle, part, on, 0);

	/// <summary>
	/// A player's store TEXT NOW (1 or 2), synchronously — what <c>Refii.player_value_text</c> reads;
	/// "" when the key holds no text.
	/// </summary>
	public string player_text(long player, string key) =>
		player is 1 or 2 ? HostBridge.GetPlayerText((int)player, key ?? string.Empty) ?? string.Empty : string.Empty;

	/// <summary>
	/// A player's store HASH NOW, synchronously — what <c>Refii.player_value_hash</c> reads: the text
	/// read as a number as C's strtoul with base 0 reads it ("0x…" hex, a leading 0 octal, else
	/// decimal), whole and within 32 bits, as "0x%08x"; "" for none.
	/// </summary>
	public string player_value_hash(long player, string key) =>
		PlayerHash(player_text(player, key)) is { } hash ? ModRowSubject.Hex(hash) : string.Empty;

	/// <summary>A store text as a hash, by refii_mod_player_hash's rule (strtoull base 0 — leading space and a sign allowed, a negative wraps past 32 bits — the whole text, at most 32 bits); null when it is not one.</summary>
	internal static uint? PlayerHash(string? text)
	{
		text = (text ?? string.Empty).TrimStart(' ', '\t', '\n', '\v', '\f', '\r');
		bool negative = text.StartsWith('-');
		if (negative || text.StartsWith('+'))
		{
			text = text[1..];
		}

		if (text.Length == 0)
		{
			return null;
		}

		bool hex = text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X');
		bool octal = !hex && text.Length > 1 && text[0] == '0';
		string digits = hex ? text[2..] : octal ? text[1..] : text;
		ulong value = 0;
		foreach (char c in digits)
		{
			int digit = c is >= '0' and <= '9' ? c - '0' : hex && c is >= 'a' and <= 'f' ? c - 'a' + 10 : hex && c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
			if (digit < 0 || digit >= (hex ? 16 : octal ? 8 : 10))
			{
				return null;
			}

			value = value * (ulong)(hex ? 16 : octal ? 8 : 10) + (ulong)digit;
			if (value > uint.MaxValue)
			{
				return null;
			}
		}

		return negative && value != 0 ? null : (uint)value;
	}

	/// <summary>A mod's signal or published text off the bus: raised as mod_signal / stored for get_text.</summary>
	private void HandleSignal(List<KeyValuePair<string, object>> request, string fields)
	{
		if (ModSignals.Parse(request) is not { } parsed)
		{
			GD.PushWarning($"[mods] godot: a signal record the host does not read: {fields}");
			return;
		}

		if (parsed.Op == ModSignals.Op.PublishText)
		{
			if (!ModSignals.SetText(parsed.Key, parsed.Text, out bool changed))
			{
				GD.PushWarning($"[mods] godot: publish_text '{parsed.Key}' not kept (over 1024 bytes, or past {ModSignals.MaxTexts} keys)");
			}
			else if (changed)
			{
				EmitSignal(SignalName.value_changed, parsed.Key, parsed.Text);
				_luaValues.Add((parsed.Key, parsed.Text, true));
			}

			return;
		}

		(string mod, string name) = ModSignals.Split(parsed.Key);
		if (mod == ModLuaLane.Scope)
		{
			HandleLane(name, parsed.Text);
			return;
		}

		EmitSignal(SignalName.mod_signal, mod, name, parsed.Text);
		var e = new Godot.Collections.Dictionary { ["mod"] = mod, ["name"] = name, ["payload"] = parsed.Text };
		EmitSignal(SignalName.mod_event, "mod_signal", e);
		GC.KeepAlive(e);
	}

	// ---- the Lua lane (ModLuaLane): Lua's asks of the host, Godot's calls into the game VM ----------

	/// <summary>A record of the core's own signal scope: an ask, an answer, a watch, a new VM's hello.</summary>
	private void HandleLane(string op, string payload)
	{
		switch (op)
		{
			case ModLuaLane.Ask when ModLuaLane.ParseAsk(payload) is { } ask:
			{
				object? answer = HostCall(ask.What, ask.Args, ask.Mod);
				if (ask.Id > 0 && !PostLua(ModLuaLane.ReplyChunk(ask.Id, answer)))
				{
					PostLua(ModLuaLane.ReplyChunk(ask.Id, null));
				}

				break;
			}
			case ModLuaLane.Answer when ModLuaLane.ParseAnswer(payload) is { } reply:
				if (_luaCalls.Remove(reply.Id, out Action<ModLuaLane.Reply>? done))
				{
					done(reply);
				}

				break;
			case ModLuaLane.Watch:
				_luaWatchesValues = true;
				break;
			case ModLuaLane.Hello:
				ModScreen.ReleaseLua(this);
				RestateToLua();
				break;
			default:
				GD.PushWarning($"[mods] godot: a Lua lane record the host does not read: {op}");
				break;
		}
	}

	/// <summary>
	/// What <c>Refii.ask_host(what, fn, ...)</c> may name (<see cref="ModApi.HostCalls"/>): the host-side
	/// reads and actions a Lua mod has no synchronous form of, answered as plain values.
	/// </summary>
	private object? HostCall(string what, object?[] args, string mod)
	{
		object? At(int i) => i < args.Length ? args[i] : null;
		long Player(int i) => At(i) is double d ? (long)d : 0;
		string Text(int i) => At(i) switch
		{
			string s => s,
			double d => d.ToString("R", CultureInfo.InvariantCulture),
			_ => string.Empty,
		};
		float Number(int i) => At(i) switch
		{
			double d => (float)d,
			bool b => b ? 1f : 0f,
			_ => 0f,
		};
		switch (what)
		{
			case "fade_state": return Plain(fade_state(Player(0)));
			case "hud_overrides": return Plain(hud_overrides(Player(0)));
			case "input_claimed": return input_claimed(Player(0), Text(1));
			case "input_claims": return Plain(Variant.From(input_claims(Player(0))));
			case "input_label": return input_label(Text(0));
			case "inventory_action": return inventory_action(Player(0), Text(1), Text(2));
			case "inventory_items": return Plain(Variant.From(inventory_items(Player(0))));
			case "lights": return Plain(Variant.From(lights()));
			case "markers": return Plain(Variant.From(markers()));
			case "meters": return Plain(Variant.From(meters()));
			case "minimap": return Plain(minimap(Player(0)));
			case "minimap_point": return Plain(minimap_point(Number(0), Number(1), Number(2)));
			case "mods": return EnabledMods.ToList();
			case "own_screen":
				OwnScreen(Player(0), Number(1) != 0f, mod, fromLua: true);
				return true;
			case "presses": return Plain(Variant.From(presses(Player(0))));
			case "snapshot_time": return snapshot_time(Text(0));
			case "touch_mode": return touch_mode();
			case "touch_stick": return Plain(touch_stick());
			case "move_stick": return Plain(move_stick(Player(0)));
			case "set_setting":
				set_setting(Text(0), Number(1));
				return true;
			case "save_snapshot": return save_snapshot(Text(0));
			case "load_snapshot": return load_snapshot(Text(0));
			default:
				GD.PushWarning($"[mods] godot: Refii.ask_host('{what}'): not a host call (ModApi.HostCalls)");
				return null;
		}
	}

	/// <summary>A Variant as the plain value a Lua literal is written from.</summary>
	private static object? Plain(Variant v)
	{
		switch (v.VariantType)
		{
			case Variant.Type.Nil: return null;
			case Variant.Type.Bool: return v.AsBool();
			case Variant.Type.Int: return v.AsInt64();
			case Variant.Type.Float: return v.AsDouble();
			case Variant.Type.Vector2:
			{
				Vector2 p = v.AsVector2();
				return new Dictionary<string, object?> { ["x"] = (double)p.X, ["y"] = (double)p.Y };
			}
			case Variant.Type.Vector3:
			{
				Vector3 p = v.AsVector3();
				return new Dictionary<string, object?> { ["x"] = (double)p.X, ["y"] = (double)p.Y, ["z"] = (double)p.Z };
			}
			case Variant.Type.Color:
			{
				Color c = v.AsColor();
				return new Dictionary<string, object?> { ["r"] = (double)c.R, ["g"] = (double)c.G, ["b"] = (double)c.B, ["a"] = (double)c.A };
			}
			case Variant.Type.Array:
				return v.AsGodotArray().Select(Plain).ToList();
			case Variant.Type.PackedStringArray:
				return v.AsStringArray().Select(text => (object?)text).ToList();
			case Variant.Type.Object:
				return null;
			case Variant.Type.Dictionary:
			{
				var map = new SortedDictionary<string, object?>(StringComparer.Ordinal);
				foreach ((Variant key, Variant value) in v.AsGodotDictionary())
				{
					map[key.AsString()] = Plain(value);
				}

				return map;
			}
			default: return v.AsString();
		}
	}

	/// <summary>A plain lane value as the Variant a script gets: a whole number as an int (handles, serials), else as it came.</summary>
	private static Variant FromLane(object? value) => value switch
	{
		null => default,
		bool b => b,
		double d when d == Math.Floor(d) && Math.Abs(d) < 9.0e15 => (long)d,
		double d => d,
		string s => s,
		_ => default,
	};

	/// <summary>A chunk for the game VM on the guest's next tick; false when it does not fit the line.</summary>
	private static bool PostLua(string chunk) => Post("run_lua " + chunk);

	/// <summary>Runs <paramref name="expression"/> in the game VM once; <paramref name="answered"/> gets its values on a later frame.</summary>
	private bool CallLua(string expression, Action<ModLuaLane.Reply> answered)
	{
		long id = ++_nextLuaCall;
		_luaCalls[id] = answered;
		if (PostLua(ModLuaLane.CallChunk(id, expression)))
		{
			return true;
		}

		_luaCalls.Remove(id);
		return false;
	}

	/// <summary>A callable run with the first value of a call_lua answer (null when it failed), unless its owner is gone.</summary>
	private bool CallLuaFirst(string expression, Callable callback) =>
		CallLua(expression, reply =>
		{
			if (callback.Target is { } owner && !IsInstanceValid(owner))
			{
				return;
			}

			callback.Call(reply.Ok && reply.Values.Length != 0 ? FromLane(reply.Values[0]) : default);
		});

	/// <summary>The published values this frame changed, stated to Lua in chunks the command line holds.</summary>
	private void FlushLuaValues()
	{
		if (_luaValues.Count == 0)
		{
			return;
		}

		PostLuaValues(_luaValues, changed: true);
		_luaValues.Clear();
	}

	/// <summary>A new game VM (its runtime's hello): every published text and every Godot pool, stated again.</summary>
	private void RestateToLua()
	{
		var texts = new List<(string Key, object Value, bool Text)>();
		foreach (string key in ModSignals.TextKeys())
		{
			texts.Add((key, ModSignals.Text(key) ?? string.Empty, true));
		}

		PostLuaValues(texts, changed: false);
		foreach ((string chunk, _) in _pools.Values)
		{
			PostLua(chunk);
		}
	}

	private static void PostLuaValues(List<(string Key, object Value, bool Text)> values, bool changed)
	{
		const int Budget = CommandLimit - 512;
		var batch = new List<(string Key, object Value, bool Text)>();
		int size = 0;
		foreach ((string Key, object Value, bool Text) item in values)
		{
			int itemSize = ModLuaLane.ValuesChunk(new[] { item }, changed).Length;
			if (batch.Count != 0 && size + itemSize > Budget)
			{
				PostLua(ModLuaLane.ValuesChunk(batch, changed));
				batch.Clear();
				size = 0;
			}

			batch.Add(item);
			size += itemSize;
		}

		if (batch.Count != 0)
		{
			PostLua(ModLuaLane.ValuesChunk(batch, changed));
		}
	}

	/// <summary>
	/// Raises Lua's <c>mod_signal {mod, name, payload}</c> in the game VM on the guest's next tick — the
	/// twin of <c>Refii.signal</c> the other way (<see cref="ModApi.SignalMeaning"/>): name scoped
	/// "&lt;mod&gt;.&lt;name&gt;", payload at most 1024 bytes. False for a bad name, a longer payload, or a line
	/// that does not fit.
	/// </summary>
	public bool signal_lua(string name, string payload)
	{
		if (!ModSignals.ValidName(name) || ModSignals.Split(name).Mod == ModLuaLane.Scope ||
			Encoding.UTF8.GetByteCount(payload ?? string.Empty) > ModSignals.MaxTextBytes)
		{
			GD.PushWarning($"[mods] godot: signal_lua('{name}'): not a scoped name or over {ModSignals.MaxTextBytes} bytes");
			return false;
		}

		return PostLua(ModLuaLane.SignalChunk(name, payload ?? string.Empty));
	}

	/// <summary>The short form, an empty payload.</summary>
	public bool signal_lua(string name) => signal_lua(name, string.Empty);

	/// <summary>
	/// Runs a Lua EXPRESSION in the game VM on the guest's next tick, once, and calls
	/// <paramref name="callback"/> with <c>{ ok, values }</c> on a later frame: ok false when it raised (the
	/// error is in the Mods error list), values its first five results (a number, a text, a boolean or
	/// null; a table crosses as null). The answering form of <see cref="run_lua"/>. False when the line
	/// does not fit.
	/// </summary>
	public bool call_lua(string expression, Variant callback)
	{
		Callable answer = ModScriptCallable.Of(callback);
		if (string.IsNullOrWhiteSpace(expression))
		{
			return false;
		}

		return CallLua(expression, reply =>
		{
			if (answer.Target is { } owner && !IsInstanceValid(owner))
			{
				return;
			}

			var values = new Godot.Collections.Array();
			foreach (object? value in reply.Values)
			{
				values.Add(FromLane(value));
			}

			var result = new Godot.Collections.Dictionary { ["ok"] = reply.Ok, ["values"] = values };
			answer.Call(result);
			GC.KeepAlive(result);
		});
	}

	/// <summary>
	/// <see cref="query"/> with its answer handed to <paramref name="callback"/>(result) instead of
	/// <c>reply</c>: the callable form of every guest read a Lua mod makes synchronously. The id, or 0
	/// when the line could not be posted.
	/// </summary>
	public long ask(string what, Godot.Collections.Array args, Variant callback)
	{
		long id = query(what, args);
		if (id != 0)
		{
			_askCallbacks[id] = ModScriptCallable.Of(callback);
		}

		return id;
	}

	/// <summary>
	/// <see cref="ask"/> answered AT ONCE, from inside an <see cref="on_verdict"/> handler
	/// (<see cref="ModApi.VerdictReadMeaning"/>): the verdict runs on the game's thread inside the seam,
	/// so the guest answers the query there, through the same branch, and the Dictionary ask's callback
	/// would get comes back now — what a Lua handler's synchronous <c>Refii.*</c> read gives it. Only
	/// the verbs of <see cref="ModApi.VerdictReads"/>; anywhere else, or for another verb,
	/// <c>{ ok = false, error }</c> and nothing is read.
	/// </summary>
	public Godot.Collections.Dictionary read(string what, Godot.Collections.Array args)
	{
		string verb = (what ?? string.Empty).Replace(' ', '_');
		List<KeyValuePair<string, object>> fields;
		if (!_inVerdict)
		{
			fields = ModVerdictRead.Fields(ModVerdictRead.Outside, string.Empty, verb);
		}
		else if (ModVerdictRead.Line(verb, args.Select(Arg)) is not { } line)
		{
			fields = ModVerdictRead.Fields(ModVerdictRead.Refused, string.Empty, verb);
		}
		else
		{
			(int result, string answer) = HostBridge.ModVerdictRead(line);
			fields = ModVerdictRead.Fields(result, answer, verb);
		}

		if (fields.FirstOrDefault(f => f.Key == "error").Value is string error)
		{
			GD.PushWarning("[mods] godot: " + error);
		}

		return ToDictionary(fields);
	}

	// ---- the runtime: the Lua runtime's clock, timers, entity values and pools, for Godot ----------

	/// <summary>The game clock (<see cref="ModApi.TimerMeaning"/>): seconds of guest ticks while no paused mode holds, as Lua's <c>Refii.now</c> counts them, from this host's start.</summary>
	public double now() => _clock.Now;

	/// <summary>Seconds of every guest tick, menus included (<c>Refii.real_now</c>).</summary>
	public double real_now() => _clock.RealNow;

	/// <summary>Runs <paramref name="fn"/>() once on the first tick the game clock passes now + <paramref name="seconds"/>; the timer's number.</summary>
	public long after(double seconds, Variant fn) => (long)_clock.After(seconds, ModScriptCallable.Of(fn));

	/// <summary>The keyed form: replaces the timer standing under <paramref name="key"/>, and answers the key (an empty key is keyless: the number).</summary>
	public Variant after(double seconds, Variant fn, string key) =>
		string.IsNullOrEmpty(key) ? (Variant)(long)_clock.After(seconds, ModScriptCallable.Of(fn)) : (Variant)(string)_clock.After(seconds, ModScriptCallable.Of(fn), key);

	/// <summary>Drops the timer under a key, or the keyless one with that number; true when one went.</summary>
	public bool cancel(Variant key) => key.VariantType switch
	{
		Variant.Type.String or Variant.Type.StringName => _clock.Cancel(key.AsString()),
		Variant.Type.Int or Variant.Type.Float => _clock.Cancel(key.AsInt64()),
		_ => false,
	};

	/// <summary>A value per entity handle (<see cref="ModApi.EntityStoreMeaning"/>), null for none.</summary>
	public Variant entity_value(long handle, string key) => _entityStore.Value(handle, key ?? string.Empty, default);

	/// <summary>The same, with <paramref name="fallback"/> for none.</summary>
	public Variant entity_value(long handle, string key, Variant fallback) => _entityStore.Value(handle, key ?? string.Empty, fallback);

	/// <summary>Sets a value per entity handle; null clears it. Forgotten at that entity's entity_removed.</summary>
	public void entity_set(long handle, string key, Variant value) =>
		_entityStore.Set(handle, key ?? string.Empty, value, value.VariantType == Variant.Type.Nil);

	/// <summary>
	/// Declares (or restates) a REGENERATING POOL in player <paramref name="player"/>'s store
	/// (<see cref="ModApi.PoolMeaning"/>) — the Lua runtime's own pool, ticked on the guest tick where
	/// the player's hero is played, so Lua and Godot share it: <paramref name="options"/> <c>max</c> (100),
	/// <c>floor</c> (0), <c>regen</c> per second (0), <c>delay</c> seconds after a spend (0), <c>max_key</c>
	/// (key + ".max"), numbers only. Stated again to every new game VM. False when the line does not fit.
	/// </summary>
	public bool pool(int player, string key, Godot.Collections.Dictionary options)
	{
		double Option(string name, double fallback) =>
			options.TryGetValue(name, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int ? v.AsDouble() : fallback;
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}

		string maxKey = options.TryGetValue("max_key", out Variant mk) && mk.VariantType == Variant.Type.String ? mk.AsString() : string.Empty;
		string chunk = ModLuaLane.PoolChunk(player, key, Option("max", 100), Option("floor", 0), Option("regen", 0), Option("delay", 0), maxKey);
		if (!PostLua(chunk))
		{
			return false;
		}

		_pools[(player, key)] = (chunk, maxKey.Length != 0 ? maxKey : key + ".max");
		return true;
	}

	/// <summary>The short form, every option its default.</summary>
	public bool pool(int player, string key) => pool(player, key, new Godot.Collections.Dictionary());

	/// <summary>
	/// A pool's <c>[value, max]</c> NOW, synchronously, from the player store the pool lives in (what
	/// <c>Refii.pool_value</c> answers): a pool not stated yet is full; empty when the store holds no
	/// maximum for it (no such pool, or not ticked yet).
	/// </summary>
	public Godot.Collections.Array pool_value(int player, string key)
	{
		string maxKey = _pools.TryGetValue((player, key ?? string.Empty), out (string Chunk, string MaxKey) declared) ? declared.MaxKey : key + ".max";
		float max = player_value(player, maxKey);
		return float.IsNaN(max)
			? new Godot.Collections.Array()
			: new Godot.Collections.Array { player_value(player, key ?? string.Empty, max), max };
	}

	/// <summary>Spends <paramref name="n"/> from a pool through the Lua runtime's own <c>Refii.pool_spend</c>, on the guest's next tick.</summary>
	public bool pool_spend(int player, string key, double n) =>
		PostLua($"if Refii and Refii.pool_spend then Refii.pool_spend({player}, {ModLuaLane.Literal(key ?? string.Empty)}, {ModLuaLane.Literal(n)}) end");

	/// <summary>The same, <paramref name="callback"/>(taken: bool) on a later frame — false when refused (null when it failed).</summary>
	public bool pool_spend(int player, string key, double n, Variant callback) =>
		CallLuaFirst($"Refii.pool_spend({player}, {ModLuaLane.Literal(key ?? string.Empty)}, {ModLuaLane.Literal(n)})", ModScriptCallable.Of(callback));

	// ---- reads a Lua mod makes synchronously, for Godot -------------------------------------------

	/// <summary>The game's FNV-1 of a name as a "0x…" string (<c>Refii.hash</c>): a GDB record's hash, a text tag's key.</summary>
	public string hash(string name) => ModRowSubject.Hex(Fnv1(name ?? string.Empty));

	/// <summary>refii_mod_fnv1 over the name's UTF-8 bytes, as the game hashes a name.</summary>
	internal static uint Fnv1(string name)
	{
		uint hash = 0x811C9DC5u;
		foreach (byte b in Encoding.UTF8.GetBytes(name))
		{
			hash = unchecked(hash * 0x01000193u) ^ b;
		}

		return hash;
	}

	/// <summary>Refii.split_screen as the guest last answered (coop_state): local co-op drawn as two halves.</summary>
	public bool split_screen() => _splitScreen;

	/// <summary>Forgives player <paramref name="player"/>'s crimes in <paramref name="villager"/>'s eyes (Refii.forgive_crimes), on the guest tick.</summary>
	public void forgive_crimes(long villager, int player) => Post($"forgive_crimes {villager} {player}");

	/// <summary>Asks the co-op partner <paramref name="question"/> for <paramref name="asker"/> (Refii.ask_partner); <paramref name="callback"/>(ticket) — null when not asked.</summary>
	public bool ask_partner(int asker, string question, Variant callback) =>
		CallLuaFirst($"Refii.ask_partner({asker}, {ModLuaLane.Literal(question ?? string.Empty)})", ModScriptCallable.Of(callback));

	/// <summary>A partner question's answer (Refii.partner_answer): <paramref name="callback"/>(null while pending, then true or false).</summary>
	public bool partner_answer(long ticket, Variant callback) =>
		CallLuaFirst($"Refii.partner_answer({ticket})", ModScriptCallable.Of(callback));

	/// <summary>Whether player <paramref name="player"/> has taken the per-player collectable (Refii.key_taken): <paramref name="callback"/>(bool).</summary>
	public bool key_taken(long entity, int player, Variant callback) =>
		CallLuaFirst($"Refii.key_taken({entity}, {player})", ModScriptCallable.Of(callback));

	/// <summary>A spell handle's cast serial (Refii.spell_serial): <paramref name="callback"/>(serial, or null).</summary>
	public bool spell_serial(long spell, Variant callback) =>
		CallLuaFirst($"Refii.spell_serial({spell})", ModScriptCallable.Of(callback));

	/// <summary>A <c>RefiiModSave</c> value (what <see cref="save_set"/> writes, what Lua reads as <c>RefiiModSave.&lt;key&gt;</c>): <paramref name="callback"/>(value, or null).</summary>
	public bool save_get(string key, Variant callback) =>
		CallLuaFirst($"RefiiModSave and RefiiModSave[{ModLuaLane.Literal(key ?? string.Empty)}]", ModScriptCallable.Of(callback));

	/// <summary>A settings.json value by key, the live override first; <paramref name="default"/> when no mod states it.</summary>
	public float setting(string key, float @default) =>
		ModSettings.TryGet(key, out float value) ? value : @default;

	/// <summary>Sets a mod setting in the running game, kept over the file's value for the session.</summary>
	public void set_setting(string key, float value) => ModSettings.Set(key, value);

	/// <summary>
	/// Runs a Lua chunk in the GAME VM on the guest's next tick (the shipped scripts' globals and
	/// <c>Refii.*</c> are in scope). The chunk is the rest of the command line, newlines kept;
	/// false when the line would not fit the tick's buffer, and nothing runs then.
	/// </summary>
	public bool run_lua(string chunk)
	{
		if (string.IsNullOrEmpty(chunk))
		{
			return false;
		}

		return Post("run_lua " + chunk.Replace("\r", string.Empty));
	}

	/// <summary>
	/// Runs a Lua chunk in the mods' FRONT-END Lua on the guest's next tick (frontend_lua): the title
	/// and the front end, where there is no game VM — the host's state for the current front-end presentation,
	/// with every mod's mod/frontend.lua run in it first (Refii.Mods.ModLua.FrontendPreamble says
	/// what Refii.* holds there). False when the line would not fit, and nothing runs then.
	/// </summary>
	public bool frontend_lua(string chunk)
	{
		if (string.IsNullOrEmpty(chunk))
		{
			return false;
		}

		return Post(ModLua.FrontendCommand + chunk.Replace("\r", string.Empty));
	}

	// ---- helper calls: one line each, run in the guest tick with the real context -------------

	/// <summary>Adds (or takes, negative) gold from an entity's purse.</summary>
	public void give_gold(long entity, long delta) => Post($"give_gold {entity} {delta}");

	/// <summary>Changes an entity's health by delta, attributed to attacker (0 for none); negative hurts.</summary>
	public void modify_health(long entity, float delta, long attacker) =>
		Post($"modify_health {entity} {Num(delta)} {attacker}");

	/// <summary>Sets an entity's current health outright.</summary>
	public void set_health(long entity, float value) => Post($"set_health {entity} {Num(value)}");

	/// <summary>Changes a hero's renown by delta through the game's own Stats.ModifyRenown (the renown event fires; the co-op partner gets its share).</summary>
	public void modify_renown(long entity, long delta) => Post($"modify_renown {entity} {delta}");

	/// <summary>Changes a hero's morality by delta (positive is good) through the game's own Stats.ModifyMorality; the game clamps it.</summary>
	public void modify_morality(long entity, float delta) => Post($"modify_morality {entity} {Num(delta)}");

	/// <summary>Changes a hero's purity by delta (positive is pure) through the game's own Stats.ModifyPurity; the game clamps it.</summary>
	public void modify_purity(long entity, float delta) => Post($"modify_purity {entity} {Num(delta)}");

	/// <summary>
	/// Saves the game as game Lua's AutoSaveGame(true, true) does, where player 1 or 2 may save
	/// (refii_mod_save_game): the game's whole save, each hero into its own package, with a
	/// save_written per package. <c>query can_save P</c> answers <c>able</c> (Player.IsAbleToSaveGame).
	/// </summary>
	public void save_game(int player) => Post($"save_game {player}");

	/// <summary>Adds one of an item definition (its GDB record hash: the "0x…" string the events carry, or a number) to an entity's inventory.</summary>
	public void add_item(long entity, Variant definition) => Post($"add_item {entity} {Arg(definition)}");

	/// <summary>Adds <paramref name="count"/> (1..100) of an item definition to an entity's inventory, each through the game's own add (refii_mod_add_items).</summary>
	public void add_item(long entity, Variant definition, long count) =>
		Post($"add_item {entity} {Arg(definition)} {Math.Clamp(count, 1, 100)}");

	/// <summary>
	/// Uses one of an item definition (the "0x…" string the events carry, or a number) from this
	/// machine's hero's inventory, as the Items page's Use does — the guest's own use, with no menu
	/// open: a potion drinks, food is eaten, an `item_used` event fires. Nothing happens when the
	/// hero carries none or the item states no use. False when the definition does not parse.
	/// </summary>
	public bool use_item(Variant definition) =>
		UseItemAction(Arg(definition)) is { } action && HostBridge.PostFrontEndAction(action) != 0;

	/// <summary>
	/// The tick pump's action <see cref="use_item"/> posts for a definition ("0x…" or decimal):
	/// <c>Inventory:use:&lt;record, decimal&gt;</c>, the Items page's own; null when it does not parse.
	/// </summary>
	public static string? UseItemAction(string definition)
	{
		string text = definition.Trim();
		bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
		return uint.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
				CultureInfo.InvariantCulture, out uint record) && record != 0u
			? Refii.Core.Engine_TitleGame.Active.UseItemAction(record)
			: null;
	}

	// ---- save snapshots: what a quicksave and a quickload are made of (SaveSnapshots) ------

	/// <summary>The level_loaded kind: a quickload's save folder is put back when it arrives.</summary>
	private const int LevelLoadedKind = 24;

	/// <summary>How long a quickload may take to stand its level before the folder goes back anyway.</summary>
	private const ulong StagedTimeoutMs = 180_000;

	private static readonly object SnapshotLock = new();

	/// <summary>Player one's save as the game last started or wrote it (its folder name); empty before either.</summary>
	private static string _currentSave = string.Empty;

	/// <summary>The save folder a quickload has staged, until its level stands; null when none is in flight.</summary>
	private static string? _staged;
	private static ulong _stagedAt;

	/// <summary>
	/// Copies this machine's player-one save, as it stands on disk, to the snapshot under
	/// <paramref name="key"/> (<c>"&lt;mod&gt;.&lt;name&gt;"</c>), replacing the last one. Kept beside
	/// the save, never in the load list. Take it on <c>save_written</c>, after the game's own save.
	/// False before the session has started or written a save, for a bad key, or when the copy fails.
	/// </summary>
	public bool save_snapshot(string key)
	{
		if (CurrentSaveDirectory() is not { } directory)
		{
			GD.PushWarning($"[mods] save_snapshot('{key}'): no save started or written yet");
			return false;
		}

		if (!SaveSnapshots.Take(directory, key, DateTime.UtcNow, out string error))
		{
			GD.PushWarning($"[mods] save_snapshot('{key}'): {error}");
			return false;
		}

		GD.Print($"[mods] snapshot '{key}' of save '{System.IO.Path.GetFileName(directory)}' taken");
		return true;
	}

	/// <summary>When the current save's snapshot under <paramref name="key"/> was taken, unix seconds; 0 when there is none.</summary>
	public long snapshot_time(string key) =>
		CurrentSaveDirectory() is { } directory && SaveSnapshots.Taken(directory, key) is { } taken
			? new DateTimeOffset(taken).ToUnixTimeSeconds()
			: 0;

	/// <summary>
	/// Loads the current save's snapshot under <paramref name="key"/> back into the running game:
	/// the guest's own in-game reload (<c>LoadFailQuestSave</c>), loading screen and all, with the
	/// save folder's own files put back once the level stands — the player's main save and the
	/// quest's fail-quest save are left as they were. False in co-op, with no snapshot, or while a
	/// load is in flight.
	/// </summary>
	public bool load_snapshot(string key)
	{
		if (_coopMode != 0)
		{
			GD.PushWarning($"[mods] load_snapshot('{key}'): not in co-op");
			return false;
		}

		lock (SnapshotLock)
		{
			if (_staged is not null)
			{
				return false;
			}
		}

		if (CurrentSaveDirectory() is not { } directory)
		{
			GD.PushWarning($"[mods] load_snapshot('{key}'): no save started or written yet");
			return false;
		}

		if (!SaveSnapshots.Stage(directory, key, out string error))
		{
			GD.PushWarning($"[mods] load_snapshot('{key}'): {error}");
			return false;
		}

		lock (SnapshotLock)
		{
			_staged = directory;
			_stagedAt = Time.GetTicksMsec();
		}

		if (!Post("run_lua LoadFailQuestSave()"))
		{
			FinishSnapshotLoad("the load was not posted");
			return false;
		}

		GD.Print($"[mods] snapshot '{key}' of save '{System.IO.Path.GetFileName(directory)}' loading");
		return true;
	}

	/// <summary>A written package's fields (<c>player=N,name='X'</c>) as <c>save_written</c> carries them.</summary>
	public static (string Save, int Player) ParseSaveWritten(string fields)
	{
		string save = string.Empty;
		int player = 0;
		foreach ((string key, object value) in ParseFieldsPlain(fields))
		{
			if (key == "name" && value is string s) save = s;
			if (key == "player" && value is long l) player = (int)l;
		}

		return (save, player);
	}

	private static Godot.Collections.Dictionary SaveWrittenFields(string fields)
	{
		(string save, int player) = ParseSaveWritten(fields);
		return new Godot.Collections.Dictionary { ["save"] = save, ["player"] = player };
	}

	/// <summary>The game's live save profile (the emulator's user_data_root), else the active one.</summary>
	private static string SaveRoot() =>
		HostBridge.GetSaveRoot() is { Length: > 0 } live ? live : ModSaveGate.UserDataRoot;

	private static string? CurrentSaveDirectory()
	{
		string save;
		lock (SnapshotLock)
		{
			save = _currentSave;
		}

		return save.Length == 0 ? null : Refii.Core.Engine_TitleGame.Active.FindSaveDirectory(SaveRoot(), save);
	}

	/// <summary>A quickload is over (its level stands, or it never will): the save folder gets its own files back.</summary>
	private static void FinishSnapshotLoad(string reason)
	{
		string? directory;
		lock (SnapshotLock)
		{
			directory = _staged;
			_staged = null;
		}

		if (directory is null)
		{
			return;
		}

		try
		{
			int restored = SaveSnapshots.Restore(directory);
			GD.Print($"[mods] snapshot load of save '{System.IO.Path.GetFileName(directory)}' over ({reason}): {restored} save file(s) put back");
		}
		catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[mods] snapshot load of '{directory}': the save folder could not be put back: {e.Message}");
		}
	}

	/// <summary>
	/// On the guest thread, inside start_game: names the save being started for the snapshots, and
	/// puts back a quickload's hold a crash left in that save's folder before the game reads it.
	/// </summary>
	private static void SnapshotsOnStartGame(int kind, IReadOnlyList<KeyValuePair<string, object>> fields)
	{
		if (kind != ModSaveGate.StartGameKind)
		{
			return;
		}

		string save = fields.FirstOrDefault(f => f.Key == "save").Value as string ?? string.Empty;
		lock (SnapshotLock)
		{
			// Empty for a new game: no snapshot until its first save.
			_currentSave = save;
			if (_staged is not null)
			{
				return;
			}
		}

		if (save.Length == 0 || Refii.Core.Engine_TitleGame.Active.FindSaveDirectory(SaveRoot(), save) is not { } directory ||
			!SaveSnapshots.IsStaged(directory))
		{
			return;
		}

		try
		{
			int restored = SaveSnapshots.Restore(directory);
			GD.Print($"[mods] save '{save}': a quickload's hold left by an earlier session put back ({restored} file(s))");
		}
		catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[mods] save '{save}': a quickload's hold could not be put back: {e.Message}");
		}
	}

	/// <summary>Gives experience to one of an entity's pools.</summary>
	public void give_experience(long entity, int pool, float amount) =>
		Post($"give_experience {entity} {pool} {Num(amount)}");

	/// <summary>Spends experience from one of an entity's pools.</summary>
	public void spend_experience(long entity, int pool, float amount) =>
		Post($"spend_experience {entity} {pool} {Num(amount)}");

	/// <summary>Casts a spell as caster: the spell type, targeted (true/false), the target entity (0 for none) and the level 0..4, starting at the caster's own position and facing.</summary>
	public void cast_spell(long caster, int type, bool targeted, long target, int level) =>
		Post($"cast_spell {caster} {type} {(targeted ? 1 : 0)} {target} {level}");

	/// <summary>
	/// Casts a spell as caster from a stated start position facing <paramref name="dir"/> (guest axes), as
	/// Fable III's weave places its partner casts: Fable 2's targeted classes place from the position along
	/// the facing (Lightning draws its bolt from there to the target, Fireball spawns its shot there, Force
	/// Push shoves the target away from there).
	/// </summary>
	public void cast_spell_at(long caster, int type, bool targeted, long target, int level, Vector3 pos, Vector3 dir) =>
		Post($"cast_spell {caster} {type} {(targeted ? 1 : 0)} {target} {level} {Num(pos.X)} {Num(pos.Y)} {Num(pos.Z)} {Num(dir.X)} {Num(dir.Y)} {Num(dir.Z)}");

	/// <summary>
	/// A cast with options (refii_mod_cast_spell_with): <c>tag</c> (int) is carried as <c>tag</c> by
	/// the cast's spell_created, vortex_started, hit, hit_landed, fireball_impact and status_applying;
	/// <c>damage_scale</c> (float, 1) multiplies every hit carrying its serial where the hit is decided;
	/// <c>pos</c> / <c>dir</c> (Vector3, guest axes) as <see cref="cast_spell_at"/>'s; <c>at</c> (an entity
	/// handle, or a Vector3 point in guest axes) places the cast by its class's own rule for that place
	/// (refii_mod_cast_options.at_kind: the Targeted class, <c>pos</c> then the origin it comes from and
	/// <c>dir</c> its facing when the origin stands on the place; Fireball and Chaos refuse a point, Slow
	/// Time and Raise Dead refuse both).
	/// </summary>
	public void cast_spell_with(long caster, int type, bool targeted, long target, int level, Godot.Collections.Dictionary options)
	{
		long tag = options.TryGetValue("tag", out Variant t) ? t.AsInt64() : 0;
		float scale = options.TryGetValue("damage_scale", out Variant d) ? (float)d.AsDouble() : 1f;
		string line = $"cast_spell_with {caster} {type} {(targeted ? 1 : 0)} {target} {level} {tag} {Num(scale)}";
		if (options.TryGetValue("pos", out Variant p) && p.VariantType == Variant.Type.Vector3)
		{
			Vector3 pos = p.AsVector3();
			line += $" {Num(pos.X)} {Num(pos.Y)} {Num(pos.Z)}";
			if (options.TryGetValue("dir", out Variant q) && q.VariantType == Variant.Type.Vector3)
			{
				Vector3 dir = q.AsVector3();
				line += $" {Num(dir.X)} {Num(dir.Y)} {Num(dir.Z)}";
			}
		}

		if (options.TryGetValue("at", out Variant a))
		{
			if (a.VariantType == Variant.Type.Vector3)
			{
				Vector3 point = a.AsVector3();
				line += $" at {Num(point.X)} {Num(point.Y)} {Num(point.Z)}";
			}
			else
			{
				line += $" at {a.AsInt64()}";
			}
		}

		Post(line);
	}

	/// <summary>Moves an entity to a world position.</summary>
	public void set_position(long entity, float x, float y, float z) =>
		Post($"set_position {entity} {Num(x)} {Num(y)} {Num(z)}");

	/// <summary>
	/// Turns an entity to face along (x, y) in guest axes (z up; the game drops z and keeps the facing
	/// level) through Physics.SetFacingVector — a hero's through Player.SetFacingVector (refii_mod_set_facing).
	/// <c>query facing E</c> / <c>query velocity E</c> answer x, y, z.
	/// </summary>
	public void set_facing(long entity, float x, float y, float z) =>
		Post($"set_facing {entity} {Num(x)} {Num(y)} {Num(z)}");

	/// <summary>
	/// Tints an entity through the game's own appearance setter (refii_mod_entity_tint): linear
	/// multipliers of its lit colour, 1 untouched, above 1 brighter; both looks draw it and co-op
	/// replicates it. <paramref name="a"/> is stored, never drawn: opacity is not the tint's.
	/// </summary>
	public void entity_tint(long entity, float r, float g, float b, float a) =>
		Post($"entity_tint {entity} {Num(r)} {Num(g)} {Num(b)} {Num(a)}");

	/// <summary>The short form, <c>a = 1</c>: the script bridge passes no C# default.</summary>
	public void entity_tint(long entity, float r, float g, float b) => entity_tint(entity, r, g, b, 1f);

	/// <summary>Puts an entity's tint back to the constructor's {1, 1, 1, 1} (refii_mod_entity_untint).</summary>
	public void entity_untint(long entity) => Post($"entity_untint {entity}");

	/// <summary>
	/// An entity's opacity 0..1 through the game's own SetAlpha (refii_mod_entity_alpha); 1 is the
	/// constructor's. An entity whose own update restates its alpha (a Blades sword, a dying
	/// creature) keeps the game's value.
	/// </summary>
	public void entity_alpha(long entity, float alpha) => Post($"entity_alpha {entity} {Num(alpha)}");

	/// <summary>An entity's uniform scale through the game's own SetScale (refii_mod_entity_scale); 1 is the constructor's.</summary>
	public void entity_scale(long entity, float scale) => Post($"entity_scale {entity} {Num(scale)}");

	/// <summary>The renderable's saturation mask (refii_mod_entity_saturation_mask): drawn desaturated while on; off is the constructor's.</summary>
	public void entity_saturation_mask(long entity, bool on) => Post($"entity_saturation_mask {entity} {(on ? 1 : 0)}");

	/// <summary>Whether the entity's body is drawn at all (refii_mod_entity_drawable); the entity keeps simulating. On is the constructor's.</summary>
	public void entity_drawable(long entity, bool on) => Post($"entity_drawable {entity} {(on ? 1 : 0)}");

	/// <summary>
	/// Locks player 1 or 2's hero on <paramref name="entity"/>, or with 0 on the game's own pick
	/// (refii_mod_lock_target): the game's force byte, look-at flag and SetTarget, what a held LT
	/// states. <c>query locked_target P</c> answers the target held.
	/// </summary>
	public void lock_target(int player, long entity) => Post($"lock_target {player} {entity}");

	/// <summary>The short form, <c>entity = 0</c>: the script bridge passes no C# default.</summary>
	public void lock_target(int player) => lock_target(player, 0);

	/// <summary>Ends the lock lock_target stated for player 1 or 2 (refii_mod_release_lock); a held LT is the trigger's.</summary>
	public void release_lock(int player) => Post($"release_lock {player}");

	/// <summary>
	/// States that hero <paramref name="handle"/>'s target lock switches by the MOUSE WHEEL
	/// (refii_mod_wheel_switches_target, what <c>Refii.wheel_switches_target</c> states each
	/// tick): STICKY — a Godot script runs off the guest tick, so the tick restates it on every
	/// tick from this call until <paramref name="on"/> false or the hero's player no longer holds
	/// a lock (the game's own release, or <see cref="release_lock"/>). While it holds and the hero
	/// is locked under the third-person camera the keyboard and mouse drive, the mouse's movement
	/// never switches target and each wheel notch is one switch; a pad is untouched. False for a
	/// bad handle.
	/// </summary>
	public bool wheel_switches_target(long handle, bool on) =>
		handle > 0 && handle <= uint.MaxValue && Post($"wheel_switches_target {handle} {(on ? 1 : 0)}");

	/// <summary>The short form, <c>on = true</c>: the script bridge passes no C# default.</summary>
	public bool wheel_switches_target(long handle) => wheel_switches_target(handle, true);

	/// <summary>Switches a player's locked target to the next one left or right (refii_mod_switch_target), the camera's own flick.</summary>
	public void switch_target(int player, bool left) => Post($"switch_target {player} {(left ? 1 : 0)}");

	/// <summary>
	/// Uses an inventory item of player 1 or 2's hero by its definition (its GDB record hash: the
	/// "0x…" string the events carry, or a number) through the game's own use (refii_mod_use_item):
	/// the quick menu's action, gated as the quick menu is (the hero alive, the d-pad up, the game's
	/// CanPlayerOpenQuickMenu for that player).
	/// </summary>
	public void use_item(int player, Variant definition) => Post($"use_item {player} {Arg(definition)}");

	/// <summary>
	/// Equips a weapon the hero's inventory holds, by definition, through the weapon set's own
	/// EquipWeaponByRecord (refii_mod_equip_weapon). On an online joiner the pick goes through the
	/// co-op kit to the host, who equips it and replicates the held item back — the caller never cares.
	/// </summary>
	public void equip_weapon(int player, Variant definition) => Post($"equip_weapon {player} {Arg(definition)}");

	/// <summary>Performs an expression the hero's performer knows, by its record, as a tap (refii_mod_perform_expression); <c>query expressions P</c> lists them.</summary>
	public void perform_expression(int player, Variant expression) => Post($"perform_expression {player} {Arg(expression)}");

	/// <summary>Assigns spell type 0..7 to every tier player 1 or 2's hero knows (refii_mod_assign_spell); <c>query spells_known P</c> lists the types.</summary>
	public void assign_spell(int player, int spell) => Post($"assign_spell {player} {spell}");

	/// <summary>
	/// The quick actions with options (refii_mod_quick_action): <c>{"ungated": true}</c> skips the
	/// quick menu's gate (the d-pad up and CanPlayerOpenQuickMenu) and is still refused for a dead
	/// hero and in a cutscene. <c>use_item(player, definition, {"ungated": true})</c> is the per-player
	/// form of the Items page's use (<see cref="use_item(Variant)"/>, this machine's hero).
	/// </summary>
	public void use_item(int player, Variant definition, Godot.Collections.Dictionary options) =>
		Post($"use_item {player} {Arg(definition)} {QuickFlags(options, false)}");

	/// <inheritdoc cref="use_item(int, Variant, Godot.Collections.Dictionary)"/>
	public void equip_weapon(int player, Variant definition, Godot.Collections.Dictionary options) =>
		Post($"equip_weapon {player} {Arg(definition)} {QuickFlags(options, false)}");

	/// <summary>
	/// Performs an expression, <paramref name="held"/> or a tap: a tap is released at once (the
	/// wheel's own tap) and plays the basic form; a held one is left unreleased, so an extendable
	/// expression extends when the game's own countdown runs out and plays on until
	/// <see cref="release_expression"/>.
	/// </summary>
	public void perform_expression(int player, Variant expression, bool held) =>
		Post($"perform_expression {player} {Arg(expression)} {(held ? 2 : 0)}");

	/// <summary><see cref="perform_expression(int, Variant, bool)"/> with the quick actions' options (<c>{"ungated": true}</c>).</summary>
	public void perform_expression(int player, Variant expression, bool held, Godot.Collections.Dictionary options) =>
		Post($"perform_expression {player} {Arg(expression)} {QuickFlags(options, held)}");

	/// <summary>
	/// The same through Lua's own <c>Refii.perform_expression</c> on the guest's next tick,
	/// <paramref name="callback"/>(performed: bool) on a later frame — what the Lua call returns (null when
	/// the call failed). False when the line does not fit.
	/// </summary>
	public bool perform_expression(int player, Variant expression, bool held, Godot.Collections.Dictionary options, Variant callback) =>
		CallLuaFirst($"Refii.perform_expression({player}, {LuaArg(expression)}, {(held ? "true" : "false")}, " +
			$"{{ungated = {((QuickFlags(options, false) & 1) != 0 ? "true" : "false")}}})", ModScriptCallable.Of(callback));

	/// <inheritdoc cref="use_item(int, Variant, Godot.Collections.Dictionary)"/>
	public void assign_spell(int player, int spell, Godot.Collections.Dictionary options) =>
		Post($"assign_spell {player} {spell} {QuickFlags(options, false)}");

	/// <summary>
	/// Charges spell type 0..7 through player 1 or 2's own magic charge (refii_mod_charge_spell): <paramref name="held"/>
	/// true holds the magic button with the spell lent to every tier the hero has it (the tiers above emptied; no
	/// spell_pick), false lets it go and the cast goes out as the game casts. The player's own slots come back once the
	/// charge is over. Guest tick.
	/// </summary>
	public void charge_spell(int player, int spell, bool held) => Post($"charge_spell {player} {spell} {(held ? 1 : 0)}");

	/// <summary>
	/// The same through Lua's own <c>Refii.charge_spell</c> on the guest's next tick,
	/// <paramref name="callback"/>(taken: bool) on a later frame — false when the charge was refused (the
	/// player not played here, no spell manager, the spell not known on its first tier); null when the
	/// call failed. False when the line does not fit.
	/// </summary>
	public bool charge_spell(int player, int spell, bool held, Variant callback) =>
		CallLuaFirst($"Refii.charge_spell({player}, {spell}, {(held ? "true" : "false")})", ModScriptCallable.Of(callback));

	/// <summary>A call argument as a Lua literal: a number as itself, anything else as a string.</summary>
	private static string LuaArg(Variant arg) => arg.VariantType switch
	{
		Variant.Type.Int => arg.AsInt64().ToString(CultureInfo.InvariantCulture),
		Variant.Type.Float => ModLuaLane.Literal(arg.AsDouble()),
		Variant.Type.Bool => arg.AsBool() ? "true" : "false",
		_ => ModLuaLane.Literal(arg.AsString()),
	};

	/// <summary>Ends player 1 or 2's held expression through the performer's own end, GameAction 25's call (refii_mod_release_expression).</summary>
	public void release_expression(int player) => Post($"release_expression {player}");

	/// <summary>Ends player 1 or 2's sprint as the sprint button's release does (refii_mod_end_sprint); <c>query sprinting P</c> reads it.</summary>
	public void end_sprint(int player) => Post($"end_sprint {player}");

	/// <summary>
	/// Sets LocomotionData fields on player 1 or 2's hero's own table, held across the game's
	/// reloads of it (refii_mod_set_locomotion): <c>{"MaxTurnSpeedStatic": 720, "FrictionCoefficient": 9}</c>;
	/// <c>false</c> as a value stops holding that field. <c>query locomotion P KEY</c> reads one.
	/// </summary>
	public void set_locomotion(int player, Godot.Collections.Dictionary values)
	{
		foreach (Variant key in values.Keys)
		{
			Variant value = values[key];
			string text = value.VariantType is Variant.Type.Int or Variant.Type.Float ? Arg(value) : "-";
			Post($"set_locomotion {player} {key.AsString()} {text}");
		}
	}

	/// <summary>
	/// Sets player 1 or 2's hero's parry window (seconds) and wedge (degrees) on its own copy of
	/// HeroCombatBalanceParams, live, held for that player's next hero (refii_mod_set_parry); a
	/// negative value leaves that one. <c>query parry P</c> reads both.
	/// </summary>
	public void set_parry(int player, double window, double wedge) =>
		Post($"set_parry {player} {Num((float)window)} {Num((float)wedge)}");

	/// <summary>REFII_QUICK_* from an options dictionary: 1 ungated, 2 held.</summary>
	private static int QuickFlags(Godot.Collections.Dictionary options, bool held) =>
		(options.TryGetValue("ungated", out Variant ungated) && ungated.AsBool() ? 1 : 0) |
		(held || (options.TryGetValue("held", out Variant h) && h.AsBool()) ? 2 : 0);

	/// <summary>
	/// Puts a status on an entity for seconds (refii_mod_apply_status): kind 1 electrified, 2 chaos,
	/// 3 burning (4, a vortex, is refused), at spell level 0..4, from <paramref name="source"/> (0:
	/// the entity itself; chaos takes its direction from it). The status_effect event fires.
	/// </summary>
	public void apply_status(long entity, int kind, float seconds, int level, long source) =>
		Post($"apply_status {entity} {kind} {Num(seconds)} {level} {source}");

	/// <summary>The short form, <c>source = 0</c>: the script bridge passes no C# default.</summary>
	public void apply_status(long entity, int kind, float seconds, int level) => apply_status(entity, kind, seconds, level, 0);

	/// <summary>The short form, <c>level = 0, source = 0</c>: the script bridge passes no C# default.</summary>
	public void apply_status(long entity, int kind, float seconds) => apply_status(entity, kind, seconds, 0, 0);

	/// <summary>Ends a status through the game's own remover (refii_mod_clear_status). <c>query has_status E KIND</c> asks.</summary>
	public void clear_status(long entity, int kind) => Post($"clear_status {entity} {kind}");

	/// <summary>
	/// Overrides player 1 or 2's movement speed by a factor (refii_mod_set_speed_multiplier): the
	/// player component's own override, what the scripts' Player.SetOverriddenMovementSpeedMultiplier
	/// and SetPlayerMovementSpeedOverridden state; 0 holds the hero in place. <c>query speed_multiplier P</c>
	/// answers whether one holds and the factor — take it only while none holds, and give it back.
	/// </summary>
	public void set_speed_multiplier(int player, float factor) => Post($"set_speed_multiplier {player} {Num(factor)}");

	/// <summary>Clears the override set_speed_multiplier stated (refii_mod_clear_speed_multiplier).</summary>
	public void clear_speed_multiplier(int player) => Post($"clear_speed_multiplier {player}");

	/// <summary>
	/// Grants an action's "ALLOW_ACTION_INTERRUPT" (refii_mod_allow_action_interrupt): what the
	/// animation event itself does, so a cancelled anim_event (a committed swing) is granted later.
	/// <c>query current_action E</c> answers the action an entity's manager runs, its clock and whether it is granted.
	/// </summary>
	public void allow_action_interrupt(long action) => Post($"allow_action_interrupt {action}");

	/// <summary>
	/// Grants an action's "ALLOW_ACTION_INTERRUPT" once its own clock reaches <paramref name="seconds"/>
	/// (refii_mod_allow_action_interrupt_at): at once when it already has, else on the guest tick it
	/// does — no polling of the clock; dropped when the action stops first.
	/// </summary>
	public void allow_action_interrupt_at(long action, float seconds) => Post($"allow_action_interrupt_at {action} {Num(seconds)}");

	/// <summary>
	/// Knocks an entity down and away along a direction scaled by force (refii_mod_knockback): a
	/// Force Push's own hit with no damage and no attacker through the game's one hit receiver.
	/// </summary>
	public void knockback(long entity, float x, float y, float z, float force) =>
		Post($"knockback {entity} {Num(x)} {Num(y)} {Num(z)} {Num(force)}");

	/// <summary>The short form, <c>force = 1</c>: the script bridge passes no C# default.</summary>
	public void knockback(long entity, float x, float y, float z) => knockback(entity, x, y, z, 1f);

	/// <summary>Staggers an entity: the flinch a 0-damage melee tap plays (refii_mod_stagger).</summary>
	public void stagger(long entity) => Post($"stagger {entity}");

	/// <summary>
	/// Plays an audio cue on an entity through the game's own cue player (refii_mod_play_sound_on,
	/// the body of the scripts' Sound.PlayEvent): the entity's sound component's emitter, or its
	/// position when it has none. <paramref name="tag"/> is what stop_sound matches. The cue's own
	/// volume applies; cue names are the audio banks' event names (docs/modding/gameplay.md, Sound).
	/// </summary>
	public void play_sound(string cue, long entity, string tag) =>
		Post($"play_sound {Word(cue)} {entity} {TagWord(tag)}");

	/// <summary>The short form, <c>tag = ""</c>: the script bridge passes no C# default.</summary>
	public void play_sound(string cue, long entity) => play_sound(cue, entity, "");

	/// <summary>Plays an audio cue at a world point on an emitter of the API's own (refii_mod_play_sound_at; eight kept, the oldest freed).</summary>
	public void play_sound_at(string cue, float x, float y, float z, string tag) =>
		Post($"play_sound_at {Word(cue)} {Num(x)} {Num(y)} {Num(z)} {TagWord(tag)}");

	/// <summary>The short form, <c>tag = ""</c>: the script bridge passes no C# default.</summary>
	public void play_sound_at(string cue, float x, float y, float z) => play_sound_at(cue, x, y, z, "");

	/// <summary>Stops the voices under a tag on an entity's emitter, or with entity 0 on the point emitters, fading over milliseconds (refii_mod_stop_sound).</summary>
	public void stop_sound(long entity, string tag, int fade_ms) =>
		Post($"stop_sound {entity} {TagWord(tag)} {fade_ms}");

	/// <summary>The short form, <c>fade_ms = 0</c>: the script bridge passes no C# default.</summary>
	public void stop_sound(long entity, string tag) => stop_sound(entity, tag, 0);

	/// <summary>Starts a music cue through SoundTools.PlayMusic's own body (refii_mod_play_music): the MUSIC_* cues the quests start.</summary>
	public void play_music(string name) => Post($"play_music {Word(name)}");

	/// <summary>Stops a music cue with the game's 2 s fade (refii_mod_stop_music).</summary>
	public void stop_music(string name) => Post($"stop_music {Word(name)}");

	/// <summary>
	/// Plays a named animation on an entity as the shipped AI does (refii_mod_play_animation):
	/// Action.SetCurrentAction with a PLAY_ANIMATION action — Anim the GDB animation record's name,
	/// SpeedMultiplier, BlendInTime — wrapped in a LOOP of <paramref name="loops"/> above 1. The
	/// game's evaluator plays it and the pose feed shows it; a higher-priority action keeps the body.
	/// </summary>
	public void play_animation(long entity, string name, int loops, float blend_seconds, float speed) =>
		Post($"play_animation {entity} {Word(name)} {loops} {Num(blend_seconds)} {Num(speed)}");

	/// <summary>The short form, <c>speed = 1</c>: the script bridge passes no C# default.</summary>
	public void play_animation(long entity, string name, int loops, float blend_seconds) => play_animation(entity, name, loops, blend_seconds, 1f);

	/// <summary>The short form, <c>blend_seconds = 0.1, speed = 1</c>: the script bridge passes no C# default.</summary>
	public void play_animation(long entity, string name, int loops) => play_animation(entity, name, loops, 0.1f, 1f);

	/// <summary>The short form, <c>loops = 1, blend_seconds = 0.1, speed = 1</c>: the script bridge passes no C# default.</summary>
	public void play_animation(long entity, string name) => play_animation(entity, name, 1, 0.1f, 1f);

	/// <summary>Ends an entity's scripted actions, the played animation among them (refii_mod_stop_animation: Action.FinishScriptedActions).</summary>
	public void stop_animation(long entity) => Post($"stop_animation {entity}");

	/// <summary>
	/// Moves a villager's opinion of a hero on an axis (0 love, 1 fear, 2 attractiveness, 3 morality,
	/// 4 respect) by delta through the game's own ModifyAxisValue, clamped to [-1, 1]
	/// (refii_mod_modify_opinion); player two's lands in their own opinion block; opinion_modified fires.
	/// </summary>
	public void modify_opinion(long villager, long hero, int axis, float delta) =>
		Post($"modify_opinion {villager} {hero} {axis} {Num(delta)}");

	/// <summary>
	/// Tells a villager the hero did a deed (refii_mod_add_deed): the deed message the game's own
	/// debug post sends, from the hero to the villager at the hero's position; the Deed record
	/// (<paramref name="deed"/> its GDB hash, a "0x…" string or number) decides the strength and
	/// kind, the villager's opinion component takes it, opinion_deed fires.
	/// </summary>
	public void add_deed(long villager, long hero, Variant deed) => Post($"add_deed {villager} {hero} {Arg(deed)}");

	/// <summary>
	/// An NPC follows a target as a quest's Follow.FollowEntity orders it (refii_mod_npc_follow):
	/// the follow component's own entry, radius the quests' 2 by default; BehaviourFollow walks it.
	/// </summary>
	public void npc_follow(long entity, long target, float radius) => Post($"npc_follow {entity} {target} {Num(radius)}");

	/// <summary>The short form, <c>radius = 2</c>: the script bridge passes no C# default.</summary>
	public void npc_follow(long entity, long target) => npc_follow(entity, target, 2f);

	/// <summary>An NPC walks to a point as a quest's ScriptFunction.StartScriptControlledMovement orders it (refii_mod_npc_go_to); not waited for.</summary>
	public void npc_go_to(long entity, float x, float y, float z) => Post($"npc_go_to {entity} {Num(x)} {Num(y)} {Num(z)}");

	/// <summary>An NPC runs from an entity as the AI's BehaviourBase:FleeFromEntityNoWait does (refii_mod_npc_flee: Navigation.FleeFromEntity at sprint); one order, the AI may retake the body.</summary>
	public void npc_flee(long entity, long from) => Post($"npc_flee {entity} {from}");

	/// <summary>An NPC attacks a target as the quests order it (refii_mod_npc_attack: CombatRegister:SetScriptTarget — the enemy relationship and the combat info's ScriptTarget).</summary>
	public void npc_attack(long entity, long target) => Post($"npc_attack {entity} {target}");

	/// <summary>
	/// An NPC holds another entity — a hero (player two's is its own) or anything — as an enemy, or no
	/// longer (refii_mod_npc_set_hostile: Faction.AddTemporaryEntityRelationship FACTION_STATUS_ENEMY /
	/// RemoveTemporaryEntityRelationship, the faction component's own lists).
	/// </summary>
	public void npc_set_hostile(long entity, long other, bool on) => Post($"npc_set_hostile {entity} {other} {(on ? 1 : 0)}");

	/// <summary>Ends every order above (refii_mod_npc_stop: StopFollowing, StopScriptControlledMovement, ClearScriptTarget); the AI mind picks its next behaviour.</summary>
	public void npc_stop(long entity) => Post($"npc_stop {entity}");

	/// <summary>
	/// Sets the game clock to hours 0..24 (refii_mod_set_time_of_day: Timing.SetTimeOfDayAbsolute's
	/// body on the time manager, clamped, told to an online partner); <c>query time_of_day</c> reads it back.
	/// </summary>
	public void set_time_of_day(float hours) => Post($"set_time_of_day {Num(hours)}");

	/// <summary>
	/// Blends the GAME's environment theme — its weather and sky — as the quests do
	/// (refii_mod_set_weather: EnvironmentTheme.BlendToEnvironmentTheme(theme, weight, seconds); the
	/// weather_theme event fires). The Dynamic Sky's own weather override is the host's switch, not this.
	/// </summary>
	public void set_weather(string theme, float weight, float seconds) =>
		Post($"set_weather {Word(theme)} {Num(weight)} {Num(seconds)}");

	/// <summary>The short form, <c>seconds = 1</c>: the script bridge passes no C# default.</summary>
	public void set_weather(string theme, float weight) => set_weather(theme, weight, 1f);

	/// <summary>The short form, <c>weight = 1, seconds = 1</c>: the script bridge passes no C# default.</summary>
	public void set_weather(string theme) => set_weather(theme, 1f, 1f);

	/// <summary>A name on a command line: no spaces (a space becomes '_'); empty is "-".</summary>
	private static string Word(string name) =>
		string.IsNullOrEmpty(name) ? "-" : name.Replace(' ', '_');

	private static string TagWord(string tag) => Word(tag);

	/// <summary>The inputs <see cref="set_appearance"/> takes, as the guest's appearance_set line names them.</summary>
	private static readonly string[] AppearanceInputs = { "tan", "fat", "height", "strength", "skill", "will", "morality", "purity", "age" };

	/// <summary>
	/// Switches player <paramref name="player"/>'s look to manual with the named inputs of
	/// <paramref name="inputs"/> (<c>Refii.set_appearance</c>): tan, fat, height, strength, skill,
	/// will, morality, purity, age; an input not named keeps how the hero looks. The Mirror's own
	/// look: the same player store, kept in that player's Mirror settings (refii_mod_set_appearance).
	/// Read it back with the query <c>appearance P</c>.
	/// </summary>
	public void set_appearance(int player, Godot.Collections.Dictionary inputs)
	{
		var line = new StringBuilder($"appearance_set {player}");
		foreach (string name in AppearanceInputs)
		{
			if (inputs.TryGetValue(name, out Variant value) && value.VariantType is Variant.Type.Float or Variant.Type.Int)
			{
				line.Append(' ').Append(name).Append('=').Append(Num((float)value.AsDouble()));
			}
		}

		Post(line.ToString());
	}

	/// <summary>Player <paramref name="player"/>'s look back to the game's own (true) or to the manual inputs it holds (false) (<c>Refii.set_appearance_automatic</c>).</summary>
	public void set_appearance_automatic(int player, bool on) => Post($"appearance_automatic {player} {(on ? 1 : 0)}");

	/// <summary>
	/// Player <paramref name="player"/>'s weight for a mod morph (<c>Refii.morph_get</c>), read from the
	/// player store at the call: the stored weight clamped to the declared range, else the default; NaN for
	/// a morph no enabled mod declares. <paramref name="morph"/> is <c>"&lt;mod&gt;.&lt;name&gt;"</c> or its
	/// id <c>"0x…"</c> (<see cref="Refii.Hero.Engine_ModMorphs"/>).
	/// </summary>
	public float morph_get(int player, string morph)
	{
		if (player is not (1 or 2) || Refii.Hero.Engine_ModMorphs.Find(morph ?? string.Empty) is not { } declared)
		{
			return float.NaN;
		}

		float stored = player_value(player, declared.StoreKey);
		return Refii.Hero.Engine_ModMorphs.Weight(declared, float.IsNaN(stored) ? null : stored);
	}

	/// <summary>
	/// Sets player <paramref name="player"/>'s weight for a mod morph (<c>Refii.morph_set</c>), clamped and
	/// saved with the hero where it is played; the hero is redrawn and <c>morph_changed</c> raised
	/// (refii_mod_set_morph_weight, the line <c>morph_set P MORPH W</c>).
	/// </summary>
	public void morph_set(int player, string morph, float weight)
	{
		Post($"morph_set {player} {morph_id(morph ?? string.Empty)} {Num(weight)}");
	}

	/// <summary>The MorphType of <c>"&lt;mod&gt;.&lt;name&gt;"</c> as <c>"0x…"</c> (<c>Refii.morph_id</c>), the one its GDB records carry.</summary>
	public string morph_id(string name) =>
		Refii.Hero.Engine_ModMorphs.Hex(Refii.Hero.Engine_ModMorphs.ParseId(name ?? string.Empty) ?? Refii.Hero.Engine_ModMorphs.Id(name ?? string.Empty));

	/// <summary>
	/// Sets a player's SESSION value (<c>Refii.player_set</c>): the player store refii_mod_player_*
	/// keeps, the same on both co-op machines. <paramref name="key"/> is "&lt;mod&gt;.&lt;name&gt;", no spaces.
	/// </summary>
	public void player_set(int player, string key, float value) =>
		Post($"player_set {player} {key.Replace(' ', '_')} {Num(value)}");

	/// <summary>Adds to a player's value (<c>Refii.player_add</c>; missing counts as 0); <paramref name="saved"/> keeps the key with the hero's save.</summary>
	public void player_add(int player, string key, float delta, bool saved) =>
		Post($"player_add {player} {key.Replace(' ', '_')} {Num(delta)} {(saved ? 1 : 0)}");

	/// <summary>Sets a player's SAVED value (<c>Refii.player_save</c>): kept with that hero's own save, back when it loads.</summary>
	public void player_save(int player, string key, float value) =>
		Post($"player_save {player} {key.Replace(' ', '_')} {Num(value)}");

	/// <summary>
	/// Sets a player's session TEXT (<c>Refii.player_set_text</c>): a typed value in the same store, at
	/// most 256 bytes, kept exactly. Read back with <see cref="player_text"/> (or the query <c>player_text P KEY</c>).
	/// </summary>
	public void player_set_text(int player, string key, string text) =>
		Post($"player_set_text {player} {key.Replace(' ', '_')} {TextWord(text)}");

	/// <summary>Sets a player's SAVED text (<c>Refii.player_save_text</c>): kept with that hero's own save.</summary>
	public void player_save_text(int player, string key, string text) =>
		Post($"player_save_text {player} {key.Replace(' ', '_')} {TextWord(text)}");

	/// <summary>
	/// Sets a player's SAVED hash (<c>Refii.player_save_hash</c>): a record hash ("0x…" or decimal)
	/// kept exactly as its "0x%08x" text. False when <paramref name="hash"/> is not one.
	/// </summary>
	public bool player_save_hash(int player, string key, string hash)
	{
		string text = (hash ?? string.Empty).Trim();
		bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			? uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)
			: uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
		if (!parsed)
		{
			return false;
		}

		player_save_text(player, key, ModRowSubject.Hex(value));
		return true;
	}

	/// <summary>A text as one command word: base64, "-" for empty (mod_commands.cpp player_set_text).</summary>
	private static string TextWord(string? text) =>
		string.IsNullOrEmpty(text) ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(text.Length > 256 ? text[..256] : text));

	/// <summary>Sets an integer under <c>RefiiModSave.&lt;key&gt;</c>, persisted with the save; the key carries no spaces.</summary>
	public void save_set(string key, long value) =>
		Post($"save_set {key.Replace(' ', '_')} {value}");

	/// <summary>
	/// Asks the guest a question (<c>health</c>, <c>position</c>, <c>gold</c>, <c>item_count</c>,
	/// <c>experience</c>, <c>ability_level</c>, <c>relation</c>, <c>alive</c>, <c>definition</c>,
	/// <c>name</c>, <c>level_name</c>, <c>hero</c>, <c>player_of</c>, <c>lua_int</c>, <c>time_of_day</c>, and the co-op
	/// surface the Lua side answers: <c>player_for E</c> (who "the hero" is for E: a dog its master,
	/// else the nearer hero), <c>world_here</c>, <c>combat_state P</c>, <c>local_hero</c>, <c>coop_mode</c>, <c>menu_player</c>,
	/// <c>coop_state</c>, <c>player_value P KEY [FALLBACK]</c>, <c>player_here P</c>,
	/// <c>player_dog P</c>, <c>focus P</c>, <c>opinion E P</c>, <c>uid E</c> — ModApi.GodotHelpers)
	/// with its arguments, and returns the id the answer will carry on <c>reply(id, result)</c>; 0
	/// when the line could not be posted. The answer comes on a later frame, after the guest's tick.
	/// </summary>
	public long query(string what, Godot.Collections.Array args)
	{
		long id = ++_nextQueryId;
		StringBuilder line = (_queryLine ??= new StringBuilder()).Clear()
			.Append("query ").Append(id).Append(' ').Append(what.Replace(' ', '_'));
		int count = args.Count;
		for (int i = 0; i < count; i++)
		{
			line.Append(' ').Append(Arg(args[i]));
		}

		return Post(line.ToString()) ? id : 0;
	}

	[ThreadStatic]
	private static StringBuilder? _queryLine;

	/// <summary>The host's own <see cref="query"/> of the co-op state, asked every frame: the same line, no script's Array.</summary>
	private long QueryCoopState()
	{
		long id = ++_nextQueryId;
		return Post(string.Create(CultureInfo.InvariantCulture, $"query {id} coop_state")) ? id : 0;
	}

	/// <summary>One line to the guest tick; false, and nothing posted, when it would overflow the tick's buffer.</summary>
	private static bool Post(string line)
	{
		if (Encoding.UTF8.GetByteCount(line) >= CommandLimit)
		{
			GD.PushWarning($"[mods] godot: command of {Encoding.UTF8.GetByteCount(line)} bytes is over the {CommandLimit} limit; not posted");
			return false;
		}

		HostBridge.PostModCommand(line);
		return true;
	}

	private static string Num(float value) => value.ToString("R", CultureInfo.InvariantCulture);

	/// <summary>A query argument as the grammar reads it: a decimal number, true/false as 1/0, or the text itself.</summary>
	private static string Arg(Variant arg) => arg.VariantType switch
	{
		Variant.Type.Bool => arg.AsBool() ? "1" : "0",
		Variant.Type.Int => arg.AsInt64().ToString(CultureInfo.InvariantCulture),
		Variant.Type.Float => Num((float)arg.AsDouble()),
		_ => arg.AsString(),
	};

	/// <summary>
	/// The player's hero entity as the bus last stated it (hero_ready / hero_gone), or 0. Player 1 is
	/// the host's hero on both machines: on an online joiner <c>hero(1)</c> is the partner, and
	/// "this machine's hero" is <see cref="local_hero"/>.
	/// </summary>
	public long hero(int player) => _hero.TryGetValue(player, out long id) ? id : 0;

	/// <summary>
	/// The local player's hero handle, as the guest last answered (Refii.local_hero; asked every
	/// frame while a script is loaded, so one guest tick behind), or 0: player two's on an online
	/// joiner, player one's on a host or alone, and in local co-op the hero of whoever owns the
	/// open menus. Name a player with <see cref="hero"/> when it must not follow the menus.
	/// </summary>
	public long local_hero() => _localHero;

	/// <summary>
	/// The cutscene statements holding for player 1 or 2 (0: either player) as the bus last stated
	/// them, as bits: <see cref="ModApi.CutsceneFull"/> 1 (every player's), <see cref="ModApi.CutsceneInteractive"/>
	/// 2, <see cref="ModApi.CutsceneLookAt"/> 4 (the game gave that player's held LT a camera). One
	/// guest tick behind, like every signal; <c>query in_cutscene P</c> asks the guest now.
	/// </summary>
	public int cutscene_kinds(int player) => _cutscene.Kinds(player);

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public int cutscene_kinds() => cutscene_kinds(0);

	/// <summary>Whether any of <paramref name="kinds"/> (bits; all three by default) holds for player 1 or 2 (0: either): <see cref="cutscene_kinds"/>.</summary>
	public bool in_cutscene(int player, int kinds) => _cutscene.In(player, kinds);

	/// <summary>The short form, <c>kinds = ModApi.CutsceneAll</c>: the script bridge passes no C# default.</summary>
	public bool in_cutscene(int player) => in_cutscene(player, ModApi.CutsceneAll);

	/// <summary>The short form, <c>player = 0, kinds = ModApi.CutsceneAll</c>: the script bridge passes no C# default.</summary>
	public bool in_cutscene() => in_cutscene(0, ModApi.CutsceneAll);

	/// <summary>
	/// Whether <paramref name="player"/> (1 or 2) is IN PLAY — the same composition as Lua's
	/// <c>Refii.in_play</c>: no pause or GUI mode and no level change in force (the paused event's
	/// modes), no level loading (level_unloading .. level_loaded), the player's hero standing
	/// (<see cref="hero"/>), and no FULL cutscene for them (<see cref="in_cutscene(int, int)"/>). As the
	/// bus last stated it, a guest tick behind like every signal.
	/// </summary>
	public bool in_play(int player) =>
		_play.InPlay(hero(player is 2 ? 2 : 1) != 0, in_cutscene(player is 2 ? 2 : 1, ModApi.CutsceneFull));

	/// <summary>The named numbers every surface states the same (<see cref="ModApi.NamedNumbers"/>): SPELL_*, ABILITY_*, REQUEST_*, ACTION_*, RELATION_*, CUTSCENE_* ….</summary>
	public Godot.Collections.Dictionary constants()
	{
		var all = new Godot.Collections.Dictionary();
		foreach ((string name, int value) in ModApi.NamedNumbers)
		{
			all[name] = value;
		}

		return all;
	}

	/// <summary>
	/// A spell type's (0..7) name tag and wheel icon path, the ones the game draws it with — a
	/// stand-in's overlays answer for it (<see cref="ModSpells.Info"/>): <c>{ name_tag, icon }</c>,
	/// empty for a type outside 0..7.
	/// </summary>
	public Godot.Collections.Dictionary spell_info(int type) =>
		ModSpells.Info(type) is { } info
			? new Godot.Collections.Dictionary { ["name_tag"] = info.NameTag, ["icon"] = info.Icon }
			: new Godot.Collections.Dictionary();

	/// <summary>Refii.coop_mode as the guest last answered: 0 alone, 1 local co-op, 2 online hosting, 3 online joined.</summary>
	public int coop_mode() => _coopMode;

	/// <summary>Refii.menu_player as the guest last answered: whose menus are open on this machine, 1 or 2.</summary>
	public int menu_player() => _menuPlayer;

	/// <summary>
	/// Refii.world_here as the guest last answered: whether this machine runs the shared world (the
	/// AI, every hit's outcome, the NPCs) — alone, in local co-op and on an online host; false on an
	/// online joiner. A script's world effects (give_gold to an NPC, spawn, cast_spell on a victim)
	/// belong where this holds; an event dictionary with <c>replica = true</c> is the other machine's.
	/// </summary>
	public bool world_here() => _coopMode != 3;

	/// <summary>
	/// Refii.player_here as the guest last answered (coop_state): whether this machine plays player
	/// <paramref name="player"/>'s hero (1 or 2) — both alone and in local co-op, player one on an
	/// online host, player two on an online joiner. A player's own effects, refills and timers run
	/// where this holds; a player value written elsewhere goes to that machine first.
	/// </summary>
	public bool player_here(int player) => player is 1 or 2 && _coopMode switch
	{
		2 => player == 1,
		3 => player == 2,
		_ => true,
	};

	/// <summary>
	/// The node Godot draws an entity with, by the HANDLE an event gave the script, or null when
	/// the entity has none (not drawn, not yet built, or the guest draws it itself). Parent a light
	/// or an effect to it to follow the creature; never move it — the entity feed owns its transform.
	/// </summary>
	public Node3D? node_for(long handle)
	{
		if (handle <= 0 || handle > uint.MaxValue)
		{
			return null;
		}

		uint address = Refii.Host.HostBridge.ModHandleAddress((uint)handle);
		return address == 0 ? null : Refii.Game.GameEntities.Current?.NodeFor(address);
	}

	/// <summary>The enabled mods' names, in load order.</summary>
	public Godot.Collections.Array<string> mods() => new(EnabledMods);

	// ---- the UI: mod menus, message boxes, published values ----------------------------------

	/// <summary>
	/// Publishes a mod value under <paramref name="key"/> ("&lt;mod&gt;.&lt;name&gt;", no spaces): what
	/// a HUD piece reads as <c>mod:&lt;key&gt;</c>, Lua as <c>Refii.value(key)</c> and native code as
	/// <c>refii::mods::Published</c>. Set on the guest's next tick, the moment every reader sees it.
	/// </summary>
	public void publish(string key, float value) =>
		Post($"publish {key.Replace(' ', '_')} {Num(value)}");

	/// <summary>
	/// Opens a menu a mod defines in <c>ui/templates.json</c> for <paramref name="player"/> (1 or 2;
	/// 0, the default, for whoever owns the menus here): over whichever menu list has the input (the
	/// front end, the pause book or a screen's list) — B returns to it — or, with no list up, in
	/// play or on the title, as a list of its own that takes the pad and closes on B. False when no
	/// template has that name or the player is played on the other machine (which opens it there).
	/// </summary>
	public bool open_menu(string name, long player) =>
		Refii.Ui.Engine_UiModMenus.OpenFor(name, (int)Math.Clamp(player, 0, 2));

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public bool open_menu(string name) => open_menu(name, 0);

	/// <summary>
	/// States the rows of a menu list for <paramref name="player"/> (1 or 2; 0, the default, for every
	/// player), replacing what was stated before — what a menu or entry with <c>"source": "rows"</c>
	/// (or <c>rows:&lt;list&gt;</c>) shows, on screen at once if it is open. Each row is a string (its
	/// name and label) or a dictionary <c>{ name, label, command, ghosted }</c>: <c>label</c> a Babel
	/// tag or literal, <c>command</c> any row verb (<c>Mod:lua:</c>, <c>Opens:</c>, <c>Screen:open:</c>,
	/// a registered command). An empty array clears the list.
	/// </summary>
	public void set_menu_rows(string menu, Godot.Collections.Array rows, long player)
	{
		var stated = new List<Refii.Ui.Engine_UiModRows.Stated>();
		foreach (Variant row in rows)
		{
			if (row.VariantType == Variant.Type.Dictionary)
			{
				Godot.Collections.Dictionary record = row.AsGodotDictionary();
				string Text(string key) => record.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
				string name = Text("name").Length != 0 ? Text("name") : Text("label");
				if (name.Length != 0)
				{
					stated.Add(new Refii.Ui.Engine_UiModRows.Stated(name, Text("label"),
						Text("command") is { Length: > 0 } command ? command : null,
						record.TryGetValue("ghosted", out Variant ghosted) && ghosted.AsBool()));
				}
			}
			else if (row.AsString() is { Length: > 0 } name)
			{
				stated.Add(new Refii.Ui.Engine_UiModRows.Stated(name, name, null, false));
			}
		}

		Refii.Ui.Engine_UiModRows.Set(menu, stated, (int)Math.Clamp(player, 0, 2));
	}

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public void set_menu_rows(string menu, Godot.Collections.Array rows) => set_menu_rows(menu, rows, 0);

	/// <summary>
	/// Opens a host-owned screen by id for <paramref name="player"/> (0, the default, for whoever owns
	/// the menus here) — the <c>Screen:open:&lt;id&gt;</c> verb, as a row on the list with the input
	/// would: the game's (achievements 1013, multiplayer 1014) or a <c>screens</c> record a mod states
	/// (id 1000 and up, with a <c>menu</c> and a <c>presentation</c>). A template book stands over
	/// the pause book, so it opens while the pause list has the input. False when it did not open
	/// (the log says why) or the player is played on the other machine.
	/// </summary>
	public bool open_screen(long screen, long player) =>
		screen is > 0 and <= uint.MaxValue &&
		Refii.Ui.Engine_UiModRows.OpenScreen((uint)screen, (int)Math.Clamp(player, 0, 2));

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public bool open_screen(long screen) => open_screen(screen, 0);

	/// <summary>
	/// Registers a ROW VERB every list runs (<c>Refii.Ui.Engine_UiCommands</c>): a row whose command is
	/// <paramref name="verb"/> — or, when it ends in <c>:</c>, begins with it — calls
	/// <paramref name="handler"/> with <c>(argument, menu, row, player)</c>. A return of <c>false</c>
	/// reports the row refused; anything else is done. The game's verbs stay the game's: false when
	/// the name is one of them.
	/// </summary>
	/// <summary>
	/// Registers a ROW SOURCE every list's template may name (<c>"source": "&lt;name&gt;[:&lt;argument&gt;]"</c>,
	/// Refii.Ui.Engine_UiMenuSources): <paramref name="rows"/> is called with the argument and
	/// returns an Array of Dictionaries, one row each — <c>name</c> (required, unique on the list),
	/// <c>label</c>, <c>icon</c>, <c>command</c> (a verb the row runs on A), <c>rating</c> (0..10
	/// stars), <c>ghosted</c>, <c>group</c> (a header the row sits under), <c>emphasised</c>,
	/// <c>counted</c>, <c>unviewed</c>. The game's sources stay the game's: false when the name is
	/// one of them.
	/// </summary>
	public bool register_source(string name, Variant rows)
	{
		Callable source = ModScriptCallable.Of(rows);
		if (string.IsNullOrWhiteSpace(name) || Refii.Ui.Engine_UiMenuSources.IsGameSource(name))
		{
			return false;
		}

		Refii.Ui.Engine_UiMenuSources.Register(name, argument =>
		{
			var list = new List<Refii.Ui.Engine_UiMenuSources.Row>();
			if (source.Target is { } target && !IsInstanceValid(target))
			{
				return list;
			}

			Variant answer = source.Call(argument);
			if (answer.VariantType != Variant.Type.Array)
			{
				return list;
			}

			foreach (Variant item in answer.AsGodotArray())
			{
				if (item.VariantType != Variant.Type.Dictionary)
				{
					continue;
				}

				Godot.Collections.Dictionary row = item.AsGodotDictionary();
				string rowName = Text(row, "name");
				if (rowName.Length == 0)
				{
					continue;
				}

				string command = Text(row, "command");
				list.Add(new Refii.Ui.Engine_UiMenuSources.Row(
					Name: rowName,
					Label: Text(row, "label", rowName),
					Icon: Text(row, "icon"),
					EventType: 0,
					Id: 0u,
					Rating: Number(row, "rating", Refii.Ui.Engine_UiMenuConstants.EmptyRatingSlide),
					Committable: false,
					Ghosted: Flag(row, "ghosted"),
					Command: command.Length != 0 ? command : null,
					Group: Text(row, "group"),
					GroupTag: Text(row, "group"),
					Emphasised: Flag(row, "emphasised"),
					Counted: !row.ContainsKey("counted") || Flag(row, "counted"),
					Unviewed: Flag(row, "unviewed")));
			}

			return list;
		});
		return true;

		static string Text(Godot.Collections.Dictionary row, string key, string fallback = "") =>
			row.TryGetValue(key, out Variant value) && value.VariantType == Variant.Type.String ? value.AsString() : fallback;
		static bool Flag(Godot.Collections.Dictionary row, string key) =>
			row.TryGetValue(key, out Variant value) && value.VariantType == Variant.Type.Bool && value.AsBool();
		static int Number(Godot.Collections.Dictionary row, string key, int fallback) =>
			row.TryGetValue(key, out Variant value) && (value.VariantType == Variant.Type.Int || value.VariantType == Variant.Type.Float)
				? (int)value.AsDouble()
				: fallback;
	}

	/// <summary>
	/// Registers a ROW CONDITION every list's entries may name in <c>when</c>
	/// (Refii.Ui.Engine_UiRowConditions): <paramref name="holds"/> is called when a list is built
	/// and answers whether the rows naming it show. The game's conditions stay the game's.
	/// </summary>
	public bool register_condition(string name, Variant holds)
	{
		Callable condition = ModScriptCallable.Of(holds);
		if (string.IsNullOrWhiteSpace(name) || Array.IndexOf(Refii.Ui.Engine_UiRowConditions.Game, name) >= 0)
		{
			return false;
		}

		Refii.Ui.Engine_UiRowConditions.Register(name, () =>
		{
			if (condition.Target is { } target && !IsInstanceValid(target))
			{
				return false;
			}

			Variant answer = condition.Call();
			return answer.VariantType == Variant.Type.Bool && answer.AsBool();
		});
		return true;
	}

	public bool register_command(string verb, Variant handler) => register_command(verb, handler, false);

	/// <summary>
	/// <see cref="register_command(string, Callable)"/>, and with <paramref name="with_row"/> the handler is
	/// called <c>(argument, e)</c> instead: <c>e</c> the row as a <c>mod_menu_row</c> Dictionary — menu, row,
	/// player, mod, command, button and the row's TYPED SUBJECT (subject_kind, definition, spell) — the table a
	/// Lua row function (<c>Mod:lua:name</c>) is called with, so a host list's pick arrives typed.
	/// </summary>
	public bool register_command(string verb, Variant handler, bool with_row)
	{
		Callable command = ModScriptCallable.Of(handler);
		var record = new Refii.Ui.Engine_UiVerb(verb, verb, "a Godot script's verb", Array.Empty<Refii.Ui.Engine_UiCommandList>(),
			Owner: "godot");
		return Refii.Ui.Engine_UiCommands.Register(record, (argument, context) =>
		{
			if (command.Target is { } target && !IsInstanceValid(target))
			{
				return Refii.Ui.Engine_UiCommandResult.Refused("the script that registered it is gone");
			}

			Variant answer;
			if (with_row)
			{
				ModRowSubject.Subject subject = ModRowSubject.Of(context.Row, context.RowCommand);
				var row = new Godot.Collections.Dictionary
				{
					["menu"] = context.Menu,
					["row"] = context.Row,
					["player"] = (long)(context.Player is 1 or 2 ? context.Player : Refii.Ui.Engine_UiModMenus.MenuPlayer),
					["mod"] = context.Mod,
					["command"] = context.RowCommand,
					["button"] = context.Button,
					["subject_kind"] = subject.Kind,
					["definition"] = subject.Definition,
					["spell"] = (long)subject.Spell,
				};
				answer = command.Call(argument, row);
				GC.KeepAlive(row);
			}
			else
			{
				answer = command.Call(argument, context.Menu, context.Row, (long)context.Player);
			}

			return answer.VariantType == Variant.Type.Bool && !answer.AsBool()
				? Refii.Ui.Engine_UiCommandResult.Refused("the script's handler answered false")
				: Refii.Ui.Engine_UiCommandResult.Done;
		});
	}

	private long _nextBoxId = Refii.Ui.Engine_UiModRequest.FirstHostBoxId - 1;

	/// <summary>
	/// Raises a message box in the game's own box art — in game the query or message box, on the
	/// front end its own dialog — for <paramref name="player"/> (1 or 2; 0, the default, for whoever
	/// owns the menus here): <paramref name="title"/> above <paramref name="text"/>, with one button
	/// (a notice) or two (a question, A and B, their captions <paramref name="buttons"/>[0] and [1];
	/// none keeps the box's own captions); more than two is a menu box (<see cref="menu_box"/>) whose
	/// answer is the button's index, on this box's signal and event. Returns the id <c>message_box_answered(id, button)</c> and
	/// the <c>mod_message_box</c> event carry: 0 for A, 1 for B, -1 when another box replaced it or
	/// the player is played on the other machine. One question and one notice stand at a time.
	/// </summary>
	public long message_box(string title, string text, Godot.Collections.Array<string> buttons, long player)
	{
		long id = NextBoxId();
		void Answered(int button)
		{
			if (IsInstanceValid(this))
			{
				EmitSignal(SignalName.message_box_answered, id, (long)button);
			}
		}

		// More than two buttons is a menu box, answered as this box: the button's index.
		if (buttons.Count > 2)
		{
			Refii.Ui.Engine_UiModBoxes.MenuBox((uint)id, (int)Math.Clamp(player, 0, 2), title, text,
				MenuOptions(buttons), cancelable: true, Answered, asMessageBox: true);
			return id;
		}

		string? Caption(int i) => buttons.Count > i && buttons[i].Length != 0 ? buttons[i] : null;
		Refii.Ui.Engine_UiModBoxes.Raise((uint)id, (int)Math.Clamp(player, 0, 2), title, text,
			Caption(0), Caption(1), buttons.Count >= 2, Answered);
		return id;
	}

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public long message_box(string title, string text, Godot.Collections.Array<string> buttons) => message_box(title, text, buttons, 0);

	/// <summary>The next box id, shared by message_box, notice and menu_box (Godot's half of the id space).</summary>
	private long NextBoxId()
	{
		if (++_nextBoxId > Refii.Ui.Engine_UiModRequest.LastBoxId)
		{
			_nextBoxId = Refii.Ui.Engine_UiModRequest.FirstHostBoxId;
		}

		return _nextBoxId;
	}

	/// <summary>A menu box's options: at most <see cref="Refii.Ui.Engine_UiModRequest.MaxMenuOptions"/>.</summary>
	private static List<string> MenuOptions(Godot.Collections.Array<string> options)
	{
		var list = new List<string>();
		foreach (string option in options)
		{
			if (list.Count == Refii.Ui.Engine_UiModRequest.MaxMenuOptions)
			{
				GD.PushWarning($"[mods] godot: a menu box takes {Refii.Ui.Engine_UiModRequest.MaxMenuOptions} options; the rest are dropped");
				break;
			}

			list.Add(option);
		}

		return list;
	}

	/// <summary>
	/// A mod's text in the culture the game runs in (<see cref="ModText.Text"/>, the lookup Lua's
	/// <c>Refii.text</c> makes): <paramref name="key"/> a bare key (<c>QUICKSAVE_SAVED</c>) or a text with
	/// <c>{KEY}</c>s in it — the running book, the mods' <c>language/&lt;culture&gt;/text/book.babel.csv</c>,
	/// their en-uk rows — then <c>%1</c>, <c>%2</c> … replaced by <paramref name="args"/>. A key nothing
	/// knows is returned as written. Any thread.
	/// </summary>
	public string text(string key, Godot.Collections.Array args)
	{
		var values = new List<string>(args.Count);
		foreach (Variant arg in args)
		{
			values.Add(arg.AsString());
		}

		return ModText.Text(key, values);
	}

	/// <summary>The short form, no arguments: the script bridge passes no C# default.</summary>
	public string text(string key) => ModText.Text(key);

	/// <summary>
	/// Posts a notice for <paramref name="player"/> (1 or 2; 0, the default, for whoever owns the
	/// menus here) on the notice channel the game's own co-op notices use — in game the HUD's info
	/// box for <paramref name="seconds"/> (0: the channel's length), on the front end its dialog until
	/// dismissed: <paramref name="title"/> above <paramref name="text"/>. Notices queue one after
	/// another. Returns the id <c>notice_done(id, outcome)</c> and the <c>mod_notice</c> event carry:
	/// 0 once it has shown and gone, -1 when it never showed (its player is played on the other
	/// machine, which shows it there).
	/// </summary>
	public long notice(string title, string text, double seconds, long player)
	{
		long id = NextBoxId();
		Refii.Ui.Engine_UiModBoxes.Notice((uint)id, (int)Math.Clamp(player, 0, 2), title, text,
			double.IsFinite(seconds) ? (float)Math.Clamp(seconds, 0, 600) : 0f, outcome =>
			{
				if (IsInstanceValid(this))
				{
					EmitSignal(SignalName.notice_done, id, (long)outcome);
				}
			});
		return id;
	}

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public long notice(string title, string text, double seconds) => notice(title, text, seconds, 0);

	/// <summary>The short form, <c>seconds = 0, player = 0</c>: the script bridge passes no C# default.</summary>
	public long notice(string title, string text) => notice(title, text, 0, 0);

	/// <summary>
	/// Puts up a menu box in the game's own menu box art (the Golden Cock's), on the front end and in
	/// game, for <paramref name="player"/> (1 or 2; 0, the default, for whoever owns the menus here):
	/// <paramref name="title"/> above <paramref name="text"/>, then <paramref name="options"/> (up to
	/// 16; more than the box's six rows are paged). B closes a <paramref name="cancelable"/> box.
	/// Returns the id <c>menu_box_answered(id, option)</c> and the <c>mod_menu_box</c> event carry:
	/// the chosen option's index, -1 for B, another box replacing it, or a player played on the
	/// other machine. One menu box stands at a time.
	/// </summary>
	public long menu_box(string title, string text, Godot.Collections.Array<string> options, long player, bool cancelable)
	{
		long id = NextBoxId();
		Refii.Ui.Engine_UiModBoxes.MenuBox((uint)id, (int)Math.Clamp(player, 0, 2), title, text, MenuOptions(options),
			cancelable, option =>
			{
				if (IsInstanceValid(this))
				{
					EmitSignal(SignalName.menu_box_answered, id, (long)option);
				}
			});
		return id;
	}

	/// <summary>The short form, <c>cancelable = true</c>: the script bridge passes no C# default.</summary>
	public long menu_box(string title, string text, Godot.Collections.Array<string> options, long player) => menu_box(title, text, options, player, true);

	/// <summary>The short form, <c>player = 0, cancelable = true</c>: the script bridge passes no C# default.</summary>
	public long menu_box(string title, string text, Godot.Collections.Array<string> options) => menu_box(title, text, options, 0, true);

	/// <summary>The ModNotice event's Godot surface, raised where the notice went (the channel is Godot's).</summary>
	internal void RaiseModNotice(uint id, int outcome, int player)
	{
		var e = new Godot.Collections.Dictionary
		{
			["id"] = (long)id,
			["outcome"] = (long)outcome,
			["player"] = (long)player,
		};
		EmitSignal(SignalName.mod_notice, e);
		EmitSignal(SignalName.mod_event, "mod_notice", e);
		GC.KeepAlive(e);
	}

	/// <summary>The <see cref="setting_changed"/> signal, raised on the main thread whoever changed the value.</summary>
	internal void RaiseSettingChanged(string key, float value) =>
		Callable.From(() => EmitSignal(SignalName.setting_changed, key, value)).CallDeferred();

	/// <summary>The ModMenuBox event's Godot surface, raised where the box was answered.</summary>
	internal void RaiseModMenuBox(uint id, int option, int player)
	{
		var e = new Godot.Collections.Dictionary
		{
			["id"] = (long)id,
			["option"] = (long)option,
			["player"] = (long)player,
		};
		EmitSignal(SignalName.mod_menu_box, e);
		EmitSignal(SignalName.mod_event, "mod_menu_box", e);
		GC.KeepAlive(e);
	}

	/// <summary>
	/// The ModMessageBox event's Godot surface: a box a mod raised (any surface's) was answered —
	/// the <c>mod_message_box</c> signal and <c>mod_event</c>, here as it happens. The guest raises
	/// the same event for native and Lua handlers on its tick and never posts it back here.
	/// </summary>
	internal void RaiseModMessageBox(uint id, int button, int player)
	{
		var e = new Godot.Collections.Dictionary
		{
			["id"] = (long)id,
			["button"] = (long)button,
			["player"] = (long)player,
		};
		EmitSignal(SignalName.mod_message_box, e);
		EmitSignal(SignalName.mod_event, "mod_message_box", e);
		GC.KeepAlive(e);
	}

	/// <summary>
	/// The ModMenuRow event's Godot surface, raised where it happens (the menu is Godot's): the
	/// <c>mod_menu_row</c> signal and <c>mod_event</c>. The guest raises the same event for native
	/// and Lua handlers on its tick and never posts it back here.
	/// </summary>
	/// <summary>
	/// Raises the row event to Godot scripts and reads their verdict back off the same
	/// dictionary: <c>e["cancel"] = true</c> consumes the press (nothing is dispatched);
	/// <c>e["command"]</c> rewritten to another verb runs that instead of the row's own. Native
	/// and Lua handlers see the event a tick later and cannot answer it.
	/// </summary>
	internal (bool Cancelled, string Command) RaiseModMenuRow(string menu, string row, int player, string mod, string command = "", string button = "")
	{
		ModRowSubject.Subject subject = ModRowSubject.Of(row, command);
		var e = new Godot.Collections.Dictionary
		{
			["menu"] = menu,
			["row"] = row,
			["player"] = (long)player,
			["mod"] = mod,
			["command"] = command ?? string.Empty,
			["button"] = button ?? string.Empty,
			["subject_kind"] = subject.Kind,
			["definition"] = subject.Definition,
			["spell"] = (long)subject.Spell,
		};
		EmitSignal(SignalName.mod_menu_row, e);
		EmitSignal(SignalName.mod_event, "mod_menu_row", e);
		bool cancelled = e.TryGetValue("cancel", out Variant cancel) &&
			cancel.VariantType == Variant.Type.Bool && cancel.AsBool();
		string after = e.TryGetValue("command", out Variant rewritten) && rewritten.VariantType == Variant.Type.String
			? rewritten.AsString()
			: command ?? string.Empty;
		return (cancelled, after);
	}

	/// <summary>
	/// Raises <c>menu_screen</c> for a screen Godot owns outright (achievements, multiplayer, a
	/// mod's screens record): the game never states these, so the event comes from here, with
	/// <c>host = true</c> and <c>open</c> saying which edge. Godot scripts only.
	/// </summary>
	internal void RaiseHostScreen(uint screen, int player, bool open)
	{
		var e = new Godot.Collections.Dictionary
		{
			["screen"] = (long)screen,
			["player"] = (long)player,
			["host"] = true,
			["open"] = open,
		};
		EmitSignal(SignalName.menu_screen, e);
		EmitSignal(SignalName.mod_event, "menu_screen", e);
		GC.KeepAlive(e);
	}

	// ---- effects: Godot draws them (Engine_FxPlayer) --------------------------------------------

	/// <summary>
	/// Plays effect <paramref name="name"/> (the particle bank's or a mod's fx/effects/, by name or
	/// FX_XXXXXXXX token) at a world position in the guest's axes, as the events state positions.
	/// <paramref name="options"/>: duration (seconds, 0 until stop_fx), scale, tint (Color or
	/// [r, g, b]), intensity, loop (true; false plays it once). Returns the id stop_fx takes, or 0
	/// for an unknown effect.
	/// </summary>
	public long play_fx(string name, float x, float y, float z, Godot.Collections.Dictionary options) =>
		PlayFx(name, new Vector3(x, y, z), 0, options);

	/// <summary>
	/// Plays effect <paramref name="name"/> on an entity by its HANDLE: it stands in the entity's own
	/// frame, options.offset (Vector3 or [x, y, z]) along the entity's axes, and moves and turns with
	/// it; the other options as play_fx. 0 for an unknown effect or handle.
	/// </summary>
	public long play_fx_on(string name, long handle, Godot.Collections.Dictionary options) =>
		handle <= 0 || handle > uint.MaxValue ? 0 : PlayFx(name, Vector3.Zero, (uint)handle, options);

	/// <summary>Stops a played effect by any surface's id (native, Lua or Godot); false when none plays under it.</summary>
	public bool stop_fx(long id) => id > 0 && id <= uint.MaxValue && Refii.Fx.Engine_FxPlayer.Stop((uint)id);

	// ---- lights: Godot draws them, the classic list takes them (Engine_ModLights) ----------------

	/// <summary>
	/// Places a mod light over <paramref name="target"/> — an entity HANDLE (int), or a Vector3 /
	/// [x, y, z] world point in the guest's axes — in <paramref name="color"/> (Color or [r, g, b])
	/// at <paramref name="energy"/> over <paramref name="range"/> metres. <paramref name="options"/>:
	/// offset (Vector3 or [x, y, z], along the entity's axes), at (a model helper name: the light
	/// stands at that .mdl.gmd point, on a rigged body on its bone — as play_fx_on's at), flicker
	/// (0..1, a waver of the energy); and for a SPOT: direction (Vector3 or [x, y, z], the offset's
	/// axes — the entity's or the helper's, the world's for a point), cone (degrees from the axis to
	/// the cone's edge, above 0 makes it a spot; at most 89) and attenuation (the edge falloff; 0 or
	/// 1 linear, the title's own). Placed as play_fx_on places an effect, so it rides the body
	/// and goes with the entity. Returns the id <see cref="light_remove"/> takes; 0 for an unknown
	/// handle or target.
	/// </summary>
	public long light(Variant target, Variant color, double energy, double range, Godot.Collections.Dictionary? options)
	{
		uint handle = 0;
		var point = Vector3.Zero;
		switch (target.VariantType)
		{
			case Variant.Type.Int:
			case Variant.Type.Float:
			{
				double raw = target.AsDouble();
				if (raw is <= 0 or > uint.MaxValue)
				{
					return 0;
				}

				handle = (uint)raw;
				break;
			}

			case Variant.Type.Vector3:
				point = target.AsVector3();
				break;

			case Variant.Type.Array when target.AsGodotArray() is { Count: >= 3 } a:
				point = new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
				break;

			default:
				return 0;
		}

		Vector3 rgb = color.VariantType switch
		{
			Variant.Type.Color => new Vector3(color.AsColor().R, color.AsColor().G, color.AsColor().B),
			Variant.Type.Vector3 => color.AsVector3(),
			Variant.Type.Array when color.AsGodotArray() is { Count: >= 3 } c =>
				new Vector3((float)c[0].AsDouble(), (float)c[1].AsDouble(), (float)c[2].AsDouble()),
			_ => Vector3.One,
		};
		var offset = Vector3.Zero;
		var direction = Vector3.Zero;
		string at = string.Empty;
		float flicker = 0f, cone = 0f, attenuation = 0f;
		if (options is not null)
		{
			static Vector3 Triple(Variant v) => v.VariantType == Variant.Type.Vector3
				? v.AsVector3()
				: v.VariantType == Variant.Type.Array && v.AsGodotArray() is { Count: >= 3 } arr
					? new Vector3((float)arr[0].AsDouble(), (float)arr[1].AsDouble(), (float)arr[2].AsDouble())
					: Vector3.Zero;
			static float Number(Variant v) => v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : 0f;

			if (options.TryGetValue("offset", out Variant o))
			{
				offset = Triple(o);
			}

			if (options.TryGetValue("at", out Variant helper) && helper.VariantType == Variant.Type.String)
			{
				at = helper.AsString();
			}

			if (options.TryGetValue("flicker", out Variant waver))
			{
				flicker = Number(waver);
			}

			if (options.TryGetValue("direction", out Variant d))
			{
				direction = Triple(d);
			}

			if (options.TryGetValue("cone", out Variant c))
			{
				cone = Number(c);
			}

			if (options.TryGetValue("attenuation", out Variant a))
			{
				attenuation = Number(a);
			}
		}

		return Refii.Lighting.Engine_ModLights.Place(
			handle, point, new Color(rgb.X, rgb.Y, rgb.Z), (float)energy, (float)range, offset, at, flicker, direction, cone, attenuation);
	}

	/// <summary>The short form, <c>options = null</c>: the script bridge passes no C# default.</summary>
	public long light(Variant target, Variant color, double energy, double range) => light(target, color, energy, range, null);

	/// <summary>Removes a mod light by any surface's id (native, Lua or Godot); false when none stands under it.</summary>
	public bool light_remove(long id) => id > 0 && id <= uint.MaxValue && Refii.Lighting.Engine_ModLights.Remove((uint)id);

	/// <summary>
	/// Sends <paramref name="mod"/>'s message <paramref name="name"/> with <paramref name="payload"/>
	/// to THE OTHER MACHINE of an online session (refii_mod_send_to_partner, run on the guest tick
	/// through <c>partner_send</c>): it arrives there as the <c>partner_message</c> event — on every
	/// surface, Godot's signal included — once and in the order sent, with <c>from</c> the player this
	/// machine plays. <paramref name="name"/> and <paramref name="mod"/> are words (letters, digits,
	/// '_', '.', '-'; at most 32), the payload any text up to 256 bytes. False, and nothing sent,
	/// outside an online session (alone or in local co-op there is no partner machine), for a bad
	/// name, or a payload over the limit.
	/// </summary>
	public bool send_to_partner(string mod, string name, string payload)
	{
		if (_coopMode < 2 || string.IsNullOrEmpty(mod) || string.IsNullOrEmpty(name) ||
			mod.Length > 32 || name.Length > 32 || mod.Contains(' ') || name.Contains(' '))
		{
			return false;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
		if (bytes.Length > 256)
		{
			return false;
		}

		return Post($"partner_send {mod} {name} {Convert.ToBase64String(bytes)}");
	}

	/// <summary>The short form, <c>payload = ""</c>: the script bridge passes no C# default.</summary>
	public bool send_to_partner(string mod, string name) => send_to_partner(mod, name, "");

	/// <summary>
	/// The mod lights standing: id, entity (handle, 0 for a point), r, g, b, energy, range, x, y, z
	/// (where it is now, guest axes), cone (0 for an omni light) and, for a spot, dx, dy, dz (the
	/// direction it shines along now, guest world axes).
	/// </summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> lights()
	{
		var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
		foreach ((uint id, uint entity, Color colour, float energy, float range, Vector3 where, float cone, Vector3 direction) in Refii.Lighting.Engine_ModLights.All())
		{
			list.Add(new Godot.Collections.Dictionary
			{
				["id"] = (long)id,
				["entity"] = (long)entity,
				["cone"] = cone,
				["dx"] = direction.X, ["dy"] = direction.Y, ["dz"] = direction.Z,
				["r"] = colour.R, ["g"] = colour.G, ["b"] = colour.B,
				["energy"] = energy,
				["range"] = range,
				["x"] = where.X, ["y"] = where.Y, ["z"] = where.Z,
			});
		}

		return list;
	}

	// ---- the HUD: markers, fades and meters (Engine_HudModMarkers, Engine_UiModFade, Engine_HudModMeters) ----

	/// <summary>
	/// Puts a marker on the HUD's world-icon channel over <paramref name="target"/> — an entity
	/// HANDLE (int), or a Vector3 / [x, y, z] world point in the guest's axes — for
	/// <paramref name="player"/> (1 or 2; 0, the default, every player this machine plays):
	/// <paramref name="icon"/> a game texture path ("art/gui/…/x.tex") or an enabled mod's PNG
	/// ("art/x.png"), <paramref name="text"/> a line under it. It rides the drawn body, is placed
	/// as the game's own icons are (each player's camera and half) and is sized by the game's
	/// distance curve. Returns the id <see cref="unmark"/> takes; 0 for no icon and no text, or an
	/// unknown handle. A marker on an entity goes with the entity.
	/// </summary>
	public long mark(Variant target, string icon, string text, long player)
	{
		uint handle = 0;
		var point = Vector3.Zero;
		switch (target.VariantType)
		{
			case Variant.Type.Int:
			case Variant.Type.Float:
			{
				double raw = target.AsDouble();
				if (raw is <= 0 or > uint.MaxValue)
				{
					return 0;
				}

				handle = (uint)raw;
				break;
			}

			case Variant.Type.Vector3:
				point = target.AsVector3();
				break;

			case Variant.Type.Array when target.AsGodotArray() is { Count: >= 3 } a:
				point = new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
				break;

			default:
				return 0;
		}

		return Refii.Ui.Engine_HudModMarkers.Add((int)Math.Clamp(player, 0, 2), handle, point, icon ?? string.Empty, text ?? string.Empty);
	}

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public long mark(Variant target, string icon, string text) => mark(target, icon, text, 0);

	/// <summary>The short form, <c>text = "", player = 0</c>: the script bridge passes no C# default.</summary>
	public long mark(Variant target, string icon) => mark(target, icon, "", 0);

	/// <summary>Removes a marker by any surface's id; false when none stands under it.</summary>
	public bool unmark(long id) => id > 0 && id <= uint.MaxValue && Refii.Ui.Engine_HudModMarkers.Remove((uint)id);

	/// <summary>
	/// <see cref="mark(Variant, string, string, long)"/> with options: <c>{ "meter": { "value": v,
	/// "max": m, "style": s } }</c> puts a fill on the new marker (<see cref="mark_meter"/>).
	/// </summary>
	public long mark(Variant target, string icon, string text, long player, Godot.Collections.Dictionary options)
	{
		long id = mark(target, icon, text, player);
		if (id != 0 && options is not null && options.TryGetValue("meter", out Variant meter) &&
			meter.VariantType == Variant.Type.Dictionary)
		{
			var m = meter.AsGodotDictionary();
			mark_meter(id, m.TryGetValue("value", out Variant v) ? v.AsDouble() : 0,
				m.TryGetValue("max", out Variant x) ? x.AsDouble() : 0,
				m.TryGetValue("style", out Variant st) ? st.AsString() : string.Empty);
		}

		return id;
	}

	/// <summary>
	/// A WORLD METER over <paramref name="target"/> (a handle, or a Vector3 / [x, y, z] guest point)
	/// for <paramref name="player"/> (1 or 2; 0 every player this machine plays): a bar filled
	/// <paramref name="value"/> of <paramref name="max"/> on the marker channel, placed, sized and
	/// gone exactly as a marker. <paramref name="style"/> "bar" ("": the HUD's gold), "health" (red)
	/// or an HTML colour; "numbers" after it draws "value / max". A drop leaves a pale trail that
	/// holds, then drains. Returns the id <see cref="mark_meter"/> restates and <see cref="unmark"/>
	/// removes; 0 for no max or an unknown handle.
	/// </summary>
	public long world_meter(Variant target, double value, double max, string style, long player)
	{
		uint handle = 0;
		var point = Vector3.Zero;
		switch (target.VariantType)
		{
			case Variant.Type.Int:
			case Variant.Type.Float:
			{
				double raw = target.AsDouble();
				if (raw is <= 0 or > uint.MaxValue)
				{
					return 0;
				}

				handle = (uint)raw;
				break;
			}

			case Variant.Type.Vector3:
				point = target.AsVector3();
				break;

			case Variant.Type.Array when target.AsGodotArray() is { Count: >= 3 } a:
				point = new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
				break;

			default:
				return 0;
		}

		return Refii.Ui.Engine_HudModMarkers.AddMeter((int)Math.Clamp(player, 0, 2), handle, point, (float)value, (float)max,
			style ?? string.Empty);
	}

	/// <summary>The short form, <c>player = 0</c>: the script bridge passes no C# default.</summary>
	public long world_meter(Variant target, double value, double max, string style) => world_meter(target, value, max, style, 0);

	/// <summary>The short form, <c>style = "", player = 0</c>.</summary>
	public long world_meter(Variant target, double value, double max) => world_meter(target, value, max, string.Empty, 0);

	/// <summary>
	/// Puts a fill on marker <paramref name="id"/> (any surface's) or restates it in place: the value
	/// changes without re-marking; <paramref name="max"/> 0 takes the fill off (a world meter with
	/// nothing else goes). False when nothing stands under the id.
	/// </summary>
	public bool mark_meter(long id, double value, double max, string style) =>
		id > 0 && id <= uint.MaxValue &&
		Refii.Ui.Engine_HudModMarkers.SetMeter((uint)id, (float)value, (float)max, style ?? string.Empty, Refii.Ui.Engine_HudModMarkers.Clock);

	/// <summary>The short form, <c>style = ""</c>.</summary>
	public bool mark_meter(long id, double value, double max) => mark_meter(id, value, max, string.Empty);

	/// <summary>The markers standing: id, player, entity (handle, 0 for a point), x, y, z, icon, text.</summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> markers()
	{
		var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
		foreach (Refii.Ui.Engine_HudModMarkers.Marker m in Refii.Ui.Engine_HudModMarkers.All)
		{
			list.Add(new Godot.Collections.Dictionary
			{
				["id"] = (long)m.Id,
				["player"] = (long)m.Player,
				["entity"] = (long)m.Handle,
				["x"] = m.Point.X, ["y"] = m.Point.Y, ["z"] = m.Point.Z,
				["icon"] = m.Icon,
				["text"] = m.Text,
				["meter_value"] = m.Meter?.Value ?? 0f,
				["meter_max"] = m.Meter?.Max ?? 0f,
			});
		}

		return list;
	}

	/// <summary>
	/// Fades <paramref name="player"/>'s screen (1 or 2; 0 every player this machine plays) to
	/// black (<paramref name="to_black"/> true) or back over <paramref name="seconds"/> (0: at
	/// once): a host-drawn layer over that player's half (their whole window alone or online),
	/// independent of the game's own fade, which level changes own. False for a player not played here.
	/// </summary>
	public bool fade(long player, bool to_black, double seconds)
	{
		int who = (int)Math.Clamp(player, 0, 2);
		return Refii.Ui.Engine_UiModBoxes.PlayedHere(who) &&
			Refii.Ui.Engine_UiModFade.Request(who, to_black, double.IsFinite(seconds) ? (float)seconds : 0f);
	}

	/// <summary>The short form, <c>seconds = 0</c>: the script bridge passes no C# default.</summary>
	public bool fade(long player, bool to_black) => fade(player, to_black, 0);

	/// <summary>A player's mod fade as it stands: alpha (0 clear .. 1 black), target, seconds of the last request.</summary>
	public Godot.Collections.Dictionary fade_state(long player)
	{
		(float alpha, float target, float seconds) = Refii.Ui.Engine_UiModFade.Describe((int)Math.Clamp(player, 0, 2));
		return new Godot.Collections.Dictionary { ["alpha"] = alpha, ["target"] = target, ["seconds"] = seconds };
	}

	/// <summary>
	/// Shows or restates the HUD meter <paramref name="id"/> for <paramref name="player"/> (1 or
	/// 2; 0, the default, every player this machine plays): <paramref name="label"/> above a bar at
	/// <paramref name="value"/> of <paramref name="max"/>, in <paramref name="style"/> "bar" (the
	/// default), "boss" (wide, red, centred) or "timer" (narrow). Meters stack under the top of the
	/// player's screen in the order first shown; the same id restates it in place. False for no id.
	/// </summary>
	public bool meter(string id, string label, double value, double max, long player, string style) =>
		Refii.Ui.Engine_HudModMeters.Set(id, (int)Math.Clamp(player, 0, 2), label ?? string.Empty, (float)value, (float)max, style ?? string.Empty);

	/// <summary>The short form, <c>style = ""</c>: the script bridge passes no C# default.</summary>
	public bool meter(string id, string label, double value, double max, long player) => meter(id, label, value, max, player, "");

	/// <summary>The short form, <c>player = 0, style = ""</c>: the script bridge passes no C# default.</summary>
	public bool meter(string id, string label, double value, double max) => meter(id, label, value, max, 0, "");

	/// <summary>Removes the meter from every player's HUD; false when none stands under the id.</summary>
	public bool meter_remove(string id) => Refii.Ui.Engine_HudModMeters.Remove(id ?? string.Empty);

	// ---- input claims and HUD overrides: the pad publish and the HUD feed read them (ModInput, Engine_UiHudOverrides) ----

	/// <summary>
	/// Claims (or releases) a mod's input action — its scoped name <c>&lt;mod&gt;.&lt;action&gt;</c> from
	/// <c>input.json</c> — for <paramref name="player"/> (1 or 2; 0 both): while claimed, the action's
	/// pad buttons, triggers and keys are withheld from the pad the guest sees for that player, and a
	/// chord's modifier with them while the chord is held; the action's own <c>input_action</c> events
	/// still fire. Cleared when the mods' Lua reloads and when the game goes down. False for an
	/// action no enabled mod declares.
	/// </summary>
	public bool claim_input(long player, string action, bool on) =>
		player is >= 0 and <= 2 && ModInput.Claim((int)player, action ?? string.Empty, on);

	/// <summary>The short form, <c>on = true</c>: the script bridge passes no C# default.</summary>
	public bool claim_input(long player, string action) => claim_input(player, action, true);

	/// <summary>Whether <paramref name="action"/> is claimed for <paramref name="player"/> (1 or 2).</summary>
	public bool input_claimed(long player, string action) =>
		player is 1 or 2 && ModInput.Claimed((int)player, action ?? string.Empty);

	/// <summary>The actions claimed for <paramref name="player"/> (1 or 2), in declaration order.</summary>
	public Godot.Collections.Array<string> input_claims(long player)
	{
		var list = new Godot.Collections.Array<string>();
		if (player is 1 or 2)
		{
			foreach (string name in ModInput.Claims((int)player))
			{
				list.Add(name);
			}
		}

		return list;
	}

	/// <summary>
	/// Holds <paramref name="button"/> down on <paramref name="player"/>'s pad (1 or 2), or releases it:
	/// input.json's names (A B X Y LB RB L3 R3 Start Back DpadUp …), LT / RT the triggers.
	/// <paramref name="held"/> false / 0 releases; a trigger is pulled by it, true / 1 fully, 2..255
	/// that much. The guest reads it as that player's own pad (ModInputPresses). False for an unknown
	/// button or player.
	/// </summary>
	public bool press(long player, string button, long held) =>
		player is 1 or 2 && ModInputPresses.Press((int)player, button ?? string.Empty, held);

	/// <summary>Presses <paramref name="button"/> on <paramref name="player"/>'s pad for at least <paramref name="ticks"/> guest ticks (1..600).</summary>
	public bool tap(long player, string button, long ticks) =>
		player is 1 or 2 && ModInputPresses.Tap((int)player, button ?? string.Empty, ticks);

	/// <summary>
	/// A touchscreen AREA: a finger landing on <paramref name="rect"/> (the root viewport's coordinates,
	/// where a script's CanvasLayer draws) presses <paramref name="action"/> (scoped,
	/// <c>&lt;mod&gt;.&lt;action&gt;</c>) for player one, held while the finger stays; an empty
	/// rectangle removes it. Stands until removed or the mod set reloads. False for an action no
	/// enabled mod declares (<see cref="ModInputTouch"/>).
	/// </summary>
	public bool touch_area(string action, Rect2 rect) => ModInputTouch.SetArea(action ?? string.Empty, rect);

	/// <summary>Whether the touchscreen is the device in use now: the touch controls are drawn and a tap goes to them.</summary>
	public bool touch_mode() => Refii.Ui.Engine_UiInputMode.Touch;

	/// <summary>The touch controls' move stick: x right and y up, each -1..1; zero with no thumb on it (or no touchscreen).</summary>
	public Vector2 touch_stick() => ModInputTouch.Stick();

	/// <summary>
	/// <paramref name="player"/>'s (1 or 2) move stick as the game is handed it: x right and y up, each -1..1.
	/// Player 1's folds the keyboard's movement keys, the touch controls' stick and their joypad; player 2's
	/// is their own joypad (the split the <c>input_action</c> events and <see cref="claim_input"/> use). Zero for a
	/// player with no pad this frame.
	/// </summary>
	public Vector2 move_stick(long player)
	{
		(float x, float y) = ModInputSticks.Move((int)Math.Clamp(player, 0, 3), Engine.GetProcessFrames());
		return new Vector2(x, y);
	}

	/// <summary>The buttons held for <paramref name="player"/> (1 or 2) by any surface's press, by name.</summary>
	public Godot.Collections.Array<string> presses(long player)
	{
		var list = new Godot.Collections.Array<string>();
		if (player is 1 or 2)
		{
			foreach (string name in ModInputPresses.For((int)player).Held)
			{
				list.Add(name);
			}
		}

		return list;
	}

	/// <summary>The player a player argument names: 1 or 2 as given, 0 the player whose menus are open on this machine; 0 for anything else.</summary>
	private int Named(long player) => player is 1 or 2 ? (int)player : player == 0 ? _menuPlayer : 0;

	/// <summary>
	/// The current region's map, as the game's own minimaps plot it, for <paramref name="player"/>'s
	/// hero (1 or 2; 0 this machine's, <see cref="local_hero"/>): <c>known</c>, <c>proper</c> (it has a real
	/// map, so the places below are stated), <c>texture</c> (Godot only) and <c>texture_path</c> (its
	/// <c>game://</c> path), <c>north</c> (radians the image turns), <c>half_size</c> (the image's half-size,
	/// from the pause root's file), <c>hero</c> and <c>facing</c>, and <c>quests</c> (an Array of places: the
	/// other quests' objectives, from the quest log of the hero whose menus this machine shows, whichever
	/// player is named). The extent is the level's own heightfields', the places the entity feed's. Places are
	/// element units around the image's centre, Y up.
	/// </summary>
	public Godot.Collections.Dictionary minimap(long player)
	{
		long handle = player is 1 or 2 ? hero((int)player) : player == 0 ? local_hero() : 0;
		uint address = handle > 0 && handle <= uint.MaxValue ? HostBridge.ModHandleAddress((uint)handle) : 0u;
		return Refii.Core.Engine_TitleGame.Active.ModMinimap(address);
	}

	/// <summary>
	/// What <paramref name="player"/>'s hero carries (1 or 2; 0 the player whose menus are open here), one
	/// Dictionary per stack in the game's order, as the game's own inventory pages show it:
	/// <c>definition</c>, <c>count</c>, <c>category</c>, <c>name</c>, <c>description</c>, <c>icon</c>,
	/// <c>model</c>, <c>pieces</c> (an outfit's worn models), <c>value</c>, <c>rating</c>, <c>usable</c>,
	/// <c>worn</c>, <c>weapon_class</c> (1 melee, 2 ranged, 0 neither), <c>new</c> and <c>lines</c> (the item
	/// page's stat lines, each <c>label</c> and <c>value</c>). Either player's, whoever's menus are open: the
	/// hero's own inventory as this machine's guest holds it.
	/// </summary>
	public Godot.Collections.Array inventory_items(long player) =>
		Named(player) is var named && named != 0
			? Refii.Core.Engine_TitleGame.Active.ModInventoryItems(named)
			: new Godot.Collections.Array();

	/// <summary>
	/// Does to an item what the game's own inventory pages do, for <paramref name="player"/>'s hero (as
	/// <see cref="inventory_items"/>): <c>use</c>, <c>equip</c>, <c>wear</c>, <c>outfit</c>, or <c>remove</c>
	/// (empties that item's clothing slot; give the item's definition). The player whose menus are open here
	/// acts through the pages' own action; the other player acts on their own hero directly, in local co-op
	/// (<see cref="coop_mode"/> 1), where both heroes are this machine's. False for an operation that is none
	/// of them, a definition that does not parse or the hero does not carry, or another machine's player.
	/// </summary>
	public bool inventory_action(long player, string operation, Variant definition)
	{
		string named = definition.AsString().Trim();
		bool hex = named.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
		if (!uint.TryParse(hex ? named[2..] : named, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
				CultureInfo.InvariantCulture, out uint record) || record == 0u)
		{
			return false;
		}

		string spelled = "0x" + record.ToString("X8", CultureInfo.InvariantCulture);
		int category = -1;
		bool carried = false;
		foreach (Variant item in inventory_items(player))
		{
			Godot.Collections.Dictionary stack = item.AsGodotDictionary();
			if (stack["definition"].AsString() == spelled)
			{
				category = stack["category"].AsInt32();
				carried = true;
				break;
			}
		}

		int acting = Named(player);
		int addressed = acting == _menuPlayer ? 0 : acting;
		if (!carried || (addressed != 0 && _coopMode != 1))
		{
			return false;
		}

		return Refii.Core.Engine_TitleGame.Active.ModInventoryAction(operation ?? string.Empty, record, category, addressed) is { } action &&
			HostBridge.PostFrontEndAction(action) != 0;
	}

	/// <summary>
	/// Dresses a model shown in a script's own world (a <c>SubViewport</c> with <c>own_world_3d</c>):
	/// each surface takes the lit material the game's item previews use, carrying its own textures. The
	/// world's shaders read the level's lighting and draw nothing without it. Gives
	/// <paramref name="node"/> <paramref name="mesh"/> as well.
	/// </summary>
	public void dress_model(MeshInstance3D node, Mesh mesh)
	{
		node.Mesh = mesh;
		Refii.Ui.Engine_UiStageMaterial.Dress(node, mesh);
	}

	/// <summary>
	/// How the item page holds an item of <see cref="inventory_items"/> after this many radians of its
	/// spin: at 0 its front faces a camera looking down -Z. An item that states no axis turns about the vertical.
	/// </summary>
	public Basis item_pose(Godot.Collections.Dictionary item, float yaw)
	{
		int axis = item.TryGetValue("rotation_axis", out Variant stated) ? stated.AsInt32() : 0;
		Vector3 rotation = item.TryGetValue("rotation", out Variant offset) ? offset.AsVector3() : Vector3.Zero;
		return Refii.Ui.Engine_UiPreviewObject.Stated(yaw, rotation, axis is >= 1 and <= 6 ? axis : 3);
	}

	/// <summary>
	/// A mod's screen stands where the pause book would, for <paramref name="player"/> (1 or 2; 0 the player
	/// whose menus are open here): while on, the book draws nothing and the pad is held from the game, so a
	/// script's own screen has the mouse and the keys. It does not pause by itself: press Start for that
	/// player to enter and leave the pause (<see cref="press"/>), and turn this off when the
	/// <see cref="paused"/> signal says the pause has ended. <paramref name="script"/> is the calling script's
	/// node (<c>self</c>): the screen is that MOD's, so only it turns it off. One screen stands at a time (a
	/// later call, by any mod, takes it over); the engine releases it when the mod set reloads or a mod is
	/// enabled or disabled, the game is left, or a level unloads (<see cref="ModScreen"/>).
	/// </summary>
	public void own_screen(long player, bool on, Node script) => OwnScreen(player, on, ModOf(script), fromLua: false);

	/// <summary>The short form, with no script named: the screen is the mod set's, owned by no one mod.</summary>
	public void own_screen(long player, bool on) => OwnScreen(player, on, string.Empty, fromLua: false);

	private void OwnScreen(long player, bool on, string mod, bool fromLua)
	{
		int named = Named(player);
		if (named == 0)
		{
			return;
		}

		if (on)
		{
			ModScreen.Take(this, mod, named, fromLua);
		}
		else
		{
			ModScreen.Release(this, mod, named);
		}
	}

	/// <summary>
	/// The mod a script node belongs to: the <c>refii_mod</c> meta <see cref="ModGodot"/> stamps on each script it
	/// runs, read off the node or the nearest ancestor that carries it; empty for a node of no mod's.
	/// </summary>
	private static string ModOf(Node? node)
	{
		for (Node? at = node; at is not null && IsInstanceValid(at); at = at.GetParent())
		{
			if (at.HasMeta(ModGodot.ModMeta))
			{
				return at.GetMeta(ModGodot.ModMeta).AsString();
			}
		}

		return string.Empty;
	}

	/// <summary>The scoped mod action <paramref name="name"/> names (<c>&lt;mod&gt;.&lt;action&gt;</c>), or null.</summary>
	private static ModInput.Action? ModAction(string name)
	{
		foreach (ModInput.Action action in ModInput.All)
		{
			if (string.Equals(action.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return action;
			}
		}

		return null;
	}

	/// <summary>What is bound now to a mod action or a game pad button: (key name, pad button name), either empty when unbound.</summary>
	private static (string Key, string Pad) Bound(string name)
	{
		name = (name ?? string.Empty).Trim();
		if (ModAction(name) is { } action)
		{
			IReadOnlyList<Key> keys = ModInput.EffectiveKeys(action);
			(IReadOnlyList<JoyButton> buttons, IReadOnlyList<JoyAxis> triggers) = ModInput.EffectivePad(action);
			string key = keys.Count != 0 ? OS.GetKeycodeString(keys[0]) : string.Empty;
			string pad = buttons.Count != 0 ? ModInput.PadName(buttons[0]) : triggers.Count != 0 ? ModInput.TriggerName(triggers[0]) : string.Empty;
			return (key, pad);
		}

		string button = name.Length == 0 ? string.Empty
			: ModInput.TryButton(name, out JoyButton joy) ? ModInput.PadName(joy)
			: ModInput.TryTrigger(name, out JoyAxis trigger) ? ModInput.TriggerName(trigger)
			: string.Empty;
		return button.Length != 0
			? (Refii.Ui.Engine_UiKeyGlyphs.LabelFor?.Invoke(button) ?? button, button)
			: (string.Empty, string.Empty);
	}

	/// <summary>
	/// The prompt glyph for <paramref name="name"/> — a mod action scoped <c>&lt;mod&gt;.&lt;action&gt;</c>
	/// (input.json), or a game pad button (<c>A</c>, <c>B</c>, <c>Start</c>, <c>LT</c> …) — as the game's own
	/// prompts draw it now: the bound key's keycap on the keyboard and mouse, the bound button in the
	/// player's glyph style and pack on a pad. A rebind shows at once. Null with nothing bound on the device
	/// in use. Godot only: Lua reads <see cref="input_label"/>.
	/// </summary>
	public Texture2D? input_glyph(string name)
	{
		(string key, string pad) = Bound(name);
		return Refii.Ui.Engine_UiKeyGlyphs.ActionGlyph(key, pad);
	}

	/// <summary>The name of what is bound to <paramref name="name"/> (as <see cref="input_glyph"/>) on the device in use now; empty when unbound.</summary>
	public string input_label(string name)
	{
		(string key, string pad) = Bound(name);
		return Refii.Ui.Engine_UiInputMode.Keycaps ? key : pad;
	}

	/// <summary>A guest world position on <see cref="minimap"/>'s map, in its units; a Vector2, or null with no proper map.</summary>
	public Variant minimap_point(double x, double y, double z) =>
		Refii.Core.Engine_TitleGame.Active.ModMinimapPoint(new Vector3((float)x, (float)y, (float)z)) is { } at
			? Variant.From(at)
			: default;

	/// <summary>
	/// States the texture a HUD element shows for <paramref name="player"/> (1 or 2; 0 both):
	/// <paramref name="element"/> a slide-toggle binding name (<c>DPadUp</c>) or an element path by its
	/// trailing segments; <paramref name="texture"/> a game texture path or an enabled mod's PNG under
	/// <c>art/</c>, fitted to the slot when it is not 128 × 128; "" clears. False for no element.
	/// </summary>
	public bool hud_icon(long player, string element, string texture) =>
		player is >= 0 and <= 2 && Refii.Ui.Engine_UiHudOverrides.SetIcon((int)player, element ?? string.Empty, texture ?? string.Empty);

	/// <summary>States the slide a HUD element sits on, as the game counts them (1 the first); 0 clears. False for no element.</summary>
	public bool hud_slide(long player, string element, long slide) =>
		player is >= 0 and <= 2 && slide is >= 0 and <= 65535 &&
		Refii.Ui.Engine_UiHudOverrides.SetSlide((int)player, element ?? string.Empty, (int)slide);

	/// <summary>Holds a HUD piece visible (its owner chain kept on its shown slides) for a player, or lets it go. False for no name.</summary>
	public bool hud_hold(long player, string piece, bool on) =>
		player is >= 0 and <= 2 && Refii.Ui.Engine_UiHudOverrides.SetHold((int)player, piece ?? string.Empty, on);

	/// <summary>The short form, <c>on = true</c>: the script bridge passes no C# default.</summary>
	public bool hud_hold(long player, string piece) => hud_hold(player, piece, true);

	/// <summary>The overrides standing for <paramref name="player"/>: <c>icons</c> (element → texture), <c>slides</c> (element → slide), <c>holds</c> (pieces).</summary>
	public Godot.Collections.Dictionary hud_overrides(long player)
	{
		var icons = new Godot.Collections.Dictionary();
		var slides = new Godot.Collections.Dictionary();
		var holds = new Godot.Collections.Array<string>();
		if (player is 1 or 2)
		{
			foreach ((string element, string texture) in Refii.Ui.Engine_UiHudOverrides.IconsFor((int)player))
			{
				icons[element] = texture;
			}

			foreach ((string element, int slide) in Refii.Ui.Engine_UiHudOverrides.SlidesFor((int)player))
			{
				slides[element] = (long)slide;
			}

			foreach (string piece in Refii.Ui.Engine_UiHudOverrides.HoldsFor((int)player))
			{
				holds.Add(piece);
			}
		}

		return new Godot.Collections.Dictionary { ["icons"] = icons, ["slides"] = slides, ["holds"] = holds };
	}

	/// <summary>The meters standing, in stack order: id, player, label, value, max, style.</summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> meters()
	{
		var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
		foreach (Refii.Ui.Engine_HudModMeters.Meter m in Refii.Ui.Engine_HudModMeters.All())
		{
			list.Add(new Godot.Collections.Dictionary
			{
				["id"] = m.Id,
				["player"] = (long)m.Player,
				["label"] = m.Label,
				["value"] = m.Value,
				["max"] = m.Max,
				["style"] = m.Style,
			});
		}

		return list;
	}

	private static long PlayFx(string name, Vector3 at, uint entity, Godot.Collections.Dictionary options)
	{
		float Number(string key, float fallback) =>
			options.TryGetValue(key, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : fallback;
		Vector3 Triple(string key, Vector3 fallback)
		{
			if (!options.TryGetValue(key, out Variant v))
			{
				return fallback;
			}

			if (v.VariantType == Variant.Type.Vector3)
			{
				return v.AsVector3();
			}

			if (v.VariantType == Variant.Type.Color)
			{
				Color c = v.AsColor();
				return new Vector3(c.R, c.G, c.B);
			}

			if (v.VariantType == Variant.Type.Array && v.AsGodotArray() is { Count: >= 3 } a)
			{
				return new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
			}

			return fallback;
		}

		Vector3 tint = Triple("tint", Vector3.One);
		bool loop = !options.TryGetValue("loop", out Variant loopValue) || loopValue.AsBool();
		return Refii.Fx.Engine_FxPlayer.Play(
			name, at, entity, Number("duration", 0f), Number("scale", 1f), new Color(tint.X, tint.Y, tint.Z),
			Number("intensity", 1f), loop, Triple("offset", Vector3.Zero),
			options.TryGetValue("at", out Variant helper) && helper.VariantType == Variant.Type.String ? helper.AsString() : string.Empty);
	}

	// ---- the field text --------------------------------------------------------------------

	/// <summary>
	/// The bus's Lua-table-literal body as a Dictionary: <c>key=value</c> pairs separated by
	/// commas; a value is a number (int when it has no point or exponent), <c>true</c>/<c>false</c>,
	/// or a 'single-quoted' string with no escapes (the bus replaces a quote inside with '_').
	/// Text that does not fit is kept as a string rather than dropped.
	/// </summary>
	/// <summary>
	/// The fields text as plain .NET values (long, double, bool, string), in order: the parser the
	/// tests exercise, with no Godot type in it (a Godot collection needs the engine running).
	/// </summary>
	public static List<KeyValuePair<string, object>> ParseFieldsPlain(string fields)
	{
		var result = new List<KeyValuePair<string, object>>(fields.AsSpan().Count('='));
		ParseFieldsPlain(fields, result);
		return result;
	}

	/// <summary><see cref="ParseFieldsPlain(string)"/> into a kept list, replacing what it held.</summary>
	public static void ParseFieldsPlain(string fields, List<KeyValuePair<string, object>> result)
	{
		result.Clear();
		int i = 0;
		int n = fields.Length;
		while (i < n)
		{
			while (i < n && (fields[i] == ',' || fields[i] == ' '))
			{
				i++;
			}

			int keyStart = i;
			while (i < n && fields[i] != '=' && fields[i] != ',')
			{
				i++;
			}

			ReadOnlySpan<char> key = fields.AsSpan(keyStart, i - keyStart).Trim();
			if (i >= n || fields[i] != '=')
			{
				break;
			}

			i++;
			object value;
			if (i < n && fields[i] == '\'')
			{
				int close = fields.IndexOf('\'', i + 1);
				if (close < 0)
				{
					close = n;
				}

				value = Shared(fields.AsSpan(i + 1, close - (i + 1)));
				i = Math.Min(close + 1, n);
			}
			else
			{
				int valueStart = i;
				while (i < n && fields[i] != ',')
				{
					i++;
				}

				value = ParseScalarPlain(fields.AsSpan(valueStart, i - valueStart).Trim());
			}

			if (key.Length != 0)
			{
				result.Add(new KeyValuePair<string, object>(Shared(key), value));
			}
		}
	}

	private static object ParseScalarPlain(ReadOnlySpan<char> text)
	{
		if (text.SequenceEqual("true"))
		{
			return BoxedTrue;
		}

		if (text.SequenceEqual("false"))
		{
			return BoxedFalse;
		}

		if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long integer))
		{
			return integer >= 0 && integer < BoxedSmall.Length ? BoxedSmall[integer] : integer;
		}

		if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double real))
		{
			return real;
		}

		return Shared(text);
	}

	private static readonly object BoxedTrue = true;
	private static readonly object BoxedFalse = false;

	/// <summary>Players, kinds, modes and flags: the small integers most fields are, boxed once.</summary>
	private static readonly object[] BoxedSmall = Enumerable.Range(0, 256).Select(i => (object)(long)i).ToArray();

	/// <summary>
	/// The field names and short texts the bus keeps sending, one string each. Direct-mapped, so it
	/// cannot grow and a clash only costs a new string; the guest thread's verdicts read it too, which
	/// is safe as a slot is one reference and a string is compared before it is used.
	/// </summary>
	private static readonly string?[] SharedTexts = new string?[512];

	private static string Shared(ReadOnlySpan<char> text)
	{
		if (text.Length == 0)
		{
			return string.Empty;
		}

		if (text.Length > 64)
		{
			return new string(text);
		}

		ref string? slot = ref SharedTexts[string.GetHashCode(text) & (SharedTexts.Length - 1)];
		string? held = slot;
		if (held is not null && text.SequenceEqual(held))
		{
			return held;
		}

		held = new string(text);
		slot = held;
		return held;
	}

	/// <summary>The fields text as the Dictionary a script receives (engine only).</summary>
	public static Godot.Collections.Dictionary ParseFields(string fields) => ToDictionary(ParseFieldsPlain(fields));

	/// <summary>Plain fields as the Dictionary a script receives (engine only).</summary>
	private static Godot.Collections.Dictionary ToDictionary(List<KeyValuePair<string, object>> fields)
	{
		var result = new Godot.Collections.Dictionary();
		foreach ((string key, object value) in fields)
		{
			// The Dictionary keeps its own copy, so a text's Variant is released at once: left to
			// the finalizer it outlives the next collection or two for nothing.
			Variant held = FieldValue(value);
			result[FieldKey(key)] = held;
			held.Dispose();
		}

		return result;
	}

	private static Variant FieldValue(object value) => value switch
	{
		long l => Variant.From(l),
		double d => Variant.From(d),
		bool b => Variant.From(b),
		_ => Variant.From((string)value),
	};

	/// <summary>
	/// A field name as a Dictionary key. A Variant of text costs a tracked, finalized object per
	/// conversion and a Dictionary's every key takes one, so each name is converted once. The names
	/// are the API's; the bound is for a mod's own, which never reach here in numbers.
	/// </summary>
	private static Variant FieldKey(string key)
	{
		if (FieldKeys.TryGetValue(key, out Variant known))
		{
			return known;
		}

		Variant made = key;
		if (FieldKeys.Count < 1024)
		{
			FieldKeys[key] = made;
		}

		return made;
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Variant> FieldKeys = new(StringComparer.Ordinal);

}
