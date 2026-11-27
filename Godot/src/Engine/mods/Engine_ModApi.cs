using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Mods;

/// <summary>One field of a mod event, as the native struct and the Lua table both carry it.</summary>
public readonly record struct ModApiField(string Name, string Type, string Meaning, bool Writable = false)
{
	/// <summary>
	/// A field only a native handler receives: an object of the seam's own (a hit record, a
	/// component, an action) that Lua and Godot have no use for and are not given.
	/// </summary>
	public bool NativeOnly => Meaning.Contains("(native", StringComparison.Ordinal);
}

/// <summary>
/// One event of the mod API: its name in C++ (<c>&lt;Name&gt;Event</c>, <c>REFII_MOD_ON(Name, fn)</c>)
/// and in Lua (<c>Refii.on("lua_name", fn)</c>), the numeric kind the bus carries, the game
/// seam it is raised from, its fields, what cancelling means (empty when it cannot be), and — for
/// a RELAYED event — who decides it and what the other co-op machine receives (empty otherwise).
/// </summary>
public sealed record ModApiEvent(
	string Name,
	string LuaName,
	int Kind,
	string Seam,
	string Summary,
	string Cancel,
	IReadOnlyList<ModApiField> Fields,
	string Relayed = "")
{
	/// <summary>
	/// The event is relayed: decided on one co-op machine and dispatched on the other as a replica
	/// (mod_events.cpp's relay table, <c>kRelayed</c>).
	/// </summary>
	public bool IsRelayed => Relayed.Length != 0;

	/// <summary>The config names of the guest functions behind <see cref="Seam"/>, if any.</summary>
	public IEnumerable<string> SeamFunctions =>
		Seam.Split(new[] { ' ', ',', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
			.Where(token => token.StartsWith("rex_", StringComparison.Ordinal));

	public bool Cancellable => Cancel.Length != 0;
}

/// <summary>One helper call of the native surface.</summary>
public readonly record struct ModApiHelper(string Signature, string Meaning);

/// <summary>
/// One row of the parity table (<see cref="ModApi.Twins"/>): what Lua writes and what Godot writes for
/// one meaning, as space-separated tokens; a row with an empty side carries <see cref="Why"/> that side
/// has no form of its own.
/// </summary>
public readonly record struct ModApiTwin(string Lua, string Godot, string Why = "")
{
	public bool Exempt => Lua.Length == 0 || Godot.Length == 0;

	public IEnumerable<string> LuaTokens => Lua.Split(' ', StringSplitOptions.RemoveEmptyEntries);

	public IEnumerable<string> GodotTokens => Godot.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// THE MOD API CATALOG: the one description of what <c>core/refii_mod_events.h</c> and
/// <c>core/refii_mod.h</c> offer, read by the Modding Guide, by <c>RefiiModApi</c> (the editor's
/// scripting surface), by the Lua runtime preamble (the event names), and by a test that holds
/// it to the header. A new event is added in the header and here, in the same change.
/// </summary>
public static class ModApi
{
	/// <summary><c>REFII_MOD_API_VERSION</c>.</summary>
	public const int Version = 2;

	/// <summary>
	/// THE SUBSCRIPTION MASKS' CAPACITY, per surface; <see cref="Events"/>.Count stays at or under
	/// each (ModApiTests holds these to the payload's constants and to REFII_EVENT_COUNT). Lua: the
	/// game Lua's number is a 32-bit float, exact to 24 bits, so a mask crosses the seam as groups of
	/// <see cref="LuaMaskBits"/> bits — never more than 20 significant bits in one number —
	/// <see cref="LuaMaskGroups"/> groups (mod_events.cpp kLuaMaskBits / kLuaMaskGroups, the
	/// preamble's <c>Refii.on</c>). Host (RefiiHost's event and verdict masks): <see cref="HostMaskWords"/>
	/// words of 64 (payload_bridge.h REFII_MOD_HOST_MASK_WORDS; <see cref="ModEventMask"/>).
	/// </summary>
	public const int LuaMaskBits = 20;
	public const int LuaMaskGroups = 10;
	public const int LuaMaskCapacity = LuaMaskBits * LuaMaskGroups;
	public const int HostMaskWords = 4;
	public const int HostMaskCapacity = 64 * HostMaskWords;

	/// <summary>
	/// THE RULE THE WHOLE SURFACE KEEPS, shown at the top of the Guide's API page: the API is the
	/// shared layer the base game's own co-op fixes and the mods both stand on, Forge/Fabric style.
	/// </summary>
	public const string PlayerAgnostic =
		"EVERY API SERVES BOTH PLAYERS; NEVER ASSUME PLAYER ONE. Events carry the player they are about (1 or 2), every helper takes a player or an entity, " +
		"and the \"which hero\" questions have one answer each, the one the base game's own co-op fixes use: player_of (is it a hero), player_for (who \"the hero\" is for an NPC or a dog), " +
		"acting_player / run_as_player (the hero a script stands for), combat_state(p), player_here(p) (where a hero's own effects are made) and world_here (where the world's are). " +
		"An event decided on one machine reaches the other when its effect belongs there (RELAYED: player two's swings reach the host's AI; the host's hits reach the joiner's looks), so a mod needs no co-op channel of its own. " +
		"What is shown follows the player it is for: a box, question, tutorial, fade, camera or Player.* call made while acting as player p appears on p's screen and acts on p's hero (online on p's machine, the answer coming back; split screen in p's half) — show_to_player(p, ...) asks for it outright.";

	/// <summary>The note a player-decided relayed event carries.</summary>
	private const string RelayedFromPlayer =
		"RELAYED: decided where the hero is played; online, the other machine receives a copy as a replica (refii_mod_event_replica / Refii.event_replica(); Godot's e.replica) with the actor as its own copy of that hero and the deciding machine's objects 0 — the host's AI sees player two's, the joiner sees player one's";

	/// <summary>Player and entity helpers every co-op rule is written with.</summary>
	private const string PlayerForMeaning =
		"the player an entity deals with, 1 or 2 (0 only with no hero): a hero its own, a dog its master's, anything else the nearer living hero with 3 m of hysteresis";

	private static ModApiField F(string name, string type, string meaning, bool writable = false) =>
		new(name, type, meaning, writable);

	/// <summary>
	/// What an entity or object field holds on each side: natively the guest address; in Lua and
	/// in a Godot script a HANDLE (<c>refii_mod_handle</c>), a small integer for that object this
	/// session, because the game's Lua number is a 32-bit float. Hashes (definitions, text) reach
	/// Lua and Godot as "0x…" strings for the same reason.
	/// </summary>
	public const string HandleNote = "an entity: natively its address, in Lua and Godot its handle";

	private static readonly ModApiField[] Common =
	{
		F("ctx", "PPCContext*", "the guest context, a copy: legal for guest calls (native only)"),
		F("base", "uint8_t*", "guest memory (native only)"),
		F("cancel", "bool", "set to refuse, where the event allows it; a Lua handler returns true", true),
	};

	private static readonly ModApiField[] Hit =
	{
		F("victim", "uint32_t", "the entity taking the hit"),
		F("victim_player", "int32_t", "1 or 2 when the victim is that player's hero, else 0"),
		F("attacker", "uint32_t", "the entity that dealt it, or 0"),
		F("attacker_player", "int32_t", "1 or 2 when the attacker is a player's hero, else 0"),
		F("hit", "uint32_t", "the hit record (native: +0x38 damage, +0x44 serial, +0x48 level, +0x54 spell type)"),
		F("damage", "float", "the damage the hit carries", true),
		F("spell_type", "int32_t", "-1 for a non-spell hit; 0 Lightning 1 Fireball 2 Slow Time 3 Blades 4 Vortex 5 Chaos 6 Force Push 7 Raise Dead"),
		F("spell_level", "int32_t", "the cast's power 0..4"),
		F("spell_serial", "uint32_t", "the cast's number: every hit of one cast carries the same"),
		F("knockdown", "bool", "the hit asks for a knockdown — force push and heavy blows set it (writable before delivery)", true),
		F("tag", "int32_t", "the tag of the cast whose serial the hit carries (spell_launch's tag, cast_spell's options.tag), 0 none; a cast's damage_scale is already in damage"),
	};

	/// <summary>The same fields after delivery: nothing is written back any more.</summary>
	private static readonly ModApiField[] HitAfter =
		Hit.Select(f => f with { Writable = false, Meaning = f.Name == "damage" ? "the damage the hit carried in" : f.Name == "knockdown" ? "the hit asked for a knockdown" : f.Meaning }).ToArray();

	/// <summary>Every event, in kind order. The kinds are <c>refii_mod_event_kind</c>'s.</summary>
	public static readonly IReadOnlyList<ModApiEvent> Events = new[]
	{
		new ModApiEvent("Tick", "tick", 0, "the guest tick (RunModTicks), after the game's own tick work",
			"Once per game step, menus included, on every surface at once: Lua handlers run every guest tick with that tick's seconds, so a Lua mod's delays and cooldowns resolve on the same grain as native ones (one 119-byte chunk a tick: 2.7 us load + run on the host's own Lua 5.1.2, the handlers' work on top).",
			string.Empty,
			new[] { F("seconds", "float", "real seconds since the last tick, capped at 0.25 across a hitch") }),
		new ModApiEvent("Hero", "hero_ready", 1, "derived in the tick from the player manager's handles (refii_mod_player_hero)",
			"A player's hero now exists: a session or save began, or a co-op partner joined. Lua also learns it through Refii.hero(player).",
			string.Empty,
			new[] { F("player", "int32_t", "1 or 2"), F("hero", "uint32_t", "the hero entity") }),
		new ModApiEvent("HeroGone", "hero_gone", 2, "derived in the tick, as HeroReady",
			"A player's hero stopped existing (a session ended, a partner left).",
			string.Empty,
			new[] { F("player", "int32_t", "1 or 2"), F("hero", "uint32_t", "the entity that was the hero") }),
		new ModApiEvent("Lua", "lua_ready", 3, "rex_lhLuaScriptSystem_LoadStartupScripts, after every mod's init.lua ran",
			"The game's Lua state was rebuilt for a session and the mods' init chunks have run.",
			string.Empty,
			new[] { F("generation", "uint32_t", "refii_mod_lua_generation(): steps on every rebuild and save load") }),
		new ModApiEvent("SaveLoaded", "save_loaded", 4, "rex_lhLuaScriptSystem_LoadScriptSave",
			"A save's Lua table was loaded into the VM: RefiiModSave now holds the save's values. A mod keeping state there re-reads it here.",
			string.Empty,
			new[] { F("generation", "uint32_t", "refii_mod_lua_generation()") }),
		new ModApiEvent("GameAction", "game_action", 5, "rex_CPlayerActionReceiver_ProcessAction (r3 receiver, r4 message; entity *(*(r3+0xC)))",
			"A GameAction reached a player hero's receiver: a button's press or release as the control scheme maps it (49 lock-on set, 50 lock-on release, 11 interact, ...; the GDB's GameAction rows).",
			"the game never sees the action",
			new[]
			{
				F("hero", "uint32_t", "the hero whose receiver got it"),
				F("player", "int32_t", "1 or 2"),
				F("action", "uint32_t", "the GameAction id"),
				F("message", "uint32_t", "the 24-byte action message (native)"),
			}),
		new ModApiEvent("AbilityRequest", "ability_request", 6, "rex_CECAbilities_PerformRequest (r3 entity, r4 request; type = request vtable slot 1)",
			"A creature asked its ability set for an ability: 16 melee pressed, 17 melee released, 18 magic charge, and the rest of the AbilityType enum.",
			"the request matches nothing; a refused melee press (16) refuses its release (17) too — the bus does it: that entity's next release is raised already cancelled and refused whatever a handler says",
			new[]
			{
				F("entity", "uint32_t", "the creature"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("request", "uint32_t", "the request object (native)"),
				F("type", "uint32_t", "the AbilityType"),
			}),
		new ModApiEvent("MeleeSwing", "melee_swing", 7, "rex_CAbilityActionMelee_CommitSwing (r3 ability, r4 entity), after the swing's animation started",
			"A strike's swing is committed. Not refusable here: refuse the AbilityRequest instead.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the striker"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("ability", "uint32_t", "the strike ability (native; 0 in a relayed copy)"),
				F("action", "uint32_t", "the live strike action the action manager (component 49) now runs, or 0 (Lua and Godot: a handle, what current_action answers; 0 in a relayed copy)"),
				F("action_type", "int32_t", "the action's EScriptableAction (action_type): -1, since a strike is cloned by the action manager and never made by the definition factory"),
				F("action_name", "const char* / string", "its enum name (action_name), \"\" for a strike"),
				F("strike", "int32_t", "the strike's kind, the ability's EAbilityActionType (ability +0x10, copied to the action's +0x7C): 1 strike, 2 flourish class, 24 release strike, 35 release strike confined, 36 advanced strike, 37 wall smash, 38 wall leap, 43 / 44 lunge close / far, 45 sprint attack (Fable III renumbers above 25: 36..38 confined, 39 advanced, 40 wall smash, 41 wall leap, 46 / 47 lunge, 48 sprint attack)"),
				F("flourish", "bool", "the ability's own GDB Flourish property: the hero's charged flourish (a release strike only the charge lets start)"),
			},
			RelayedFromPlayer),
		new ModApiEvent("Roll", "roll_can_start", 8, "rex_CAbilityActionStrafeRoll_CanStart (r4 entity)",
			"The dodge roll asks whether it may start; asked once per matching roll record, so it gates without spending.",
			"the roll does not start",
			new[] { F("entity", "uint32_t", "the roller"), F("player", "int32_t", "1 or 2 for a hero, else 0") }),
		new ModApiEvent("RollStarted", "roll_started", 9, "rex_CAbilityActionStrafeRoll_Start (r4 entity), when it returned 1",
			"A roll was queued. Rolls are invulnerable for their whole length in the game already.",
			string.Empty,
			new[] { F("entity", "uint32_t", "the roller"), F("player", "int32_t", "1 or 2 for a hero, else 0") },
			RelayedFromPlayer),
		new ModApiEvent("Block", "block", 10, "rex_CActionBlock_OnHitBlockPose / rex_CActionBlock_OnHitBlockedResponse (r3 listener at action+0x54, r4 hit)",
			"A block is taking a hit. Fired once per hit although the game runs two block listeners (the second takes the first's verdict).",
			"the guard BREAKS: the hit lands in full, as if there were no block (never for an unblockable hit)",
			new[]
			{
				F("blocker", "uint32_t", "the blocking entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("hit", "uint32_t", "the hit record (native)"),
				F("damage", "float", "the hit's damage"),
				F("unblockable", "bool", "the hit bypasses blocks in the game already"),
			}),
		new ModApiEvent("AnimEvent", "anim_event", 11, "rex_CAction_OnAnimEvent (r3 live action, r6 value object; text **r6)",
			"An animation event reached an action; ALLOW_ACTION_INTERRUPT is the one that lets the next action in. Actions whose class overrides the handler without calling the base (about 18 of 180) are not seen.",
			"the action never sees the event",
			new[]
			{
				F("action", "uint32_t", "the live action (Lua and Godot: a handle, what current_action answers — match a committed strike on it)"),
				F("action_type", "int32_t", "the action's EScriptableAction (action_type), -1 unknown"),
				F("action_name", "const char* / string", "its enum name (action_name), \"\" unknown"),
				F("name", "const char* / string", "the event's text"),
				F("entity", "uint32_t", "the action's owner, as the action states it (any creature)"),
				F("player", "int32_t", "1 or 2 when the owner is a player hero, else 0"),
			}),
		new ModApiEvent("Hit", "hit", 12, "rex_CECHitReceiver_OnHit (r3 victim's component 31, owner at +4; r4 hit), before delivery",
			"A hit is arriving at its victim: melee, spell, physics, the Blades' stabs. The one receiver every hit reaches.",
			"the hit is dropped entirely: no damage, no response",
			Hit),
		new ModApiEvent("HitLanded", "hit_landed", 13, "rex_CECHitReceiver_OnHit, after delivery",
			"A hit was delivered, with the damage it carried in. Read-only.",
			string.Empty,
			HitAfter,
			"RELAYED: decided where the world runs (the host); online, a hit with a hero on either side reaches the joiner as a replica (refii_mod_event_replica / Refii.event_replica(); Godot's e.replica) with the heroes as the joiner's copies, other entities by UniqueID, hit 0 and spell_serial the joiner's own spell of that cast with its tag once the cast is paired (host_serial / local_serial; a hit for a spell the host announced and the joiner has not made yet waits up to a second), else the host's number — follow it for looks, never act on the world from it"),
		new ModApiEvent("HitResponse", "hit_response", 14, "rex_CECHitResponsePlayAction_OnHit (r3 listener, entity at r3-8; r4 hit; r5 result word), priority 20 on the dispatch",
			"The victim is about to flinch or be knocked down by a hit. The game's own early-outs (a blocked or parried hit, a ranged hit, a knockdown request, a killing blow) come first, so a lethal hit's fall always plays. A second response the listener can play (its +28 branch) is cancelled with it; a flinch played by a knockdown-flagged hit without the listener's response raises nothing.",
			"the hit is absorbed: no animation, no state left behind",
			new[]
			{
				F("victim", "uint32_t", "the entity"),
				F("victim_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("hit", "uint32_t", "the hit record (native)"),
				F("damage", "float", "the game's own arithmetic for the hit (rex_CECHealth_CalculateDamage)"),
				F("health", "float", "the victim's current health"),
				F("max_health", "float", "the victim's maximum health"),
			}),
		new ModApiEvent("SpellCharge", "spell_charge", 15, "rex_CAbilityActionMagicCharge_CanStart (r4 entity)",
			"A hero pressed B to charge a spell. Also asked to continue a charge already held.",
			"the charge does not start",
			new[] { F("entity", "uint32_t", "the caster"), F("player", "int32_t", "1 or 2 for a hero, else 0") }),
		new ModApiEvent("SpellChargeLevel", "spell_charge_level", 16, "rex_CECSpellManager_ChargeLevelFromTime (r3 spell manager; r6 int* charged-slot count, r7 int* first slot), after it",
			"The charge time became the top slot to cast (first + count - 1); the release's cascade launches slots 0..level, each at its own power. Raised every frame of a held charge, to every surface alike: a cap written here is re-evaluated each frame (a pool refilling mid-hold raises it), and a repeat charge to the same slot is reported again.",
			string.Empty,
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("level", "int32_t", "the top slot 0..4; the cascade launches 0..level (native: writable, clamped)", true),
			}),
		new ModApiEvent("SpellLaunch", "spell_launch", 17, "rex_CECSpellManager_LaunchSlot (r3 manager = component 56, caster at +4; r4 targeted; r5 slot; slot table +0x80)",
			"One slot is about to launch. The type can be changed for this launch only; the slot keeps its assignment.",
			"the launch is skipped",
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("manager", "uint32_t", "the spell manager component (native)"),
				F("targeted", "bool", "a targeted cast (else area)"),
				F("slot", "int32_t", "the slot 0..4, also the power level"),
				F("type", "int32_t", "the spell type the slot holds; -1 empty (native: writable for this launch)", true),
				F("cascading", "bool", "the release cascade is running through the lower slots"),
				F("tag", "int32_t", "the cast's tag, 0 none (writable): the spell this launch creates carries it, and so does every later event of that cast — spell_created, vortex_started, hit, hit_landed, fireball_impact, status_applying — by its serial, a hit landed inside the factory included", true),
			}),
		new ModApiEvent("SpellLaunched", "spell_launched", 18, "rex_CECSpellManager_OnSpellLaunched (r3 manager, r4 slot), behind the launch's own success test",
			"A spell was really created for a launch.",
			string.Empty,
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("manager", "uint32_t", "the spell manager component (native)"),
				F("slot", "int32_t", "the slot launched"),
			}),
		new ModApiEvent("SpellCreated", "spell_created", 19, "rex_CSpellRegistry_Create (r3 registry, r4 type, r5 targeted, r6 target, r7 level, r8 caster), after it",
			"The spell factory made a spell object: every cast, the player's and any creature's, and refii_mod_cast_spell's. Read-only.",
			string.Empty,
			new[]
			{
				F("spell", "uint32_t", "the spell object"),
				F("type", "int32_t", "0 Lightning .. 7 Raise Dead"),
				F("targeted", "bool", "a targeted cast"),
				F("target", "uint32_t", "the target entity, or 0"),
				F("level", "int32_t", "the power 0..4"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("serial", "uint32_t", "the cast's number (the spell object's +0x44), carried by its hits"),
				F("slot", "int32_t", "the launch slot it came from (spell_launch's slot), -1 for a cast no launch made (a creature's own, cast_spell)"),
				F("tag", "int32_t", "the cast's tag (spell_launch's, or cast_spell's options.tag), 0 none; online, on the machine not deciding the caster, the deciding machine's tag once the cast is paired (0 before)"),
				F("host_serial", "uint32_t", "this spell's serial on the online host (host_serial(serial)): serial itself on the host, alone and in local co-op; on the joiner the host's spell of the same cast once paired, 0 before"),
			}),
		new ModApiEvent("Experience", "experience", 20, "rex_CECExperience_Modify (r3 component 164, r4 pool, f1 amount) / rex_CECExperience_Spend (r3 component, r4 pool, f1 cost)",
			"Experience is changing on an entity: earned, refunded or spent. Pools: 0 General, 1 Strength, 2 Skill, 3 Will.",
			"the pools are left untouched",
			new[]
			{
				F("entity", "uint32_t", "the entity, when the component is its own experience component; else 0"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("component", "uint32_t", "the experience component (native)"),
				F("pool", "int32_t", "0 General, 1 Strength, 2 Skill, 3 Will"),
				F("amount", "float", "the change (signed) or the cost (native: writable)", true),
				F("spend", "bool", "true for a Spend, false for a Modify"),
				F("nested", "bool", "raised inside another experience change (a Spend's payments, the co-op share): scale only when false; cannot be cancelled"),
			}),
		new ModApiEvent("EntityDied", "entity_died", 21, "rex_CECCreature_Die (r3 creature component 20, owner at +4, alive byte +0x18; r4 killer) / rex_CECCreature_FinishDeath (r3 creature component)",
			"A creature is dying: every creature death goes through here, from damage, a script kill or a fall. Heroes come to HeroDowned instead.",
			"for a death by damage (health at zero) only: the creature stays alive with 1 health and the next hit asks again; other deaths cannot be refused",
			new[]
			{
				F("entity", "uint32_t", "the dying creature"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("killer", "uint32_t", "the killer entity, or 0"),
				F("killer_player", "int32_t", "1 or 2 when the killer is a hero, else 0"),
			}),
		new ModApiEvent("HeroDowned", "hero_downed", 22, "rex_CECHealth_OnHeroDeath (r3 health component 36, owner at +4; r4 killer), the hero branch of the zero-health dispatch",
			"A player's hero reached zero health: the knockdown, the scar, the resurrection potion and the revive flow follow.",
			"all of it is skipped and the hero keeps 1 health — a mod's own death and respawn starts here",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("killer", "uint32_t", "the killer entity, or 0"),
				F("killer_player", "int32_t", "1 or 2 when the killer is a hero, else 0"),
			}),
		new ModApiEvent("Damage", "damage", 23, "rex_CECHealth_OnHit (r3 listener = health+0xC, entity at r3-8; r4 hit; r5 handled word), priority 30 on the dispatch",
			"The victim's health is about to take a hit's damage, after the responses and blocks ran. The damage is the game's final arithmetic; change it on Hit instead.",
			"no health is removed and no death follows; the flinch and sound already played",
			new[]
			{
				F("victim", "uint32_t", "the entity"),
				F("victim_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("attacker", "uint32_t", "the attacker entity, or 0"),
				F("attacker_player", "int32_t", "1 or 2 when the attacker is a hero, else 0"),
				F("hit", "uint32_t", "the hit record (native)"),
				F("damage", "float", "the game's damage for the hit (rex_CECHealth_CalculateDamage)"),
				F("health", "float", "the victim's health before"),
				F("max_health", "float", "the victim's maximum health"),
				F("lethal", "bool", "the damage reaches the health"),
				F("spell_type", "int32_t", "-1 for a non-spell hit, else the spell"),
				F("spell_level", "int32_t", "the cast's power 0..4"),
				F("spell_serial", "uint32_t", "the cast's number"),
			}),
		new ModApiEvent("LevelLoaded", "level_loaded", 24, "rex_lhLuaScriptSystem_OnLevelLoad (r3 script-system owner, r4 from-save), after the game's AIManager:OnLevelLoad",
			"A level finished loading and the heroes stand in it: a new game, a fast travel, a save load. Not a door inside a region.",
			string.Empty,
			new[]
			{
				F("level", "const char* / string", "the level's name"),
				F("from_save", "bool", "the level came up from a save load"),
			}),
		new ModApiEvent("LevelUnloading", "level_unloading", 25, "rex_lhWorld_UnloadLevel (r3 world manager; +0x24 the level holder), before it",
			"The current level is about to be unloaded: the last moment its entities exist.",
			string.Empty,
			new[] { F("level", "const char* / string", "the level's name") }),
		new ModApiEvent("GameSaving", "game_saving", 26, "rex_lhSaveManager_WriteSave (r3 save manager, r4 file name), before it",
			"A save file is about to be written (the main save; a quest-fail save when one is made): put state into RefiiModSave now.",
			string.Empty,
			new[] { F("file", "const char* / string", "the save file's name") }),
		new ModApiEvent("ItemAdded", "item_added", 27, "rex_CECInventory_AddOneOfRecord (r3 out, r4 inventory component 13, owner at +4; r5 definition hash; r6 notify), after it",
			"An item was added to an entity's inventory by its definition: a pickup, a purchase, a gift, a reward, a script, the starting kit. After the add: several of its callers use the new item at once and cannot take a refusal.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the inventory's owner"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("inventory", "uint32_t", "the inventory component (native)"),
				F("definition", "uint32_t", "the item definition's GDB record hash"),
				F("notify", "bool", "the game's item-added notifications run"),
			}),
		new ModApiEvent("ItemUsed", "item_used", 28, "rex_lhEntity_UseInventoryItem (r3 entity, r4 item shared pointer, r5 script override)",
			"An entity is using an inventory item: a potion, food, a book, a gift — anything with a carried-action script, which does the consuming.",
			"the use is refused",
			new[]
			{
				F("entity", "uint32_t", "the user"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("item", "uint32_t", "the item object (native; 0 in a relayed copy)"),
				F("definition", "uint32_t", "the item definition's GDB record hash"),
				F("count", "int32_t", "how many the stack holds"),
			},
			RelayedFromPlayer),
		new ModApiEvent("HeldItem", "held_item", 29, "rex_CECHeldItems_AttachItemToSlot (r3 component 24, owner at +4; r4 slot name; r5 item) / rex_CECHeldItems_ReleaseSlot (r3, r4)",
			"An item entity is attached to or released from one of an entity's holding slots: a weapon drawn (release from the holding slot, attach to the hand), sheathed, or picked up.",
			"a release is refused (the attach after it then sees nothing); an attach cannot be refused — its callers record the drawn weapon regardless",
			new[]
			{
				F("entity", "uint32_t", "the holder"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("slot", "const char* / string", "the slot's name"),
				F("item", "uint32_t", "the item entity on an attach; 0 on a release"),
				F("attached", "bool", "true for an attach, false for a release"),
			}),
		new ModApiEvent("WeaponEquipped", "weapon_equipped", 30, "rex_CECWeaponSet_RecordDrawnItem (r3 weapon set 28, owner at +4; r4 item entity), before it",
			"A weapon was equipped into an entity's weapon set: the melee or ranged record changes. At equip time, not on a draw. Read-only.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the owner"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("item", "uint32_t", "the weapon's item entity"),
				F("definition", "uint32_t", "the weapon definition's GDB record hash"),
			}),
		new ModApiEvent("AbilityLevel", "ability_level", 31, "rex_CECStats_SetAbilityLevel (r3 stats 57, owner at +4; r4 type 1..14; r5 level; r6 force)",
			"An ability's level is being set on an entity's stats: a purchase or unlearn on the abilities book, a script, a save load.",
			"the level stays as it was (the book has already spent the experience)",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("type", "int32_t", "the ability 1..14 (1 Heavy Weapons, 2 Physique, 3 Toughness, 4 Ranged Weapons, 5 Accuracy, 6 Speed, 7 Lightning .. 14 Raise Dead)"),
				F("level", "int32_t", "the level being set (native: writable)", true),
				F("old_level", "int32_t", "the level before"),
			}),
		new ModApiEvent("Gold", "gold", 32, "rex_CECMoney_Modify (r3 purse component 81, owner at +4, amount at +12; r4 delta; r5 no split; r6 co-op share; r7 passive)",
			"Gold is changing on an entity's purse: a purchase, a sale, a pickup, rent, a gift, a reward.",
			"the purse is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the purse's owner"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("gold", "int32_t", "the purse before"),
				F("delta", "int32_t", "the signed change (writable)", true),
				F("passive", "bool", "income shown with the HUD's icon (rent)"),
				F("share", "bool", "the game's co-op split of a change already reported: scale only when false; cannot be cancelled"),
			}),
		new ModApiEvent("Relation", "relation", 33, "rex_CECFaction_RelationTo (r3 faction component 133, owner at +4; r4 the other entity), after it",
			"The game asked how an entity stands to another: its faction rule, asked by the AI, the targeting and the HUD many times a tick. The answer is writable: a handler makes two creatures friends or foes.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the entity asking"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("other", "uint32_t", "the entity asked about"),
				F("other_player", "int32_t", "1 or 2 when the other is a hero, else 0"),
				F("relation", "int32_t", "1 neutral, 2 ally, 4 enemy (writable)", true),
			}),
		new ModApiEvent("InputAction", "input_action", 34, "the guest tick, from the host's input queue (a mod's input.json; RefiiHostPostModInput)",
			"A mod's own input action was pressed or released by a player: the keyboard or the first pad is player 1, the second pad player 2. Edges only. The player rebinds an action on Options › Controls › the mod's title (a key row and a pad row per action, where the declaration binds each), stored in refii.toml as key_<mod>.<action> / pad_<mod>.<action> over the file's default; none unbinds.",
			string.Empty,
			new[]
			{
				F("name", "const char* / string", "\"<mod>.<action>\" from the mod's input.json"),
				F("player", "int32_t", "1 or 2"),
				F("pressed", "bool", "true on the press, false on the release"),
			}),
		new ModApiEvent("QuestStarted", "quest_started", 35, "rex_CECQuestTracker_SetAsActive (r3 tracker component, owner at +4; r4 quest name; r5 flag; r6 make primary)",
			"A quest is being activated on a hero's quest tracker, by name (the GDB record's, the one the shipped scripts pass).",
			"the quest stays inactive",
			new[]
			{
				F("quest", "const char* / string", "the quest's name"),
				F("hero", "uint32_t", "the tracker's owner"),
				F("player", "int32_t", "1 or 2"),
				F("make_primary", "bool", "it becomes the tracked quest"),
			}),
		new ModApiEvent("QuestEnded", "quest_ended", 36, "rex_CECQuestTracker_OnQuestEnded (r3 tracker; r4 record: +0 name, +21 completed, +22 failed), after it",
			"A quest ended: completed (its rewards were just given) or failed. After the fact.",
			string.Empty,
			new[]
			{
				F("quest", "const char* / string", "the quest's name"),
				F("hero", "uint32_t", "the tracker's owner"),
				F("player", "int32_t", "1 or 2"),
				F("completed", "bool", "it was completed"),
				F("failed", "bool", "it was failed"),
			}),
		new ModApiEvent("QuestObjective", "quest_objective", 37, "rex_CECQuestTracker_SetObjectiveTagSilently (r3 tracker; r4 quest name; r5 tag)",
			"An objective is being added to a quest: the text tag the quest log shows. Behind SetObjectiveTag, SetObjectiveTagFinal and a quest's default objective.",
			"nothing is added",
			new[]
			{
				F("quest", "const char* / string", "the quest's name"),
				F("tag", "const char* / string", "the objective's text tag"),
				F("hero", "uint32_t", "the tracker's owner"),
				F("player", "int32_t", "1 or 2"),
			}),
		new ModApiEvent("Dialogue", "dialogue", 38, "rex_CECTalk_PlayLine (r3 talk component, owner at +4; r4 text hash; r5 force subtitle; r6 listener)",
			"A creature is about to speak a line: every line, the heroes' and the villagers', reaches here with its final text key (after the voice and variant suffixes).",
			"the line is not spoken: no audio, no subtitle, no speech animation",
			new[]
			{
				F("speaker", "uint32_t", "the speaking entity"),
				F("speaker_player", "int32_t", "1 or 2 when the speaker is a hero, else 0"),
				F("text", "uint32_t", "the line's text hash (the TextManager key; Lua and Godot get it as a \"0x…\" string)"),
				F("listener", "uint32_t", "the entity spoken to, or 0"),
				F("listener_player", "int32_t", "1 or 2 when the listener is a hero, else 0"),
				F("subtitle", "bool", "the subtitle is forced"),
			}),
		new ModApiEvent("EntitySpawned", "entity_spawned", 39, "rex_lhEntityManager_Add (r3 the manager, r4 the entity), raised on the next tick",
			"An entity joined the world: a script or generator created it, or a level or save loaded it. Raised on the first tick after the add, when its components exist; at most 64 a tick, so a level load spreads over a few ticks.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the new entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("definition", "uint32_t", "its definition hash (Lua and Godot: a \"0x…\" string)"),
			}),
		new ModApiEvent("EntityRemoved", "entity_removed", 40, "rex_lhEntity_Destroy (r3 the entity), before it",
			"An entity is leaving the world (cleaned up after death, destroyed by a script, unloaded with its level): the last moment it is whole.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("definition", "uint32_t", "its definition hash"),
			}),
		new ModApiEvent("ExpressionStarted", "expression_started", 41, "rex_CECExpressionPerformer_StartExpression (r3 performer component, owner at +4; r4 the record pair)",
			"A creature starts an expression: the expression wheel, a script's PerformExpression, a trophy. Raised before the game's own can-perform check, which may still refuse it.",
			"the expression is refused",
			new[]
			{
				F("entity", "uint32_t", "the performer"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("expression", "uint32_t", "the expression record's hash"),
			}),
		new ModApiEvent("Renown", "renown", 42, "rex_lhHeroStats_ModifyRenown (r3 stats 57, owner at +4; r4 delta; r5 partner share)",
			"Renown is changing. The co-op partner's share is a second event with share set.",
			"renown is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("delta", "int32_t", "the change (writable)", true),
				F("share", "bool", "the game's co-op split of a change already reported: scale only when false; cannot be cancelled"),
			}),
		new ModApiEvent("Morality", "morality", 43, "rex_lhHeroStats_ModifyMorality (r3 stats, owner at +4; f1 delta)",
			"Morality (good and evil) is changing; the game clamps it afterwards. The game skips the change when the hero's stats are not live.",
			"morality is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("delta", "float", "the change, positive is good (writable)", true),
			}),
		new ModApiEvent("Purity", "purity", 44, "rex_lhHeroStats_ModifyPurity (r3 stats, owner at +4; f1 delta)",
			"Purity (pure and corrupt) is changing; the game clamps it afterwards. The game skips the change when the hero's stats are not live.",
			"purity is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("delta", "float", "the change, positive is pure (writable)", true),
			}),
		new ModApiEvent("Marriage", "marriage", 45, "rex_CECPlayerFamily_Marry (r3 the hero's family component, owner at +4; r4 spouse; r5 ceremony)",
			"A hero marries: the wedding ceremony or a script.",
			"the marriage is refused",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("spouse", "uint32_t", "the spouse"),
			}),
		new ModApiEvent("DogTrick", "dog_trick", 46, "rex_CECDog_PerformCommand (r3 dog component, owner at +4; r4 the command record pair; r5 force)",
			"The dog is told a trick: an expression's completion, a gift, an item, a script. Raised before the dog's unlocked-trick check, which may still refuse it.",
			"the dog does not do it",
			new[]
			{
				F("dog", "uint32_t", "the dog"),
				F("trick", "uint32_t", "the command record's hash"),
				F("player", "int32_t", "whose dog: 1 or 2 by its master (Dog.GetMaster, the dog component's +300), else by the players' dog records (Refii.player_dog); 0 for neither"),
			}),
		new ModApiEvent("RangedShot", "ranged_shot", 47, "rex_CECFirearm_Fire (r3 firearm component, owner at +4; r4 target; r5 aimed)",
			"A ranged weapon is about to fire, the hero's or anyone's. The game may still refuse it (no ammunition, the hero busy).",
			"the shot is not fired",
			new[]
			{
				F("shooter", "uint32_t", "the creature holding the weapon"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("target", "uint32_t", "the entity aimed at, or 0"),
				F("aimed", "bool", "an aimed shot rather than a hip shot"),
				F("weapon", "uint32_t", "the weapon's item entity"),
			}),
		new ModApiEvent("StatusEffect", "status_effect", 48, "rex_lhEntity_AddComponent (after; the component's GetECType) / rex_lhEntity_RemoveComponent (r3 entity, r4 id) / rex_lhRagdoll_GoFloppy / rex_lhRagdoll_Recover (r3 entity) / rex_CECCreature_SetUnconscious (r3 comp 20, r4 down) / rex_HollowMan_SetCrumbled (r3 action, r4 crumbled)",
			"A status effect started or ended on an entity: 1 electrified, 2 chaos, 3 burning, 4 caught in a vortex, 5 knocked down (every ragdoll fall), 6 mauled by the dog, 7 unconscious, 8 a hollow man crumbled to bones.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("effect", "int32_t", "1 electrified, 2 chaos, 3 burning, 4 in a vortex, 5 knocked down, 6 mauled by the dog, 7 unconscious, 8 crumbled (hollow man)"),
				F("started", "bool", "true when it started, false when it ended"),
				F("serial", "uint32_t", "for a status started inside a spell's installer (status_applying's seams) or a vortex's capture (vortex_caught's): that cast's serial (a hit's spell_serial); 4294967295 otherwise"),
				F("source", "uint32_t", "for a status started inside an installer or a vortex's capture: who put it on (the caster); 0 otherwise"),
			}),
		new ModApiEvent("TargetChanged", "target_changed", 49, "rex_CECTargetingPlayer_SetTarget (r3 targeting 77, owner at +4; r4 target) / _ClearTarget (r3)",
			"A player's target changed, after the fact; 0 when it was cleared.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("target", "uint32_t", "the new target, or 0"),
			}),
		new ModApiEvent("OpinionDeed", "opinion_deed", 50, "rex_CECOpinionReaction_OnDeed (r3 opinion component, owner at +4; r4 message: +32 deed, +52 kind, +72 doer)",
			"A villager reacts to a deed, an expression or a gift it saw: the opinion change and the renown, morality and purity the deed earns follow. Raised before the villager's own in-world check.",
			"the villager ignores it",
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("doer", "uint32_t", "who did it"),
				F("doer_player", "int32_t", "1 or 2 when the doer is a hero, else 0"),
				F("deed", "uint32_t", "the deed record's hash"),
				F("kind", "int32_t", "the message kind, 0..4"),
			}),
		new ModApiEvent("OpinionModified", "opinion_modified", 51, "rex_CECOpinionReaction_ModifyAxisValue (r3 component, owner at +4; r4 axis; f1 delta)",
			"A villager's opinion axis is changed directly, not through a deed (the villager's own reactions, scripts): 0 love, 1 fear, 2 attractiveness, 3 morality, 4 respect.",
			"the axis is left as it is",
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("axis", "int32_t", "0 love, 1 fear, 2 attractiveness, 3 morality, 4 respect"),
				F("delta", "float", "the change, clamped to [-1, 1] after (writable)", true),
				F("player", "int32_t", "whose opinion set moves: 2 while the villager holds player two's set (their opinion scope), else 1"),
			}),
		new ModApiEvent("TimeOfDay", "time_of_day", 52, "rex_lhGameTimeManager_Update (r3 the time manager: +4 day fraction, +8 day count), after it",
			"The world clock reached a new hour or a new day: natural time, a fast forward, sleeping, travel time and a script's set all show here (a set one tick late).",
			string.Empty,
			new[]
			{
				F("hour", "int32_t", "0..23"),
				F("day", "int32_t", "the day count"),
				F("time", "float", "the day fraction 0..1"),
				F("new_day", "bool", "the day count changed"),
			}),
		new ModApiEvent("WeatherTheme", "weather_theme", 53, "rex_lhEnvironmentThemeManager_BlendToTheme (r3 manager; r4 name; f1 weight; f2 seconds)",
			"An environment theme (the weather and the sky's look) is blended to by a script or the network. Position-painted blending never comes here. On a co-op joiner the host's blends arrive here too; refusing one there desyncs the two skies.",
			"the blend is skipped",
			new[]
			{
				F("theme", "const char* / string", "the theme's name"),
				F("weight", "float", "the target weight (writable)", true),
				F("seconds", "float", "the blend time (writable)", true),
			}),
		new ModApiEvent("SleepMenu", "sleep_menu", 54, "rex_SleepMenu_Show (r3 the bed; the sleeper: the bed's newest user, rex_CECOnActionUse_Use r4)",
			"A bed's sleep menu is about to open; the time skip after it is the shipped scripts'.",
			"the menu stays closed",
			new[]
			{
				F("seen", "bool", "always true"),
				F("player", "int32_t", "the player sleeping: the bed's user as the game's use routine passed it to the bed's script (rex_CECOnActionUse_Use); 0 when no player used it"),
			}),
		new ModApiEvent("Travel", "travel", 55, "rex_CmdLevelChange_Start (r3 command: +20 world, +24 level, +28 entrance, +36 type, +50 full load), after it",
			"A travel started: a door, a level exit, a fast travel, a script. full_load is false for a change inside the loaded level, which LevelLoaded never reports.",
			string.Empty,
			new[]
			{
				F("world", "const char* / string", "the destination world"),
				F("level", "const char* / string", "the destination level"),
				F("entrance", "const char* / string", "the entrance marker"),
				F("type", "int32_t", "the travel type (3 free fast travel, 4 paid, 5 level exit)"),
				F("full_load", "bool", "a full level load follows"),
				F("player", "int32_t", "who asked for it (both heroes travel): the player a level exit fired for (its user, the player its walk-in test tested, an online joiner's request) or the world map's owner for a fast travel; 0 for a script's travel or one this machine did not see asked for"),
			}),
		new ModApiEvent("FastTravel", "fast_travel", 56, "rex_FastTravelManager_TravelToStop (r3 manager; r4 level; r5 stop, +4 marker; r6 paid); the chooser: inside rex_lhGuiWorldMap_FastTravel, the world map's owner",
			"The hero chose a fast-travel stop: the world map or a script. From the world map the map has already closed when this fires; a refusal leaves the hero there with the map shut.",
			"the hero stays put and pays nothing",
			new[]
			{
				F("level", "const char* / string", "the destination level"),
				F("stop", "const char* / string", "the stop's marker"),
				F("paid", "bool", "the travel costs gold"),
				F("player", "int32_t", "who chose it: the world map's owner (rex_lhGuiWorldMap_FastTravel); 0 for a script's"),
			}),
		new ModApiEvent("MenuOpened", "menu_opened", 57, "rex_lhGuiManager_EnterGuiMode / rex_lhGuiManager_EnterGuiModeQuiet (r3 the GUI manager), when menu mode was off",
			"The game entered its menu mode; the world pauses behind it.",
			string.Empty,
			new[]
			{
				F("seen", "bool", "always true"),
				F("player", "int32_t", MenuPlayerMeaning),
			}),
		new ModApiEvent("MenuClosed", "menu_closed", 58, "rex_lhGuiManager_ExitScreen / rex_lhGuiManager_ExitGuiModeQuiet (r3 the GUI manager), when it really leaves menu mode",
			"The game left its menu mode.",
			string.Empty,
			new[]
			{
				F("seen", "bool", "always true"),
				F("player", "int32_t", MenuPlayerMeaning),
			}),
		new ModApiEvent("MenuScreen", "menu_screen", 59, "rex_lhGuiManager_SetScreen (r3 the GUI manager; r4 the screen id), when it changes; TU1 numbering on both builds",
			"A menu shows a screen: 0 pause, 2 spells, 14 abilities, 16 equipment, 17 logbook, 18 options, 22 shop, 23 world map, 24 co-op. A screen Godot owns (1000 and up) raises it from Godot on its open AND its close, to Godot scripts only, with two extra dictionary keys: host (true) and open (true opening, false closing).",
			string.Empty,
			new[]
			{
				F("screen", "int32_t", "the screen id the game states; 1000 and up a screen Godot owns (achievements 1013, multiplayer 1014, a mod's screens record)"),
				F("player", "int32_t", MenuPlayerMeaning),
			}),
		new ModApiEvent("WeatherRegion", "weather_region", 60, "rex_lhPaintMap_DaySetAt (r3 paint map, v1 the hero's position; returns the day set hash or -1), after it",
			"The hero walked into a different painted weather region: the region's environment day set, which picks the theme by time of day. A scripted blend is WeatherTheme. The game tests one hero, the one in player one's slot, once a frame — not once per split-screen view.",
			string.Empty,
			new[]
			{
				F("day_set", "uint32_t", "the region's day set (a GDB record hash; Lua and Godot: a \"0x…\" string), 0 outside every painted region"),
				F("previous", "uint32_t", "that player's day set before"),
				F("inside", "bool", "the hero is inside a painted region"),
				F("player", "int32_t", "the player whose hero the game tested (1 as shipped)"),
			}),
		new ModApiEvent("SleepChosen", "sleep_chosen", 61, "rex_lhMenuBox_Close (r3 the choice menu: +0 index, +13 cancelled), while the sleep menu is open",
			"The player answered a bed's sleep menu. A mod can change the choice or refuse the sleep; the shipped SleepFunction.Sleep applies it. A length the menu does not offer is a Lua mod wrapping SleepFunction.Sleep.",
			"the sleep is refused, as if cancelled",
			new[]
			{
				F("bed", "uint32_t", "the bed"),
				F("choice", "int32_t", "0 fast-forwards six hours, 1 twelve hours, 2 adds a day (the clock stays), 3 adds seven days (writable)", true),
				F("cancelled", "bool", "the player backed out"),
				F("player", "int32_t", "the player sleeping, as SleepMenu stated it"),
			}),
		new ModApiEvent("SlowTime", "slow_time", 62, "rex_SpellSlowTime_SetWorldScale (r3 the caster; f1 the scale; world +128)",
			"Slow Time changed the world's time scale. Every creature but the caster slows: the game slows the world and gives the caster (and the co-op partner) the inverse rate.",
			string.Empty,
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("scale", "float", "the world's time scale, 1 normal (writable)", true),
				F("active", "bool", "the world is slowed (scale < 1)"),
			}),
		new ModApiEvent("Summoned", "summoned", 63, "rex_SpellRaiseDead_BindSummoner (r3 summoner, r4 summon, r6 the spell's serial) / rex_SpellDamoclesSword_Create (r3 sword, r4 the sword set: its spell's serial at +0x10, r5 caster), after them",
			"A spell summoned a creature or a weapon. The summon leaves through EntityRemoved. serial is the summoning spell's as the game states it at each seam — the bind's own argument, the sword set's (swords a re-cast adds to a live set carry the set's: the spell they join) — so it matches that cast's spell_created, hits and spell_ended, and tag is that cast's.",
			string.Empty,
			new[]
			{
				F("summoner", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("summon", "uint32_t", "the summoned entity"),
				F("kind", "int32_t", "1 a Raise Dead minion, 2 a Blades sword"),
				F("serial", "uint32_t", "the summoning spell's serial (spell_created's, a hit's spell_serial)"),
				F("tag", "int32_t", "the tag of the cast whose serial it carries (spell_launch's, cast_spell's options.tag), 0 none"),
			}),
		new ModApiEvent("Crime", "crime", 64, "rex_CECCrime_Add (r3 crime component, r4 criminal, r5 victim, r6 type)",
			"A crime is recorded against someone. The co-op partner's crimes are their own. A reported murder also records an assault (type 3), raised again.",
			"the crime is not recorded: no witness record, no bounty, no guards",
			new[]
			{
				F("criminal", "uint32_t", "who committed it"),
				F("player", "int32_t", "1 or 2 when the criminal is a hero, else 0"),
				F("victim", "uint32_t", "the victim, 0 when none"),
				F("victim_player", "int32_t", "1 or 2 when the victim is a hero, else 0"),
				F("type", "int32_t", "the game's crime type (2 murder, 3 assault, 6 trespass, 11 vandalism among them)"),
			}),
		new ModApiEvent("Achievement", "achievement", 65, "rex_CECStats_UnlockAchievement (r3 stats component, owner at +4; r4 id)",
			"An achievement unlocks, for both heroes (scripts and the stats code both unlock here). Each hero earns its own: an unlock is the hero's (its character's) and also counts on the profile's total. Query a player's with achievement_unlocked / achievements / character_id.",
			"it stays locked",
			new[]
			{
				F("id", "int32_t", "the achievement, 1..50"),
				F("source", "uint32_t", "the entity whose stats asked"),
				F("player", "int32_t", "1 or 2 when the source is a hero, else 0"),
			}),
		new ModApiEvent("CollectableTaken", "collectable_taken", 66, "rex_CECInventory_AddEntity (coop_world_hooks.cpp), before the pickup",
			"A player picks up a one-time collectable kept per player in co-op (a silver key): each hero takes their own.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("entity", "uint32_t", "the collectable in the world"),
				F("record", "uint32_t", "its GDB record"),
				F("stays", "bool", "it stays in the world for the partner"),
			}),
		new ModApiEvent("PartnerConsent", "partner_consent", 67, "the consent ask (mod_lua_natives.cpp: the core beds and Refii.ask_partner)",
			"A co-op script asks the other player's consent before something that changes both players' game. A handler can answer for the partner.",
			string.Empty,
			new[]
			{
				F("ticket", "uint32_t", "the question, as Refii.partner_answer takes it"),
				F("kind", "int32_t", "1 sleep, 2 sex, 0 a mod's own question"),
				F("asker", "int32_t", "the asking player, 1 or 2"),
				F("detail", "int32_t", "the sleep type for kind 1"),
				F("answer", "int32_t", "-1 asks the partner (the default), 0 refuses, 1 agrees (writable)", true),
			}),
		new ModApiEvent("PropertyBought", "property_bought", 68, "rex_CECPlayerProperties_Buy (r3 the buyer's properties comp69, r4 the building), before",
			"A hero buys a building. Online it fires on the host, where the economy is (a joiner's purchase is run there).",
			"the building stays unbought and the gold where it is",
			new[]
			{
				F("hero", "uint32_t", "the buyer"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the building"),
				F("price", "int32_t", "what it costs, condition included (writable)", true),
			}),
		new ModApiEvent("PropertySold", "property_sold", 69, "rex_CECPlayerProperties_Sell (r3 the seller's comp69, r4 the building), before",
			"A hero sells a building; a building shared with a Fable 3 partner gives them half.",
			"it stays owned",
			new[]
			{
				F("hero", "uint32_t", "the seller"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the building"),
				F("price", "int32_t", "what the seller is paid (writable)", true),
			}),
		new ModApiEvent("RentPaid", "rent_paid", 70, "rex_CECPlayerProperties_PayIncome (r3 comp69, r4 drops, r5 away), after",
			"A hero's rent and shop profits were paid, every five real minutes (or the catch-up for time away, paid on the next screen).",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the owner"),
				F("player", "int32_t", "1 or 2"),
				F("gold", "int32_t", "the total paid"),
				F("drops", "int32_t", "how many five-minute drops it covers"),
				F("away", "bool", "the catch-up for time out of the game"),
			}),
		new ModApiEvent("PropertyRented", "property_rented", 71, "rex_CECPlayerProperties_SetRentedOut (r3 comp69, r4 the building, r5 rented), before",
			"A building is rented out, or its tenants are evicted.",
			"it stays as it was",
			new[]
			{
				F("hero", "uint32_t", "the owner"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the building"),
				F("rented", "bool", "rented out (false: evicted)"),
			}),
		new ModApiEvent("PropertyCondition", "property_condition", 72, "the condition rules (property_hooks.cpp: the rent payout and the repair)",
			"A building's condition (Fable 3's, 0..1) changed: a rented home wore down at a rent drop, or it was repaired. At 0 its tenants pay nothing.",
			string.Empty,
			new[]
			{
				F("uid", "uint64_t", "the building's UniqueID (\"0x…\" in Lua and Godot)"),
				F("player", "int32_t", "the owner, 1 or 2"),
				F("condition", "float", "now, 0..1"),
				F("previous", "float", "before"),
				F("repaired", "bool", "a repair (else wear)"),
			}),
		new ModApiEvent("Divorce", "divorce", 73, "rex_CECPlayerFamily_FinishDivorcing (r3 the family comp136, r4 the spouse), after",
			"A marriage ended: the spouse has left the hero.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("spouse", "uint32_t", "the former spouse"),
			}),
		new ModApiEvent("Widowed", "widowed", 74, "rex_CECPlayerFamily_Widow (r3 the family comp136, r4 &the spouse's UID), after",
			"A hero's spouse died or was removed: the household ends.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("uid", "uint64_t", "the late spouse's UniqueID (\"0x…\")"),
			}),
		new ModApiEvent("ChildBorn", "child_born", 75, "rex_CECPlayerFamily_AdoptWithSpouse (r3 the family comp136, r4 the spouse, r5 the child), after",
			"A child is born to (or adopted by) a hero and their spouse. Online it fires on the host.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the parent hero"),
				F("player", "int32_t", "1 or 2"),
				F("spouse", "uint32_t", "the other parent"),
				F("child", "uint32_t", "the child"),
			}),
		new ModApiEvent("GiftGiven", "gift_given", 76, "rex_lhGiftComponent_GiveGift (r3 the giver's comp33, r4 the item), before",
			"A hero gives a gift (a ring among them) to the one they target. It leaves their inventory and comes back after fifteen seconds unless the villager keeps it (gift_accepted). Fires on the giver's machine.",
			"nothing is given",
			new[]
			{
				F("hero", "uint32_t", "the giver"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("target", "uint32_t", "the one it is given to"),
				F("item", "uint32_t", "the item's record (\"0x…\")"),
			}),
		new ModApiEvent("GiftAccepted", "gift_accepted", 77, "rex_CECHeroGifts_RemovePermanently (r3 the giver's comp33), after",
			"A villager kept a hero's gift: it is theirs for good and the gift's morality applies. Online it fires on the host.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the giver"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("item", "uint32_t", "the item's record (\"0x…\")"),
			}),
		new ModApiEvent("Engaged", "engaged", 78, "rex_CECOpinionReaction_SetEngaged (r3 the villager's comp47, r4 engaged), before",
			"A villager becomes engaged to a hero (a ring accepted), or the engagement ends.",
			"it stays as it was",
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("player", "int32_t", "the hero it concerns, 1 or 2"),
				F("engaged", "bool", "engaged (false: the engagement ends)"),
			}),
		new ModApiEvent("Follow", "follow", 79, "rex_CECFollow_Start (r3 the follower's comp94, r4 the leader), before / rex_CECFollow_Stop (r3 comp94), after",
			"A creature starts or stops following someone: a hero's follower, a fiancé, a partner for the night.",
			"on a start: it does not follow",
			new[]
			{
				F("follower", "uint32_t", "the creature"),
				F("leader", "uint32_t", "who it follows"),
				F("player", "int32_t", "1 or 2 when the leader is a hero, else 0"),
				F("following", "bool", "starts (false: stops)"),
			}),
		new ModApiEvent("FurnitureUpgraded", "furniture_upgraded", 80, "rex_CECUpgradeableFurniture_SetLevel (r3 the piece's comp68, r4 the level), before",
			"A piece of furniture changes level while redecorating (with a furniture card). Online it fires on the host.",
			"it keeps its level",
			new[]
			{
				F("piece", "uint32_t", "the furniture"),
				F("player", "int32_t", "the player redecorating, 1 or 2"),
				F("level", "int32_t", "the new level (writable)", true),
				F("previous", "int32_t", "the level before"),
			}),
		new ModApiEvent("Redecorating", "redecorating", 81, "rex_CECBuilding_SetRedecorating (r3 the building's comp67, r4 on), after",
			"Redecorating a building starts or ends. Fires on the redecorating player's machine.",
			string.Empty,
			new[]
			{
				F("building", "uint32_t", "the building"),
				F("player", "int32_t", "the player redecorating, 1 or 2"),
				F("on", "bool", "starts (false: ends)"),
			}),
		new ModApiEvent("HouseholdBudget", "household_budget", 82, "rex_lhGuiRealEstateScreen_SetBudget (r3 the estate screen, r4 the gold), before",
			"A hero sets a household's budget at its home: the upkeep the family is given. Online it fires on the host.",
			"the budget stays",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the home"),
				F("gold", "int32_t", "the budget (writable)", true),
			}),
		new ModApiEvent("TrackedStat", "tracked_stat", 83, "rex_CECStats_ModifyTrackedStat (r3 the hero's stats comp57, r4 the stat, r5 the amount), before",
			"A hero's tracked stat counts (the game's ETrackedStatType: kills, sex, marriages, children, gold spent and many more; achievements and titles are judged from them). Online, player two's are counted on the joiner's machine.",
			"nothing is counted",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("stat", "int32_t", "the stat (ETrackedStatType)"),
				F("amount", "int32_t", "what is added (writable)", true),
			}),
		new ModApiEvent("Partnership", "partnership", 84, "the household tick (hero_partners.cpp): the two heroes' partnership changed",
			"Fable 3's player-to-player partnership between the two heroes changed. Fires on the host.",
			string.Empty,
			new[]
			{
				F("kind", "int32_t", "0 none, 1 business partners, 2 married"),
				F("previous", "int32_t", "the kind before"),
			}),
		new ModApiEvent("HouseholdRestored", "household_restored", 85, "the household tick (household_persist.cpp), after the restore",
			"Player two is back in the host's world: the household and property kept with the world were put back on their hero. Fires on the host.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "player two's hero"),
				F("player", "int32_t", "2"),
				F("spouses", "int32_t", "the spouses found in the world"),
			}),
		new ModApiEvent("DynamicWeather", "dynamic_weather", 86, "the guest tick, from the host's \"ModWeather:\" pump action (Engine_DynamicWeatherChoice's change)",
			"The dynamic weather in effect changed: the player's override, else the level's rule (fixed, interior, the game's own theme, or a roll that drifts every few minutes). One weather per co-op session: the host decides, an online joiner adopts the host's statement for its level; each player's sky switch only changes whether it is drawn. Fires on both machines.",
			string.Empty,
			new[]
			{
				F("level", "const char* / string", "the level the weather is for, \"<level>/<scenario>\" lower-case, as the statement names it"),
				F("weather", "const char* / string", "a preset (Clear, PartlyCloudy, Cloudy, Overcast, Rain, Storm, Snow, Fog, LightRain, LightSnow, Blizzard), \"theme\" (the game's own theme weather) or \"interior\""),
				F("previous", "const char* / string", "the weather before, the same way; empty for the session's first"),
				F("seconds", "float", "how long the change blends over (0: at once)"),
				F("from_host", "bool", "an online joiner adopting the host's statement; false where this machine decided it"),
			}),
		new ModApiEvent("ModMenuRow", "mod_menu_row", 87, "Godot's menu lists (Engine_UiModMenus), then the guest tick from the host's command queue (menu_row)",
			"A row of ANY list was chosen — the game's own menus included, a menu a mod defines in ui/templates.json, a row a mod patched into a shipped menu, any row whose command is Opens:<menu> or Mod:lua:<function> — or a ROW ACTION (ui/templates.json \"rowActions\": a button a mod puts beside a shipped screen list's page buttons) was pressed on the highlighted row, with menu the list's template name (ItemsMenu, WeaponsMenu, ExpressionsMenu, AbilitiesMenu) and the trailing fields command and button. A GODOT HANDLER MAY ANSWER IT on the dictionary it is handed: e[\"cancel\"] = true consumes the press (nothing is dispatched to the game or the list), and e[\"command\"] rewritten to another verb runs that verb instead of the row's own. Godot scripts get it as it happens; native handlers on the next guest tick, on the title and the front end too; Lua handlers on the next guest tick: in a game the init.lua ones, on the title and the front end the mod/frontend.lua ones (run in the current Gameface presentation's own Lua state).",
			string.Empty,
			new[]
			{
				F("menu", "const char* / string", "the menu the row is on: the mod menu's name, or the list's own (frontend-root, pause-root, a screen's population script)"),
				F("row", "const char* / string", "the row's name"),
				F("player", "int32_t", "1 or 2: the player whose menu it is"),
				F("mod", "const char* / string", "the mod that stated the row or its menu, empty for a shipped row"),
				F("command", "const char* / string", "the row's own command (Inventory:use:1234, Inventory:equip:…, Ability:learn:9, Shop:buy:7; the expression id on the expressions list), or a ROW ACTION's highlighted row's; a Godot handler may REWRITE it to another verb, which then runs instead"),
				F("button", "const char* / string", "a ROW ACTION's: the button pressed (X or Y); empty for a plain row choice"),
				F("subject_kind", "const char* / string", "the row's TYPED SUBJECT, read by the engine off the row's own command or a host list's row name (ModRowSubject) so no handler parses a verb: \"item\" (Inventory:use, inventory:usable), \"weapon\" (Inventory:equip), \"expression\" (an expression row, expressions), \"will\" (Ability:learn 7..14, spells_known); empty for none"),
				F("definition", "const char* / string", "the item's, weapon's or expression's record hash as \"0x%08x\" (exact, where a float would not be); empty for none and for a will"),
				F("spell", "int32_t", "a will's spell type 0..7 (0 Lightning .. 7 Dead Rising: the ability less 7), -1 for none"),
			}),
		new ModApiEvent("ModMessageBox", "mod_message_box", 88, "Godot's host boxes (Engine_UiModBoxes), then the guest tick from the host's command queue (box_answer)",
			"A message box a mod raised (refii_mod_message_box, Refii.message_box, RefiiHost.message_box) was answered. Godot scripts get it as it happens; native handlers on the next guest tick, on the front end too; Lua handlers in a game (init.lua) and on the front end (mod/frontend.lua).",
			string.Empty,
			new[]
			{
				F("id", "uint32_t", "what the call returned: the payload's ids are 1 .. 2^23-1, Godot's 2^23 .. 2^24-1"),
				F("button", "int32_t", "0 for A, 1 for B, -1 when another box replaced it or its player is played on the other machine"),
				F("player", "int32_t", "1 or 2: whose box it was"),
			}),
		new ModApiEvent("ModNotice", "mod_notice", 89, "Godot's notice channel (Engine_UiNotices, shown by GameUi), then the guest tick from the host's command queue (notice_done)",
			"A notice a mod posted (refii_mod_notice, Refii.notice, RefiiHost.notice) has gone. Godot scripts get it as it happens; native handlers on the next guest tick, on the front end too; Lua handlers in a game.",
			string.Empty,
			new[]
			{
				F("id", "uint32_t", "what the call returned: the payload's ids are 1 .. 2^23-1, Godot's 2^23 .. 2^24-1 (one id space with the message and menu boxes)"),
				F("outcome", "int32_t", "0 once it was shown and its time ran out (or it was dismissed on the front end); -1 when it never showed: its player is played on the other machine, or the queue overflowed"),
				F("player", "int32_t", "1 or 2: whose notice it was"),
			}),
		new ModApiEvent("ModMenuBox", "mod_menu_box", 90, "Godot's host menu box (Engine_UiModMenuBox), then the guest tick from the host's command queue (menu_box_answer)",
			"A menu box a mod raised (refii_mod_menu_box, Refii.menu_box, RefiiHost.menu_box) was answered. Godot scripts get it as it happens; native handlers on the next guest tick, on the front end too; Lua handlers in a game. A RefiiHost.message_box with more than two buttons is a menu box answered as mod_message_box instead.",
			string.Empty,
			new[]
			{
				F("id", "uint32_t", "what the call returned (one id space with the message boxes and notices)"),
				F("option", "int32_t", "the chosen option's index in the list the mod gave (paging is the box's own); -1 for B on a cancelable box, another box replacing it, or its player played on the other machine"),
				F("player", "int32_t", "1 or 2: whose box it was"),
			}),
		new ModApiEvent("ModAbility", "mod_ability", 91, "the engine's mod-ability purchase (src/vfs/src/hooks/mod_abilities.cpp), run on the guest tick from the book's ability_buy / ability_refund",
			"A player is buying or giving back a level of a mod ability (ui/abilities.json). The ENGINE runs the purchase from the declaration: the level is the player's saved value under levelKey, the price the game's own table of the ability costLike names, split evenly across the declared disciplines' pools (the first named takes the odd point) and spent through the game's own Spend; an unlearn (\"refund\": \"half\") gives half the given-up level's price back into General, as the shipped unlearn does. Raised before anything changes, only for a purchase that can happen. A mod applies the level's effect by reading the level from the player store where it is used.",
			"refuses it: nothing is spent and the level stands",
			new[]
			{
				F("ability", "const char* / string", "the ability's declared name"),
				F("mod", "const char* / string", "the mod that declared it"),
				F("refund", "bool", "true for an unlearn, false for a purchase"),
				F("previous", "int32_t", "the level held"),
				F("level", "int32_t", "the level it will hold"),
				F("experience", "int32_t", "the price to be spent (a purchase) or what comes back into General (an unlearn)"),
				F("player", "int32_t", "1 or 2: whose book it is (the level, the hero and the experience are theirs)"),
			}),
		new ModApiEvent("SpellPick", "spell_pick", 92, "a mod's pick: the spell selectors (Engine_UiSpellPicks), then the guest tick from the host's command queue (spell_pick); the game's own pick: the guest's slot writer CECSpellManager::SetSpellSlot (TU1 sub_82541F30) when it is called as a choice",
			"A player chose or cleared a pick on one of their spell slots. A mod's pick (ui/spellwheel.json, Spell Weaving's partner) is kept per player and slot as a saved player value under the pick's store key, written after the handlers unless one refuses it. The game's own pick is the slot's spell (pick \"spell\", mod \"\"): every assignment the game makes as a choice raises it before the slot is written — each step of the selectors' carousel, the scripts' spell-table natives, the host's own assignment — and a changed spell is the one stored; the template's defaults, the other machine's pick arriving and a save load are not picks. Raised on the machine whose player made the pick; Godot scripts get it from the bus.",
			"keeps the slot's pick as it was",
			new[]
			{
				F("pick", "const char* / string", "the pick's name as ui/spellwheel.json states it; \"spell\" for the game's own pick"),
				F("mod", "const char* / string", "the mod that stated the pick; empty for the game's own"),
				F("slot", "int32_t", "the slot, 0..4"),
				F("spell", "int32_t", "the will chosen, 0 Lightning .. 7 Dead Rising; -1 cleared (writable: another will, or -1)", true),
				F("previous", "int32_t", "the will held before, -1 none"),
				F("player", "int32_t", "1 or 2: whose selectors"),
			}),
		new ModApiEvent("PartnerMessage", "partner_message", 93, "the receiving machine's guest tick, from the route's mod lane (core/event_relay.h pm: lines; mod_events.cpp DrainPartnerMessages), after the other machine's refii_mod_send_to_partner / Refii.send_to_partner / RefiiHost.send_to_partner",
			"A mod's own message from THE OTHER MACHINE of an online session. Carried on the route — the ordered, acknowledged lane the game's own cross-machine calls take — so it arrives once, in the order sent, and waits out a load or a pause; never raised on the sender's machine, never alone or in local co-op. Not a replica: it is this machine's own handling, so player-state writes in a handler count. Godot scripts get it from the bus like any other event.",
			string.Empty,
			new[]
			{
				F("mod", "const char* / string", "the mod that sent it (its name)"),
				F("name", "const char* / string", "the message's word, as the sender named it (letters, digits, '_', '.', '-'; at most 32)"),
				F("payload", "const char* / string", "its text, at most 256 bytes; on the Lua and Godot surfaces a quote, backslash or line break in it arrives as '_'"),
				F("from", "int32_t", "the player the sending machine plays: 1 the host, 2 the joiner"),
			}),
		new ModApiEvent("LookAtButton", "look_at_button", 94, "rex_CPlayer_SetLookAtButtonDown (r3 the player component 33, owner at +4; r4 the value), before it",
			"A player hero's look-at flag is being written — the byte every lock camera script reads, what a held LT states: by the camera's input event on LT down (1), the hero's message handler on the release (0), or the API's own lock_target / release_lock. A mod whose lock is its own writes down to keep the flag equal to it, whoever asks.",
			"the flag stays as it was (the writer never runs; the camera mode is not re-picked)",
			new[]
			{
				F("hero", "uint32_t", "the hero whose flag it is"),
				F("player", "int32_t", "1 or 2"),
				F("down", "bool", "the value being written (writable)", true),
				F("component", "uint32_t", "the player component the flag lives on (native)"),
			}),
		new ModApiEvent("VortexStarted", "vortex_started", 95, "rex_VortexComponent_Init (r3 the component 211, owner at +4; r4 caster, r5 target, r6 serial -> +0x18, r8 level -> +0x14), after it — inside the spell factory, before spell_created; lifts is answered in rex_VortexComponent_Classify (TU1 sub_8275EB10 / disc sub_827364F0) while that vortex's update runs",
			"A vortex is live: its component was initialised on the vortex entity by the vortex spell's post-init, inside the factory and before spell_created reports the spell object, which finishes at once (no registry lookup ever returns a vortex). spell_parts(serial) finds the component again while it lives. Every half second its update asks how it takes each creature in reach — lift it (the capture, vortex_caught) or shudder it, the one hit per cast it gives a creature it cannot lift (a hero, or a creature not flagged liftable); lifts = false answers 'cannot lift' for every creature, so this vortex picks none up and its shudder hit lands on each creature it passes once per cast, carrying the cast's serial and tag (props are lifted as before). Online the cast runs on both machines (replica on the copy), and lifts holds on both: the machine deciding the caster states it, and the core carries its answer to the other machine's copy through the cast pairing. Until the pair forms the copy lifts no creature (its half-second check passes over them), so it starts lifting at most one check after the pair; a copy whose cast cannot pair follows its own lifts after the eight seconds an unpaired cast is kept.",
			string.Empty,
			new[]
			{
				F("vortex", "uint32_t", "the live component (native)"),
				F("entity", "uint32_t", "the vortex entity, the thing in the world the look rides"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("target", "uint32_t", "the target entity, or 0"),
				F("serial", "uint32_t", "the cast's number (the spell object's +0x44), carried by its hits"),
				F("level", "int32_t", "the power 0..4"),
				F("x", "float", "the vortex entity's position"),
				F("y", "float", "the vortex entity's position"),
				F("z", "float", "the vortex entity's position"),
				F("tag", "int32_t", "the cast's tag (spell_launch's), 0 none"),
				F("damage", "float", "the damage of one of the vortex's hits on a creature it holds, before that creature's damage factor (the component's +0x38, from the spell's data at Init)"),
				F("lifts", "bool", "whether the vortex lifts creatures, true as the game has it (writable): false lifts none and shudders each creature it reaches once per cast instead; online the deciding machine's answer holds on both machines", true),
			}),
		new ModApiEvent("VortexEnded", "vortex_ended", 96, "rex_VortexComponent_Shutdown (r3 the component), before it",
			"The vortex is ending: its component's Shutdown, with the twister's last point (the component's +0x140, the vector its update hands every ground effect and places the entity at). The look holds where the twister ended, not where the cast began.",
			string.Empty,
			new[]
			{
				F("vortex", "uint32_t", "the component (native)"),
				F("entity", "uint32_t", "the vortex entity"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("serial", "uint32_t", "the cast's number"),
				F("x", "float", "the twister's point"),
				F("y", "float", "the twister's point"),
				F("z", "float", "the twister's point"),
			}),
		new ModApiEvent("FireballImpact", "fireball_impact", 97, "rex_TargetedFireball_Impact (r3 the spell object, r4 what it struck or 0, v1 position, v2 normal), after it, when its exploded-byte guard passed",
			"A targeted fireball struck something: its Impact ran. detonated is the exploded byte (spell+0x48) going up inside the call — the blast delivered every victim's hit inside it and the fireball is spent; a creature the fireball passes and ignites leaves it clear and the fireball flies on. hits counts the hits the receiver took inside the call carrying this cast's serial: 0 on a detonation means the blast found nothing alive. Online the cast runs on both machines; the hits are decided where world_here holds.",
			string.Empty,
			new[]
			{
				F("spell", "uint32_t", "the spell object (native)"),
				F("serial", "uint32_t", "the cast's number"),
				F("level", "int32_t", "the power 0..4"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("struck", "uint32_t", "the entity struck, 0 for ground, wall or water"),
				F("struck_player", "int32_t", "1 or 2 when a hero was struck, else 0"),
				F("x", "float", "the impact position"),
				F("y", "float", "the impact position"),
				F("z", "float", "the impact position"),
				F("nx", "float", "the impact normal"),
				F("ny", "float", "the impact normal"),
				F("nz", "float", "the impact normal"),
				F("detonated", "bool", "the blast went off inside this call"),
				F("hits", "int32_t", "hits delivered inside the call carrying this serial"),
				F("tag", "int32_t", "the cast's tag (spell_launch's), 0 none"),
			}),
		new ModApiEvent("Paused", "paused", 98, "rex_lhGameFlowGuiMode_Apply / rex_lhGameFlowGuiMode_Remove (r3 the GameFlow mode command; its id by vtable slot 1), after each",
			"The game's pause state flipped: a GameFlow mode command of the pause family became the mode in force (its apply: both players' input categories applied, the world held behind it — what rex_lhGameFlow_IsGuiActive answers), or stopped being it (its remove). Mode 1 is the GUI / pause command (the pause menu and every screen entered from play, the partner's relayed pause, a camera mode); mode 2 a level change. Raised again when a command above it is removed (the mode re-applied) and when one is pushed over it (removed for its length).",
			string.Empty,
			new[]
			{
				F("paused", "bool", "true after the apply (the mode is in force), false after the remove"),
				F("mode", "int32_t", "the command's id: 1 the pause / menu mode, 2 a level change"),
				F("player", "int32_t", "for mode 1 with a menu screen up: whose menu (2 on an online joiner or for local player two's pause book, else 1); 0 for a pause with no screen (the partner's relayed one, a camera mode) and for mode 2"),
				F("screen", "int32_t", "the GUI manager's screen id then, TU1 numbering (31 none, 0 the pause root, 29 co-op); -1 when the in-game GUI is not up"),
			}),
		new ModApiEvent("StartGame", "start_game", 99, "rex_lhFrontEnd_SetStartGameTarget (r3 the front-end object, r4 the save name LHString, r5 the start level LHString), before it",
			"The front end starts a game: New Game, Load, Continue or a co-op join stated the target the in-game state is created from. On the title there is no game VM, so Lua hears it in mod/frontend.lua; native handlers and Godot scripts as any event.",
			"the start is refused and the front end stays where it is (the save gate's own use, Engine_ModSaveGate: a save stamped with mods not enabled now is asked about, filtered, then started again)",
			new[]
			{
				F("save", "const char* / string", "the save's name; \"\" for a new game"),
				F("level", "const char* / string", "the level the game starts in"),
				F("new_game", "bool", "no save: a new game"),
				F("player", "int32_t", "the signed-in pad's player: 1"),
				F("commit", "const char* / string", "the host action the start was dispatched from, the guest-tick pump's name for it (LoadSave:N, MenuEvent:…, the root's bare row name); \"\" for a start the pump did not run. A refusing handler posts it again to make the same start once its question is answered"),
			}),
		new ModApiEvent("Cutscene", "cutscene", 100, "polled each guest tick before tick (no hook): GUI.IsFullCutscenePlaying (sub_823060E0 / disc sub_82907D08); the player component's +0x3B (Player.SetForegroundCutsceneInEffect sub_823E72B8 / sub_823C5CE8) and +0xC8 (Player.SetOverrideCameraMode's LOOK_AT scope, sub_823E31B8 / sub_823C1B80)",
			"A cutscene took a hero's camera and controls away from free play, or gave them back — one of the game's own three statements flipped, raised per kind on each edge: " + CutsceneKindsMeaning +
			". Raised before the tick event, so a tick handler already sees the new state; in_cutscene answers the same live at any moment. With no listener nothing is read, so a handler subscribed mid-scene hears the scene start on its first tick.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2; 0 for a full cutscene (every player's)"),
				F("active", "bool", "true on the rising edge, false on the falling one"),
				F("kind", "int32_t", "the statement that flipped: 1 full, 2 interactive, 4 look-at (REFII_CUTSCENE_*)"),
				F("kinds", "int32_t", "every statement holding for the player after this edge, as those bits (for player 0 the full cutscene's)"),
				F("hero", "uint32_t", "the player's hero (0 for player 0, or once the hero is gone)"),
			}),
		new ModApiEvent("HealthChanged", "health_changed", 101, "rex_CECHealth_Modify (r3 health component 36, owner at +4; f1 delta; r5 the source), after it, when current health (+0x14) moved",
			"An entity's health changed through the game's own Modify: a hit's damage, a potion, a heal, modify_health. delta is what landed after the clamp to 0..max (a heal at full health is not reported; damage the game refuses — a protected hero — moves nothing and raises nothing). Online the game's copy on the machine not authoritative for the entity is flagged a replica.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "whose health"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("delta", "float", "the change that landed: positive a heal, negative damage"),
				F("health", "float", "health after it"),
				F("max", "float", "the maximum"),
				F("source", "uint32_t", "the entity the change is attributed to (an attacker, a healer), 0 for none"),
			}),
		new ModApiEvent("StatusApplying", "status_applying", 102, "rex_SpellLightning_Electrify (r3 source, r4 victim, r5 serial, r6 level, f1 duration) / rex_SpellChaos_ApplyToVictim (r3 source, r4 victim, r5 serial, f1 seconds, r7 is-creature, r8 replica) / rex_SpellFireball_Ignite (r3 victim, r4 source, r5 serial, r6 level, f3 burn time), before them",
			"A spell's status is about to be put on an entity (1 electrified, 2 chaos, 3 burning), through the installer the spell itself calls — a cast's hit, apply_status. seconds is writable; the installer may still refuse (an entity not in the world, the world's spell-count gate). Chaos fires where it is decided: never for the network receive's copy, and a written seconds is what the game sends the partner.",
			"nothing is put on (chaos sends nothing to the partner)",
			new[]
			{
				F("entity", "uint32_t", "the victim"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("effect", "int32_t", "1 electrified, 2 chaos, 3 burning (status_effect's numbering)"),
				F("serial", "uint32_t", "the cast's serial (a hit's spell_serial); 4294967295 for none (apply_status)"),
				F("source", "uint32_t", "who put it on (the caster), 0 for none"),
				F("level", "int32_t", "the spell level the component records, 0..4; -1 for chaos (it states none)"),
				F("seconds", "float", "how long it lasts (writable, > 0)", true),
				F("tag", "int32_t", "the tag of the cast whose serial it carries (spell_launch's, cast_spell's options.tag), 0 none"),
			}),
		new ModApiEvent("SaveWritten", "save_written", 103, "rex_lhSaveManager_WritePackage / rex_lhSaveManager_WriteHeroPackage (r4 the package's name, r5 the player whose hero it holds; r3 the result), after them",
			"A save package was written, or the write failed: the full write (main save, hero save, chapter) and player two's hero save alone, each with the player whose hero it holds. Godot's signal is raised after the host stamped the package's sidecar, so save_snapshot of the new save works from it.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "the player whose hero the package holds, 1 or 2"),
				F("save", "const char* / string", "the package's name, its folder (Hero000); \"\" when the game named none"),
				F("ok", "bool", "the writer's own result: false for a failed write"),
			}),
		new ModApiEvent("LockLost", "lock_lost", 104, "rex_CPlayer_ClearPlayerMode (r3 the player's mode handle, comp33+0x14; r4 the mode), for mode 4 TARGET_LOCK, when the game's own HasPlayerMode (sub_821E8B90 / disc sub_821E8870) held it before the call and not after",
			"A player's target lock ended: the game dropped its own TARGET_LOCK mode — whichever of its drops ended it (the targeting update when the force byte was released — release_lock —, its range check when the target died or left reach, the network's replication of the partner's). Raised at the drop, only when the mode was really held. Read-only; online the run on the machine not deciding the hero is flagged a replica.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("hero", "uint32_t", "the player's hero"),
				F("target", "uint32_t", "the target the targeting component still holds after the drop, 0 for none"),
			}),
		new ModApiEvent("AttackModeChanged", "attack_mode_changed", 105, "host state (GameInput's attack mode), stated by Godot with the command attack_mode P MODE and raised on the next guest tick (no seam)",
			"The keyboard's attack mode changed for a player: which pad button the attack key presses — 1 melee (X), 2 ranged (Y), 3 magic (B) — set by the mode keys (Fable III's 1, 2, 3). Raised on the machine whose keyboard plays the player; attack_mode(p) reads the mode at any time.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2: the player the keyboard plays"),
				F("mode", "int32_t", "1 melee, 2 ranged, 3 magic (Refii.ATTACK_MODE_*); 0 when no keyboard plays the player here any more"),
			}),
		new ModApiEvent("ItemRemoved", "item_removed", 106, "rex_CECInventory_RefreshRecordRefs (r3 the inventory component, r4 the definition), after it: the tail RemoveItem's body (sub_824F12A0) and the by-category loops (sub_824F1910, sub_824F1650) run once per unit taken",
			"One unit left an inventory — used up, sold, given, dropped, taken by a quest or by remove: whatever path took it ran the game's own removal, which calls this tail once per unit. Read-only: the unit is gone.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "whose inventory"),
				F("player", "int32_t", "1 or 2 for a hero's inventory, else 0"),
				F("inventory", "uint32_t", "the inventory component (13)"),
				F("definition", "uint32_t", "the item's definition (GDB record hash)"),
				F("count", "int32_t", "units this removal took: 1"),
				F("remaining", "int32_t", "how many of the definition the inventory still holds"),
			}),
		new ModApiEvent("ExpressionWheel", "expression_wheel", 107, "rex_lhGuiManager_OpenExpressionWheel (r3 the quick menu, r4 the player record), after it / rex_lhGuiManager_CloseExpressionWheel (r3 the quick menu), before it; the highlight polled each guest tick while the wheel is up (the HUD's own reader)",
			"A player's expression wheel opened, closed, or highlighted a different expression. Open and close are the game's own calls; while the wheel is up each change of highlight is one event with open true. Read-only.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2: whose wheel"),
				F("open", "bool", "true while the wheel is up (the open and each highlight change), false for the close"),
				F("highlighted", "uint32_t", "the expression record the wheel highlights (what perform_expression takes), 0 for none or closed"),
			}),
		new ModApiEvent("SpellEnded", "spell_ended", 108, "rex_CSpellRegistry_Update (r3 the registry), after it; each spell it stops and erases read before its Stop by the midasm hooks RefiiSpellRegistryUpdating / RefiiSpellRegistryEnding (r31 the spell)",
			"A spell ended: the spell registry's update stopped it and took it out of the registry — the one place the game removes a live spell. reason 0 the spell's own update finished it this pass (its time ran out, or its own logic), 1 it was stopped from outside before the pass (the caster's spells stopped, a re-cast replacing it). Raised after the update. Read-only; online the game's copy on the machine not deciding the caster is flagged a replica.",
			string.Empty,
			new[]
			{
				F("serial", "uint32_t", "the registry's serial: spell_created's, a hit's spell_serial, summoned's"),
				F("type", "int32_t", "the spell type, 0 Lightning .. 7 Dead Rising (Refii.SPELL_*)"),
				F("caster", "uint32_t", "the caster, 0 for none or gone"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("tag", "int32_t", "the tag of the cast whose serial it carries (spell_launch's, cast_spell's options.tag), 0 none"),
				F("reason", "int32_t", "0 finished by its own update, 1 stopped from outside (REFII_SPELL_END_*)"),
				F("level", "int32_t", "the spell's level, 0..4"),
			}),
		new ModApiEvent("SpellChargeEnded", "spell_charge_ended", 109, "rex_CAction_End (r3 the action), before it, for the magic charge's two actions (quick / full charge) once started and not yet ended; released when inside rex_CAbilityActionMagicCast_Start (r4 the entity)",
			"A hero's magic charge ended — released into a cast (the magic release ability took the charge over: its spell_launch / spell_launched follow) or not (a hit's reaction, a roll, the charge refused). Raised once per charge, on the machine that plays the hero. Read-only.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("caster", "uint32_t", "the hero"),
				F("released", "bool", "true when the magic button was released into a cast"),
			}),
		new ModApiEvent("MeleeSwingEnded", "melee_swing_ended", 110, "rex_CAction_End (r3 the action), before it, for the strike action rex_CAbilityActionMelee_CommitSwing committed, once started and not yet ended",
			"A committed strike ended. hits counts the hit_landed deliveries whose attacker was the striker while that strike was its current action — this machine's own and, online, the copies relayed from the machine deciding hits (one arriving after the end is not counted); 0 is a whiff. Read-only.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the striker"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("action", "uint32_t", "the strike action melee_swing named (Lua and Godot: a handle; 0 in a relayed copy)"),
				F("action_type", "int32_t", "the action's EScriptableAction (action_type), -1 unknown"),
				F("hits", "int32_t", "how many hits the strike landed"),
				F("strike", "int32_t", "melee_swing's strike for this strike"),
				F("flourish", "bool", "melee_swing's flourish for this strike"),
			},
			RelayedFromPlayer),
		new ModApiEvent("ActionMoveInterrupt", "action_move_interrupt", 111, "rex_CAction_MovementInterruptible (r3 the action handle), after it, when it answered yes",
			"Movement may interrupt an entity's current action: the game's own test answered yes (the action holds no interrupt veto and an ALLOW_MOVEMENT_INTERRUPT anim event allowed it — a cast's waits for its PERFORM). The player's control update asks every frame of that window: while it answers no a moved stick is withheld from the body, when it answers yes a moved stick stops the action. Cancel to hold the action — the animation plays on and the stick neither cancels it nor moves the body — for as long as a handler cancels.",
			"the action holds: movement does not interrupt it (the stick is withheld from the body)",
			new[]
			{
				F("entity", "uint32_t", "whose action"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("action", "uint32_t", "the action (Lua and Godot: a handle, what current_action answers)"),
				F("action_type", "int32_t", "the action's EScriptableAction (action_type), -1 unknown"),
			}),
		new ModApiEvent("CameraChanged", "camera_changed", 112, "the game VM: the core camera chunk after each CameraManager:FrameUpdate(hero), when the hero's CurrentCamera (the table the update handed rex_lhCameraManager_ActivateCamera) maps to another mode in the override set's CameraModes",
			"The camera the game has active for a player changed: the camera manager's own pick among its sets (debug, override, multiplayer, normal), as ECameraMode — 2 GAME (the third-person camera), 3 RANGED_AIM, 4 MAGIC_AIM, 7 ICS (an interactive scene's), 8 FIRST_PERSON, 9 COMBAT_CANNED_ACTION, 10 CANNED_ACTION ... (Refii.CAMERA_*). Raised on the machine that runs that player's camera. Read-only; camera_mode(p) reads it at any time.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("mode", "int32_t", "the ECameraMode now active (Refii.CAMERA_*), -1 for a camera the override set does not list"),
				F("previous", "int32_t", "the mode before, -1 for none stated yet"),
				F("hero", "uint32_t", "the player's hero"),
			}),
		new ModApiEvent("VortexCaught", "vortex_caught", 113, "rex_VortexComponent_Capture (r3 the component 211, r4 the victim; the update's one call after appending the victim to its held list at +0x1B0) / rex_VortexComponent_Release (r3 the component, r4 the victim; the update's drop, the per-victim release and Shutdown), after each; serial from the component's +0x18",
			"A vortex caught an entity (caught true) or let one go (caught false), whether or not it ever hit it: the vortex's own capture — the update's one call for every entity it takes, the creatures within its creature cap and the props it lifts within the world's prop cap alike (a creature-only rule tests the victim: creature_tier non-nil) — which puts the in-vortex status on the victim (that status_effect kind 4 carries the cast's serial and the caster as source), and its release, every way one ends — dropped from the twister, released on its own, or the vortex shutting down. Carries the cast's tag. vortex_held(serial) lists who it holds now. Online the cast runs on both machines (replica on the copy).",
			string.Empty,
			new[]
			{
				F("serial", "uint32_t", "the cast's number (the component's +0x18), as its vortex_started and hits carry it"),
				F("tag", "int32_t", "the cast's tag (spell_launch's), 0 none"),
				F("vortex", "uint32_t", "the live component (native)"),
				F("entity", "uint32_t", "the vortex entity"),
				F("victim", "uint32_t", "the creature caught or let go"),
				F("player", "int32_t", "the victim's player: 1 or 2 for a hero, else 0"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("caught", "bool", "true on capture, false on release"),
			}),
		new ModApiEvent("AppearanceChanged", "appearance_changed", 114, "character_morph_hooks.cpp's tick, when the look Godot published for the player from the player store's character_morphs.* set (refii.character_morphs.p<N>.*) takes over: the morph hook answers the weight function sub_822DA028 from it for that hero",
			"The look in force for a player's hero changed (Murgo's Mirror): manual came on or off, or a manual input moved — the Mirror's Apply or preview, or a mod's set_appearance / set_appearance_automatic, on either machine. Raised on every machine (the look is the player's store, the same on both). Not raised while automatic stats move (that is the game's own look). Read-only; appearance(p) reads the look at any time.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("automatic", "bool", "true when the game's own stats drive the look again, false when the stated manual inputs do"),
				F("hero", "uint32_t", "the player's hero, 0 while it has none"),
			}),
		new ModApiEvent("MorphChanged", "morph_changed", 115, "character_morph_hooks.cpp's tick, when the weight refii_mod_morph_weight answers for a player and a mod morph moved (the player store's morph.<id>, or the registry Godot published as refii.morphs.*): the morph hook answers the weight function sub_822DA028 with it for that player's hero and redraws it",
			"A mod morph's weight changed for a player: the Mirror's mod sliders, or a mod's morph_set on either machine (the weight is the player's store, the same on both), or the enabled mods' morphs.json changing what it answers. Raised on every machine; the hero is redrawn (its appearance flushed, its texture layers rebuilt). Read-only; morph_get(p, morph) reads the weight at any time.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("morph", "uint32_t", "the morph's id, refii_mod_morph_id(\"<mod>.<name>\") (\"0x…\" to Lua and Godot, as morph_id gives it)"),
				F("weight", "float", "the weight the hook answers now: the stored one clamped to the declared range, else the default"),
				F("hero", "uint32_t", "the player's hero, 0 while it has none"),
			}),
		new ModApiEvent("GuildSeals", "guild_seals", 116, "Fable 3: Fable3/src/hooks/src/mod_events_hooks.cpp — the stats component's guild-seal grant and spend, and their recompute (seals earned from renown), each compared before and after",
			"Fable 3 only. A hero's unspent guild seals changed: granted directly (a quest, a script), spent on the Road to Rule, or earned as renown passed a threshold. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2, 0 for another entity"),
				F("delta", "int32_t", "the change in unspent seals"),
				F("seals", "int32_t", "the unspent seals after it (Stats.GetNumberOfGuildSeals)"),
				F("source", "const char* / string", "'granted', 'spent' or 'earned'"),
			}),
		new ModApiEvent("Treasury", "treasury", 117, "Fable 3: Fable3/src/hooks/src/mod_events_hooks.cpp — the kingdom treasury's writer, compared before and after (Treasury.Set writes directly and is not seen)",
			"Fable 3 only. The kingdom's treasury changed: tax, a transfer to or from the hero's purse, the cost or gain of a decision. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("treasury", "int32_t", "the treasury after the change"),
				F("delta", "int32_t", "the change"),
				F("type", "int32_t", "the game's change type (0 tax, 1 transfer in, 2 a demand's gain, 15 a demand's cost, 17 transfer out)"),
			}),
		new ModApiEvent("KingdomDecision", "kingdom_decision", 118, "Fable 3: Fable3/src/hooks/src/mod_events_hooks.cpp — a town demand's accept and reject, after the call, when it was taken",
			"Fable 3 only. A royal decision on a town demand was taken, accepted or rejected, and its effects ran. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("demand", "uint32_t", "the demand's TownDemandId"),
				F("province", "uint32_t", "its ProvinceId"),
				F("level", "uint32_t", "its LevelId"),
				F("accepted", "bool", "true accepted, false rejected"),
			}),
		new ModApiEvent("ChestOpened", "chest_opened", 119, "Fable 2: the chest's Open (rex_CECChest_Open, r3 the chest component 40) with the opener from the open action (rex_CAction_OpenChest_Update); Fable 3: Lua_Chest_Open (component 47) and CAction_OpenChest's slot 24",
			"A chest opened: a world chest, a silver-key chest, a demon door's reward, on Fable 3 a Road to Rule chest. The opener is the hero whose open action opened it, 0 when a script opened it. Read-only (the contents are the script's).",
			string.Empty,
			new[]
			{
				F("chest", "uint32_t", "the chest"),
				F("opener", "uint32_t", "who opened it, 0 for a script"),
				F("player", "int32_t", "1 or 2 when the opener is a player's hero, else 0"),
				F("keys", "int32_t", "the silver keys it needs"),
				F("seals", "int32_t", "Fable 3: the guild seals it costs the opener (a Road to Rule chest); 0 elsewhere"),
				F("first", "bool", "Fable 3: it was never opened before"),
			}),
		new ModApiEvent("ItemBought", "item_bought", 120, "Fable 2: the shopkeeper's buy (rex_CECShopkeeper_Buy, r3 shopkeeper, r4 the item, r5 out, r6 the buyer); Fable 3: CECShopkeeper::Buy (same registers) and the steal from a closed shop",
			"A hero bought an item from a shop (one event per item: clothes, dyes, tattoos, weapons, titles, potions). On Fable 3 a theft from a closed shop is one too, stolen. The price is what the shop charges.",
			"the purchase does not happen: no gold, the item stays in the shop (Fable 3; on Fable 2 it is reported only)",
			new[]
			{
				F("shopkeeper", "uint32_t", "the shopkeeper, 0 for a theft"),
				F("buyer", "uint32_t", "the buyer"),
				F("player", "int32_t", "1 or 2 when the buyer is a player's hero, else 0"),
				F("definition", "uint32_t", "the item's record (\"0x…\")"),
				F("price", "int32_t", "the gold charged"),
				F("stolen", "bool", "Fable 3: taken from a closed shop"),
			}),
		new ModApiEvent("ItemSold", "item_sold", 121, "Fable 2: the shopkeeper's sell (rex_CECShopkeeper_Sell, r3 shopkeeper, r4 the item, r5 the seller); Fable 3: CECShopkeeper::Sell (r6 sell all, r7 the count)",
			"A hero sold an item to a shop (Fable 3: a pawn shop, one item or the whole stack).",
			"the sale does not happen: no gold, the item stays with the seller (Fable 3; on Fable 2 it is reported only)",
			new[]
			{
				F("shopkeeper", "uint32_t", "the shopkeeper"),
				F("seller", "uint32_t", "the seller"),
				F("player", "int32_t", "1 or 2 when the seller is a player's hero, else 0"),
				F("definition", "uint32_t", "the item's record (\"0x…\")"),
				F("price", "int32_t", "the gold paid for one"),
				F("count", "int32_t", "how many were sold"),
			}),
		new ModApiEvent("ShopPrice", "shop_price", 122, "the shopkeeper's BuyPrice and SellPrice (Fable 2: r3 shopkeeper, r4 the item; Fable 3: r3 shopkeeper, r4 the customer, r5 the item), after each",
			"A shop priced an item: the price shown, the can-afford test and the price paid all ask the same function, so a written price is the shop's price everywhere. Asked often while a shop screen is open.",
			string.Empty,
			new[]
			{
				F("shopkeeper", "uint32_t", "the shopkeeper"),
				F("customer", "uint32_t", "Fable 3: the customer; Fable 2: the hero whose shop menu is open"),
				F("player", "int32_t", "1 or 2 for a player's hero, else 0"),
				F("definition", "uint32_t", "the item's record (\"0x…\")"),
				F("kind", "const char* / string", "'buy' or 'sell'"),
				F("price", "int32_t", "the gold (writable, at least 0)", true),
			}),
		new ModApiEvent("DigSpotDug", "dig_spot_dug", 123, "the digging spot's OnDug (r3 the spot's component, the spot at +4; r4 the digger), after it",
			"A digging spot was dug out, by a hero's spade or a dog. What it yields is the calling script's.",
			string.Empty,
			new[]
			{
				F("spot", "uint32_t", "the digging spot"),
				F("digger", "uint32_t", "who dug it"),
				F("player", "int32_t", "1 or 2 when the digger is a player's hero, else 0"),
				F("by_dog", "bool", "a dog dug it"),
				F("criminal", "bool", "digging it is a crime (a grave)"),
			}),
		new ModApiEvent("DemonDoor", "demon_door", 124, "Fable 2: DemonDoor.Open; Fable 3: DemonDoor's SpawnFace, DespawnFace and Open (its effect tail, reached only when the door opens)",
			"A Demon Door changed: its face came out ('face_out', Fable 3), went back ('face_in', Fable 3) or the door opened ('opened'). The game does not state who opened it.",
			string.Empty,
			new[]
			{
				F("door", "uint32_t", "the door"),
				F("state", "const char* / string", "'face_out', 'face_in' or 'opened'"),
				F("player", "int32_t", "always 0: the game does not state who"),
			}),
		new ModApiEvent("Drink", "drink", 125, "PlayerDrunkenness.Drink (r3 the drunkenness component, the hero at +4; f1 the amount: `fadds` into +0x10, +0x1C counted)",
			"A hero drank: the amount goes into the beer reservoir and the drink is counted.",
			"the drink does not count: no alcohol, no count",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("amount", "float", "the alcohol (writable)", true),
				F("drinks", "int32_t", "the drinks counted before this one"),
			}),
		new ModApiEvent("Sanctuary", "sanctuary", 126, "Fable 3: Fable3/src/hooks/src/mod_events_hooks.cpp — the game-component switch manager's arrival (TU3 0x8239E198 / disc 0x82447758, r3 the manager: +0 the reason, +4 the entity's smart pointer), after it, when the current component's side changed (its slot 6, IsGUIActive)",
			"Fable 3 only. The hero arrived in the Sanctuary (entered) or back in the world (not entered): every completed switch, by START, a HUD shortcut, the world map, the Sanctuary's exits, a load or a quit. Reported when the side changes, after the switch, not cancellable (sanctuary_request decides the entry).",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the entity the switch was asked for (the world's hero), 0 for none"),
				F("player", "int32_t", "1 or 2 when that entity is a player's hero, else 0"),
				F("entered", "bool", "true: now in the Sanctuary; false: back in the world"),
				F("reason", "int32_t", "the game's EGUIEntryReason: 1 save select, 2 main menu (START), 3 dog, 4 furniture upgrade, 5 clothing, 6 weapons, 7 trading, 8 gift giving, 9 multiplayer room, 10 world map, 11 exit initialise (a load), 12 exit, 13 exit to the front end, 14 exit to save select, 15 quit, 16..19 quit to load / fail-quest load / join / PvP; TU3 only: 21 the world map's close with a pending stop"),
			}),
		new ModApiEvent("SanctuaryRequest", "sanctuary_request", 127, "Fable 3: Fable3/src/hooks/src/mod_events_hooks.cpp — GameComponentSwitchManager.SwitchToNextComponent (TU3 0x8239D5A8 / disc 0x82446CE8: r3 the manager, r4 the reason, r5 the entity), before it, unless a switch is already under way (the game's own gate, game+0xBB, refuses that one itself)",
			"Fable 3 only. A switch to the other component is asked for: into the Sanctuary from the world (to_sanctuary: START, a HUD shortcut to clothing or weapons, the world map, trading, gift giving, the dog) or back out of it (exits, loads, quits, restarts).",
			"an ENTRY does not happen (to_sanctuary with reason 1..10): the hero stays in the world. A leave, a load, a quit or a restart (reason 11 and up) is never refused",
			new[]
			{
				F("hero", "uint32_t", "the entity the switch is asked for, 0 for none"),
				F("player", "int32_t", "1 or 2 when that entity is a player's hero, else 0"),
				F("reason", "int32_t", "the game's EGUIEntryReason (as in sanctuary)"),
				F("to_sanctuary", "bool", "true: the switch goes into the Sanctuary; false: out of it"),
			}),
		new ModApiEvent("WeaponLevelUp", "weapon_level_up", 128, "Fable 3: CustomisableWeapon's SetLevel (r3 the weapon component 289, r4 the target level, r5 the hero), before it, when r4 differs from the level; cause from CustomisableWeapon.TriggerSpecialLevelUp's bracket",
			"Fable 3 only. A hero's weapon changes level: its morph at a level-up ('level_up', the game's TriggerSpecialLevelUp), or a new or restored weapon brought to its hero's ability level ('sync'). Each level stepped through adds that level's augments; a level below the current one first resets the weapon to level 1 and clears every augment it has, then steps up.",
			"the level stays as it is (the pending condition augments of a level-up are still applied)",
			new[]
			{
				F("weapon", "uint32_t", "the weapon"),
				F("owner", "uint32_t", "the hero whose ability level sets it (else its carrier)"),
				F("player", "int32_t", "1 or 2 when the owner is a player's hero, else 0"),
				F("level_from", "int32_t", "the level before"),
				F("level", "int32_t", "the new level (writable: at least 1, at most the higher of the asked level and the weapon's top level; lower than level_from resets the weapon's augments)", true),
				F("cause", "const char* / string", "'level_up' (the weapon's morph) or 'sync' (a new or restored weapon)"),
			}),
		new ModApiEvent("WeaponAugmentUnlocked", "weapon_augment_unlocked", 129, "Fable 3: CustomisableWeapon's AddAugment (r3 the weapon component 289, r4 the augment reference, r5 1 a level's augment / 0 a met condition), before it",
			"Fable 3 only. A weapon gains an augment by play: a level step's augment ('level') or a met condition applied at the level-up ('condition'). The augment goes to the weapon's Augmentable.",
			"the augment is not added; a refused condition augment is gone (the game empties its list after the level-up)",
			new[]
			{
				F("weapon", "uint32_t", "the weapon"),
				F("owner", "uint32_t", "who carries it, 0 for none"),
				F("player", "int32_t", "1 or 2 when the owner is a player's hero, else 0"),
				F("augment", "uint32_t", "the augment's record (\"0x…\")"),
				F("source", "const char* / string", "'level' or 'condition'"),
				F("level", "int32_t", "the weapon's level as it is added"),
			}),
		new ModApiEvent("WeaponUpgradeReady", "weapon_upgrade_ready", 130, "Fable 3: CustomisableWeapon's conditional-augment check (r3 the weapon component 289), after it, when its pending list grew (the authority's check only: elsewhere it relays)",
			"Fable 3 only. A weapon's augment condition was met (kills, gifts, a script's amount): the weapon now waits to level up, its new augments pending until then. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("weapon", "uint32_t", "the weapon"),
				F("owner", "uint32_t", "who carries it, 0 for none"),
				F("player", "int32_t", "1 or 2 when the owner is a player's hero, else 0"),
				F("added", "int32_t", "the condition augments this check added"),
				F("pending", "int32_t", "the condition augments waiting for the level-up"),
				F("level", "int32_t", "the weapon's level"),
			}),
		new ModApiEvent("HandHolding", "hand_holding", 131, "Fable 3: CECHandHolding's state setter (TU3 0x826C3140 / disc 0x82697E80: r3 the villager's hand-holding component 71, r4 on), after it, when +0xC changed; the leader is the component's +0x30 smart pointer",
			"A villager's hand was taken by a hero (held, or dragged when the villager would not come willingly) or let go: every start and every drop (falling behind, combat, a script's ClearHandHolding, a throw). A start is also a follow (event 79): the hand-holding start begins the villager's follow. Fable 2 has no hand holding (its nearest is follow, 79). Read-only.",
			string.Empty,
			new[]
			{
				F("villager", "uint32_t", "the villager whose hand it is"),
				F("leader", "uint32_t", "who holds (or held) it"),
				F("player", "int32_t", "1 or 2 when the leader is a player's hero, else 0"),
				F("holding", "bool", "taken (false: let go)"),
				F("dragged", "bool", "the villager is (was) being dragged, not led"),
				F("forced", "bool", "the hold was forced (InitiateHandHolding's forced flag)"),
			}),
		new ModApiEvent("HandHoldingRequest", "hand_holding_request", 132, "Fable 3: CECHandHolding's start (TU3 0x826C27C0 / disc 0x82697500: r3 the villager's component 71, r4 the leader, r5 forced), before it",
			"Something asks to take a villager's hand: the hero's interact on a villager, a script (HandHolding.InitiateHandHolding), a forced drag (ForceDragging), the restore of a saved hold, or a co-op partner's request the host replays. Online, the asking machine and the host both decide it. Fable 2 has no hand holding.",
			"the hand is not taken: the game's own refusal (nothing is written; a ForceDragging switch has already let go, so the villager is released; a refused saved hold is not retried)",
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("leader", "uint32_t", "who would hold the hand"),
				F("player", "int32_t", "1 or 2 when the leader is a player's hero, else 0"),
				F("forced", "bool", "a forced hold"),
			}),
		new ModApiEvent("InteractionStarted", "interaction_started", 133, "Fable 3: CECCharacterInteraction::Perform (TU3 0x8261BC98 / disc 0x825F4460: r3 the hero's CharacterInteraction component 302, the hero at +4; r4 the target; r5 the interaction's record; r7 1 for a script's), on the machine that performs it (the guest's own local-management test, sub_822092F0, first)",
			"A hero performs a social interaction with someone: the choice picked on a villager's interaction ring, a script's CharacterInteraction.PerformInteraction, or one a co-op partner's machine relayed. Fable 3 only (Fable 2 has expressions instead: expression_started).",
			"the interaction is not performed: nothing is played or stored; the ring still moves on as after any choice",
			new[]
			{
				F("hero", "uint32_t", "the hero performing it"),
				F("player", "int32_t", "1 or 2 when the hero is a player's, else 0"),
				F("target", "uint32_t", "who it is performed on (a villager, a dog, the partner)"),
				F("interaction", "uint32_t", "the interaction's record (\"0x…\")"),
				F("scripted", "bool", "a script asked for it (PerformInteraction), not the ring"),
			}),
		new ModApiEvent("InteractionResult", "interaction_result", 134, "Fable 3: the relationship mini-game's finish (TU3 0x82715438 / disc 0x826EBAE0: r3 the controller, its kind at +0xB0; r4 success), before it, while the GUI's interaction mode is open and its hero has a RelationshipHero (0x11A)",
			"The interaction circle mini-game ended: the hero hit the circle (success) or missed. The outcome drives everything after it: RelationshipHero.GetSuccess, the game's OnSuccess or OnFail, the hero's count and the message to the target. Fable 3 only.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero who played it"),
				F("player", "int32_t", "1 or 2 when the hero is a player's, else 0"),
				F("target", "uint32_t", "the interaction mode's target, 0 for a podium or a scripted one"),
				F("kind", "int32_t", "1 a villager's (RelationshipHero.StartNPCInteraction), 0 a scripted or podium one (Start, StartPodiumInteraction)"),
				F("success", "bool", "the hero hit the circle (writable)", true),
			}),
		new ModApiEvent("InteractionMode", "interaction_mode", 135, "Fable 3: Fable3/src/hooks/src/interaction_hooks.cpp — the GUI's interaction mode opening (its mode set and the four dedicated openers: real estate, shop, pawn shop, orb; raised when the opener's clear ran) and closing (its close, when its open flag +4 was set)",
			"A hero's interaction screen opened or closed: a villager's interaction ring, a shop, the estate screen, the world map and the others. mode: 0 villager ring, 1 shop, 2 decoration, 3 world GUI, 4 podium, 5 a scripted RelationshipHero interaction (no menu), 6 or 7 real estate, 8 orphanage, 9 crime, 10 world map, 11 orb, 12 an unnamed decoration menu, 13 dog, 14 pawn shop, 15 unnamed. Opening one while another is open is one event, with the one it replaced as previous. Fable 3 only (Player.StartInteractionMode, on both titles, is a different thing: the hero's movement mode for scripts).",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero whose screen it is"),
				F("player", "int32_t", "1 or 2 when the hero is a player's, else 0"),
				F("target", "uint32_t", "who or what it is with (the villager, the shopkeeper), 0 for none"),
				F("mode", "int32_t", "the mode opened or closed"),
				F("previous", "int32_t", "on opening: the mode it replaced, -1 when none was open; -1 on closing"),
				F("open", "bool", "opened (true) or closed (false)"),
			}),
		new ModApiEvent("RelationshipStage", "relationship_stage", 136, "Fable 3: Fable3/src/hooks/src/mod_events_hooks.cpp — the opinion record's SetStage (r3 the record, r4 the stage; +0x90 compared before and after), bracketed by its four callers, each stating the opinion component: the opinion update's progress (r4), a relationship quest's end (r5), OpinionReaction.ResetCurrentRelationshipStage (r3, r4 the hero) and the net client's sync (r3)",
			"A villager's relationship with a hero changed stage (fear, hate, neutral, friend, love): opinion crossing into the next stage, a relationship quest finished, a script, divorce or parenthood resetting it, or on an online client the host's sync. Fable 3 only: Fable 2's opinions are axis values with no stage (see opinion_modified, engaged, marriage). Read-only.",
			string.Empty,
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("hero", "uint32_t", "the hero the relationship is with"),
				F("player", "int32_t", "1 or 2 for a player's hero, else 0"),
				F("stage", "int32_t", "the new stage: 0 fear, 1 hate, 2 neutral, 3 friend, 4 love (the game's ERelationshipStages)"),
				F("previous", "int32_t", "the stage before"),
				F("cause", "const char* / string", "'opinion' (the opinion moved it), 'quest' (a relationship quest ended), 'reset' (ResetCurrentRelationshipStage: a script, divorce, Villager.AddParent) or 'sync' (an online client applying the host's)"),
			}),
		new ModApiEvent("DogDig", "dog_dig", 137, "Dog.SetEntityBeingDug (r3 the dog component, the dog at +4; r4 the entity it digs), after it: the dog brain's statement; it marks a digging spot dug by a dog",
			"A dog started digging at a spot it found. first is true when the spot was not yet marked dug by a dog. What the dig yields comes later (dig_spot_dug, by_dog). Read-only.",
			string.Empty,
			new[]
			{
				F("dog", "uint32_t", "the dog"),
				F("spot", "uint32_t", "what it digs (a digging spot)"),
				F("player", "int32_t", "1 or 2 by the dog's master, else 0"),
				F("first", "bool", "the spot was not yet marked dug by a dog"),
			}),
		new ModApiEvent("DogLeading", "dog_leading", 138, "Dog.SetIsInLeadingHeroBehaviour (r3 the dog component; r4 quest), after it: the dog's find is its lead target (Fable 2 +0x154 while +0xD4 < 0; Fable 3 +0x104 while +0x88 < 0)",
			"A dog started leading its hero to something it found: a digging spot, a chest or treasure (target), or a quest objective (quest, target 0). The game's HUD says which (GUI_DOG_DIGSPOT / CHEST / QUEST). Read-only.",
			string.Empty,
			new[]
			{
				F("dog", "uint32_t", "the dog"),
				F("player", "int32_t", "1 or 2 by the dog's master, else 0"),
				F("target", "uint32_t", "the find, 0 for a quest objective"),
				F("quest", "bool", "it leads to a quest objective"),
			}),
		new ModApiEvent("DogTrickLearned", "dog_trick_learned", 139, "Dog.UnlockTrickIfNecessary (r3 the dog component; r4 the {hash, record} pair; Fable 3 r5 show), after it, when the unlocked list grew (Fable 2 +0x2E4..+0x2E8, Fable 3 +0x2A4..+0x2A8)",
			"A dog learned a trick: Fable 2 from a trick book; Fable 3 from its training (a level-up unlocks its level's tricks, shown false: the level-up itself is dog_learned) or a script. trick is the hash dog_trick names it by. Read-only.",
			string.Empty,
			new[]
			{
				F("dog", "uint32_t", "the dog"),
				F("player", "int32_t", "1 or 2 by the dog's master, else 0"),
				F("trick", "uint32_t", "the trick's command hash (\"0x…\"), as dog_trick's"),
				F("shown", "bool", "the game announced it (tutorial, DogLearningFX); always true on Fable 2"),
				F("tricks", "int32_t", "how many tricks the dog knows now"),
			}),
		new ModApiEvent("DogLearned", "dog_learned", 140, "Fable 2: Dog.RaiseSniffingLevelTo / RaiseCombatLevelTo (r3 the dog component; r4 the level; +0xB8 / +0xBC), after each, when the level rose; Fable 3: DogStats' training-level notice (r3 the DogStats component 242; r4 the track; r5 up), after it",
			"A dog's training level changed: Fable 2's dog training books (track 0 sniffing / hunting, 1 combat), Fable 3's training (0 explore, 1 combat, 2 charisma). Fable 3 also announces a loss (up false); a silent set (a load, the co-op resync) is not raised. Read-only.",
			string.Empty,
			new[]
			{
				F("dog", "uint32_t", "the dog"),
				F("player", "int32_t", "1 or 2 by the dog's master, else 0"),
				F("track", "int32_t", "0 explore (Fable 2: sniffing), 1 combat, 2 charisma (Fable 3)"),
				F("level", "int32_t", "the new level"),
				F("up", "bool", "it rose (always on Fable 2)"),
				F("xp", "int32_t", "Fable 3: the track's training XP; 0 on Fable 2"),
			}),
		new ModApiEvent("DiveSpotUsed", "dive_spot_used", 141, "the dive spot's use (r3 the DiveSpot component, Fable 2 96 / Fable 3 117, the spot at +4; r4 the diver), after it; its one caller is CAction_SwimDiveLevelExit",
			"A hero finished a dive at a dive spot: the find is counted on their stats and the spot's script runs (online on Fable 3 it may run a moment later). Read-only.",
			string.Empty,
			new[]
			{
				F("spot", "uint32_t", "the dive spot"),
				F("diver", "uint32_t", "the hero who dived"),
				F("player", "int32_t", "1 or 2"),
			}),
		new ModApiEvent("Vomit", "vomit", 142, "the drunkenness component's vomit (r3 the component, the hero at +4), after it, when NumberOfVomits (+0x20) moved",
			"A drunk hero threw up: the beer reservoir is emptied and the vomit counted. Read-only.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("vomits", "int32_t", "the hero's vomits counted, this one included"),
			}),
		new ModApiEvent("JobStarted", "job_started", 143, "Fable 3: the job sign's start (TU3: r3 the JobSign component 311, r4 the Job Data table, r5 player two; r3 = 1 once started. Disc: r3 the component, r4 the hero, r5 the table), after it",
			"Fable 3 only. A hero started a job session at a job sign (blacksmith, pie maker, lute hero): the game handed the session to the sign's StartJob script. job is the sign's JobName. Read-only.",
			string.Empty,
			new[]
			{
				F("sign", "uint32_t", "the job sign"),
				F("hero", "uint32_t", "the hero working it"),
				F("player", "int32_t", "1 or 2"),
				F("job", "const char* / string", "the job's name, the sign's JobName"),
			}),
		new ModApiEvent("JobLevel", "job_level", 144, "Fable 3: Stats.SetJobLevel (r3 the stats component 15, the hero at +4; r4 the job's name; r5 the level), before it",
			"Fable 3 only. A hero's level at a job is set (a level-up, or a script's reset). Setting it also restarts the sessions worked at the level (job_worked).",
			"the level is not set: the job's record keeps its level and its sessions worked",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("job", "const char* / string", "the job's name"),
				F("level", "int32_t", "the new level (writable, at least 0)", true),
				F("previous", "int32_t", "the level it had"),
			}),
		new ModApiEvent("JobEarnings", "job_earnings", 145, "Fable 3: Stats.SetTotalMoneyEarnedForJob (r3 the stats component; r4 the job's name; r5 the total), after it",
			"Fable 3 only. A hero's running total of gold earned at a job is set: a session's pay folded into the job's record (the gold itself is the gold event). Read-only.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("job", "const char* / string", "the job's name"),
				F("total", "int32_t", "the new total"),
				F("earned", "int32_t", "what it added (the total less the one it replaced)"),
			}),
		new ModApiEvent("JobWorked", "job_worked", 146, "Fable 3: Stats.SetTimesWorkedJobThisLevel (r3 the stats component; r4 the job's name; r5 the count), after it",
			"Fable 3 only. A hero's count of sessions worked at a job's current level is set. Read-only.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("job", "const char* / string", "the job's name"),
				F("times", "int32_t", "the sessions worked at this level"),
				F("level", "int32_t", "the job's level"),
			}),
		new ModApiEvent("JobMultiplier", "job_multiplier", 147, "GUI.SetJobMultiplierLevel (r3 the level) and GUI.TurnJobMultiplierOff (Fable 2 spells it TurnJobMulitplierOff), after each",
			"A job's combo multiplier as its script states it to the HUD: Fable 2's blacksmith, woodcutting and bartending, Fable 3's job signs. on false: the multiplier was turned off. The game does not state whose job. Read-only.",
			string.Empty,
			new[]
			{
				F("level", "int32_t", "the multiplier level, 0 when turned off"),
				F("on", "bool", "false when turned off"),
				F("player", "int32_t", "always 0: the game does not state who"),
			}),
		new ModApiEvent("JobSummary", "job_summary", 148, "Fable 2: GUI.TurnOnMiniGameComplete (r3 the title, r4 the subtitle) and GUI.AddAmountToMiniGameComplete (r3 the line, r4 the amount), after each",
			"Fable 2 only. A job shift's summary screen as the job script fills it: its title ('title': text and subtitle), then each line ('line': text and amount). The texts are the script's keys. Read-only.",
			string.Empty,
			new[]
			{
				F("part", "const char* / string", "'title' or 'line'"),
				F("text", "const char* / string", "the title's or the line's text key"),
				F("subtitle", "const char* / string", "the title's subtitle key; '' for a line"),
				F("amount", "int32_t", "the line's amount; 0 for the title"),
			}),
		new ModApiEvent("GuildMessage", "guild_message", 149, "the guild voice's play-next (Fable 2: the GuildMessages component, its queue at +0x20, count +0x24; Fable 3: the GuildMessageManager, queue +0x18, count +0x1C), after it, when a message was queued",
			"The guild voice starts its next message (Fable 2's guild voice, Fable 3's Jasper or the narrator): voiced and subtitled. Fable 2 names it by its text tag; Fable 3 by the text key the tag resolved to (\"0x…\"). Read-only.",
			string.Empty,
			new[]
			{
				F("message", "const char* / string", "Fable 2: the message's text tag; Fable 3: its text key (\"0x…\")"),
				F("narrator", "const char* / string", "Fable 2: the narrator's tag; Fable 3: the narrator's key (\"0x…\")"),
				F("player", "int32_t", "always 0: the voice is the machine's, not a hero's"),
			}),
		new ModApiEvent("TitleChanged", "title_changed", 150, "Stats.SetTitleRecord (r3 the stats component, the hero at +4; r4 the title name), after it, when the title record it writes (Fable 2 +0x5C, Fable 3 +0x3F0) moved",
			"A hero's title changed: bought from a title seller or set by a script. Only a title the hero holds is applied, so a title it does not hold, or the one already worn, raises nothing. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2, 0 for another entity"),
				F("title", "const char* / string", "the title's record name"),
			}),
		new ModApiEvent("TitleUnlocked", "title_unlocked", 151, "Stats.AddUnlockedTitle (r3 the stats component; r4 the title name), after it, when the unlocked titles' end (Fable 2 +0x6C, Fable 3 +0x46C) advanced",
			"A title was added to a hero's unlocked titles (a purchase, a quest, a deed). The game pushes a repeated unlock again, and it is raised again. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2, 0 for another entity"),
				F("title", "const char* / string", "the title's record name"),
				F("titles", "int32_t", "how many entries the unlocked titles hold now"),
			}),
		new ModApiEvent("ChapterProgress", "chapter_progress", 152, "Stats.SetChapterProgress (r3 the stats component; f1 the progress), after it, when the progress (Fable 2 +0x58, Fable 3 +0x39C) changed: the game only ever raises it",
			"The story's chapter progress rose. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero whose stats hold it"),
				F("player", "int32_t", "1 or 2, 0 for another entity"),
				F("progress", "float", "the progress now"),
				F("previous", "float", "the progress before"),
			}),
		new ModApiEvent("ScarAdded", "scar_added", 153, "Morph.AddScar (r3 the morph component, the entity at +4; r4 the body-area mask), before it",
			"A scar is about to be added to a body area: a knockout, Morph.AddRandomScar (the areas 0x8, 0x400 and 0x20000) or a script. The game still refuses it when two scars already cover the area (and on Fable 3 without a scar record for it).",
			"no scar is added (the game's own refusal path)",
			new[]
			{
				F("entity", "uint32_t", "who is scarred"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("area", "uint32_t", "the body-area mask (\"0x…\")"),
				F("scars", "int32_t", "the scars it has before this one"),
			}),
		new ModApiEvent("ScarRemoved", "scar_removed", 154, "Morph.RemoveRandomScar (r3 the morph component), after it, when its scar count dropped",
			"A random scar was healed. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "who was healed"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("scars", "int32_t", "the scars it has now"),
			}),
		new ModApiEvent("AgeChanged", "age_changed", 155, "Age.SetAge (r3 the age component, the entity at +4; f1 the age: `stfs` into +0xC, clamped to [0, +0x10]), before it, when f1 differs from the age",
			"An entity's age is about to be set: a hero ageing (on Fable 2 every stats update that moves it), a statue sculpted, a villager converted or cloned, a type change, a script. The game clamps the age to [0, limit].",
			"the age stays (on Fable 3 nothing is sent to the co-op partner either)",
			new[]
			{
				F("entity", "uint32_t", "whose age"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("age", "float", "the age being set (writable; the game clamps it)", true),
				F("previous", "float", "the age before"),
				F("limit", "float", "the age limit it is clamped to"),
			}),
		new ModApiEvent("HeroStage", "hero_stage", 156, "Player.SetHeroStage (r3 the player component, the hero at +4; r4 the stage), after it, when the stage (Fable 2 +0x54, Fable 3 +0x74) changed",
			"The hero's story stage changed (the number the game's scripts set with Player.SetHeroStage). After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("stage", "int32_t", "the stage now"),
				F("previous", "int32_t", "the stage before"),
			}),
		new ModApiEvent("ClothingWorn", "clothing_worn", 157, "Fable 2: AppearanceModifierManager.AddAndReplaceItem (rex_CECAppearanceModifiers_AddAndReplaceItem, r3 the appearance component 15, r4 the item); Fable 3: the appearance manager's add every wear reaches (same registers, r3 back 1 worn / 0 refused); before each",
			"An item of clothing, a hairstyle or a tattoo is about to be worn by anyone: the wardrobe, a purchase, an outfit, a script. The game still refuses an item whose slot is blocked. Wearing it replaces what it covers.",
			"it is not worn (the game's own refusal; on Fable 3 the add answers 0)",
			new[]
			{
				F("entity", "uint32_t", "who wears it"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("definition", "uint32_t", "the item's record (\"0x…\")"),
				F("category", "int32_t", "the item's category, in each title's own numbers (Fable 2 item+0x10, Fable 3 item+0x18)"),
			}),
		new ModApiEvent("ClothingRemoved", "clothing_removed", 158, "Fable 2: AppearanceModifierManager.RemoveItem / RemoveWornByCategory (r3 the appearance component; r4 the item / the category); Fable 3: the appearance manager's remove and RemoveAllItemsOfCategory; after each, when the worn list shrank",
			"Worn clothing was taken off: one item, or every item of a category. On Fable 3 an item another wear replaces is taken off here too. After the fact, not cancellable.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "who wore it"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("definition", "uint32_t", "the item's record (\"0x…\"), 0 when a whole category came off"),
				F("category", "int32_t", "the category (the item's, or the one removed), in each title's own numbers"),
				F("count", "int32_t", "how many worn items came off"),
			}),
		new ModApiEvent("ClothingDyed", "clothing_dyed", 159, "Fable 2: AppearanceModifierManager.SetClothingItemDyeColour (rex_CECAppearanceModifiers_SetDyeColour, r3 the component, r4 the item, r5 the slot, r6..r8 red/green/blue, f1 brightness), before it, and lhGuiInventory_ApplyDyeToItem (r4 the dye, r5 the item, r6 the slot), after it; Fable 3: Lua_AppearanceModifierManager_SetClothingItemDyeColour (same registers as Fable 2's), before it",
			"A worn item is dyed: a dye from the inventory (Fable 2), the game's dye screens, an outfit, a script. A script's dye and every Fable 3 dye are decided before they are written; Fable 2's inventory dye is reported after, its colours read from the item.",
			"the item keeps its colour (not for Fable 2's inventory dye, which is reported after)",
			new[]
			{
				F("entity", "uint32_t", "who wears the item"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("definition", "uint32_t", "the dyed item's record (\"0x…\")"),
				F("dye", "uint32_t", "Fable 2's inventory dye: the dye item's record (\"0x…\"); 0 otherwise"),
				F("red", "int32_t", "0..255 (writable where decided)", true),
				F("green", "int32_t", "0..255 (writable where decided)", true),
				F("blue", "int32_t", "0..255 (writable where decided)", true),
				F("brightness", "float", "the dye's brightness (writable where decided)", true),
				F("secondary", "bool", "the item's second colour slot (true) or its first"),
			}),
		new ModApiEvent("PubGameStarted", "pub_game_started", 160, "Fable 2: StartPubGame (rex_lhPubGameManager_StartPubGame, r3 the game's name, r4 the variation; r3 back 1 started / 0 refused), before it, while no pub game is running (the manager 0x834C20B8's state +0x34 is 0)",
			"Fable 2 only. A pub game (Keystone, Fortune's Tower, Spinnerbox) is about to start. The games are player one's.",
			"the game does not start: StartPubGame answers false to its script, as when one is already running",
			new[]
			{
				F("game", "const char* / string", "the game's name as the script gives it (the game lower-cases it: keystone, magedice, wizardstower)"),
				F("variation", "const char* / string", "the variation (the table and stakes)"),
				F("hero", "uint32_t", "player one's hero"),
				F("player", "int32_t", "1"),
			}),
		new ModApiEvent("PubGameFinished", "pub_game_finished", 161, "Fable 2: the pub game's settlement (rex_lhPubGameManager_Settle, r3 the manager), from its tick in state 9, before it",
			"Fable 2 only. A pub game ended and is about to be settled: the winnings paid to player one's hero, the per-game statistics, the purity balancing and the gossip. Not cancellable.",
			string.Empty,
			new[]
			{
				F("game", "const char* / string", "the game's name (lower case)"),
				F("variation", "const char* / string", "the variation"),
				F("hero", "uint32_t", "player one's hero"),
				F("player", "int32_t", "1"),
				F("gold", "int32_t", "the gold paid to (or, negative, taken from) the purse: the profile's gold less the stake (+0x60)"),
				F("result", "int32_t", "the session's result the game records in its statistics and gossips about (±25): gold, netted against the debt"),
			}),
		new ModApiEvent("VillageWealth", "village_wealth", 162, "Village.AdjustWealthIndex (r3 the village component, the village at +4; f1 the change; Fable 2 r5 / Fable 3 r6 the unscaled byte; Fable 3 r5 the keep-local byte), before it",
			"A town's wealth is about to change: a shop's trade, a gift's value, a deed, a script. The game scales the change by the town's factor unless it is unscaled, then clamps the wealth to [min, max].",
			"the wealth stays (on Fable 3 only for a change the game sends to the co-op partner; one it keeps local is reported)",
			new[]
			{
				F("village", "uint32_t", "the town"),
				F("delta", "float", "the change as stated, before the town's scaling (writable)", true),
				F("scaled", "bool", "the game multiplies it by the town's factor (Fable 2 +0x1C, Fable 3 +0x64)"),
				F("wealth", "float", "the wealth before"),
				F("min", "float", "the lowest it can go"),
				F("max", "float", "the highest it can go"),
			}),
		new ModApiEvent("GargoyleDestroyed", "gargoyle_destroyed", 163, "Fable 2: WorldMapInfo.IncrementGargoylesKilled (rex_CWorldMapInfo_IncrementGargoylesKilled, r3 the world map info), before it, when the current level's name it counts the kill for is set",
			"Fable 2 only. A gargoyle was destroyed and counted for the current region. The script that counts it does not state who shot it. Not cancellable.",
			string.Empty,
			new[]
			{
				F("level", "const char* / string", "the current level, whose region the kill is counted for"),
				F("player", "int32_t", "always 0: the game does not state who"),
			}),
		new ModApiEvent("CameraCut", "camera_cut", 164, "the camera statements (src/runtime/src/core/stated_view.cpp): the block the manager's getter built (rex_lhCameraManager_Block) carries the cut byte set where its previous block had it clear; raised at the guest tick for the player of the push (rex_lhCameraManager_Push) that ends the update",
			"The game CUT player p's camera: the camera its readers see jumped to a new place instead of blending (a cutscene's shot, a load, a canned camera's start). " + CameraEventsMeaning + " Read-only.",
			string.Empty,
			new[] { F("player", "int32_t", "1 or 2: whose camera") }),
		new ModApiEvent("CameraBlend", "camera_blend", 165, "the camera statements: Camera.InterpolateFor (rex_Camera_InterpolateFor) with ticks above 0 stated since the previous push; raised at the guest tick for the player of the push (rex_lhCameraManager_Push) that ends the update",
			"Player p's camera started a BLEND to its new place over ticks guest ticks (a camera script's transition: aiming in and out, a spell's charge, a canned camera's end). " + CameraEventsMeaning + " Read-only.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("ticks", "int32_t", "the blend's length in guest ticks (15 a second), the longest stated in the update"),
			}),
		new ModApiEvent("CameraScreenChanged", "camera_screen_changed", 166, "the views the shell states every drawn frame (RefiiHostStateCameraViews): raised at the guest tick when the screen mode or the split differs from the last raised",
			"The co-op SCREEN changed: the mode the player chose (camera_screen_mode) or whether two views are drawn (camera_split). " + CameraScreenModeMeaning + " Read-only.",
			string.Empty,
			new[]
			{
				F("mode", "int32_t", "0 shared, 1 split_vertical, 2 split_horizontal, 3 hybrid"),
				F("split", "bool", "two views are drawn now"),
				F("previous", "int32_t", "the mode before, -1 for none raised yet"),
				F("reason", "int32_t", "0 the player's choice or a player joining or leaving, 1 hybrid's distance"),
			}),
		new ModApiEvent("CameraZoomChanged", "camera_zoom_changed", 167, "the game VM: the core camera chunk after each SACCamera:CalculateActiveZoomLevel, when the camera's zoom set (CurrentZoomSet.Name) or whether it blends toward combat (ZoomLevelCombatWeight > 0) differs from the last stated for its player",
			"Player p's exploring camera changed ZOOM: another set (exterior, interior, vanity; Fable 3 also sprint, vault, falling ...) or into or out of the combat blend. " + CameraZoomMeaning + " Read-only.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("set", "const char* / string", "the zoom set's name, \"\" for an unnamed one"),
				F("previous_set", "const char* / string", "the set before, \"\" for none"),
				F("level", "int32_t", "the index in the set"),
				F("combat", "float", "the weight toward the combat camera, 0..1"),
				F("string_length", "float", "metres"),
				F("cage_radius", "float", "metres"),
				F("max_cage_radius", "float", "metres, 0 for none"),
				F("height", "float", "the camera's height over the focus, metres"),
				F("focal_offset", "float", "the focus's height over the feet, metres"),
				F("fov", "float", "degrees, horizontal"),
				F("max_coop_distance", "float", "the co-op leash the level states, metres, 0 for none"),
			}),
		new ModApiEvent("CameraTick", "camera_tick", 168, "the camera statements: each push (rex_lhCameraManager_Push), the end of one camera manager's update; raised at the guest tick with the block its getter built (rex_lhCameraManager_Block)",
			"Once per guest tick per player, after the camera's update and before it is drawn: the block the game's readers see. A handler that wants the picture moved states camera_offset. " + CameraEventsMeaning + " Read-only.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("x", "float", "the position, guest axes"),
				F("y", "float", "the position"),
				F("z", "float", "the position"),
				F("fx", "float", "forward"),
				F("fy", "float", "forward"),
				F("fz", "float", "forward"),
				F("ux", "float", "up"),
				F("uy", "float", "up"),
				F("uz", "float", "up"),
				F("fov", "float", "degrees, horizontal"),
				F("cut", "bool", "the block's cut byte"),
			}),
	};

	/// <summary>
	/// WHAT FABLE 3 RAISES (Fable3/src/hooks/src/mod_lua_hooks.cpp and mod_events_hooks.cpp), by kind:
	/// each with its note where the fields or the timing differ from the catalog's Fable 2 seam. Gold,
	/// renown, ability_level, fast_travel and quest_started are decided inside the seam, as the catalog states them
	/// (cancel, and their writable field); every other Fable 3 event is raised from the next guest tick
	/// and reports. An event absent here is Fable 2's alone.
	/// </summary>
	public static readonly IReadOnlyDictionary<int, string> Fable3Raised = new Dictionary<int, string>
	{
		[0] = string.Empty,
		[1] = string.Empty,
		[2] = string.Empty,
		[3] = string.Empty,
		[4] = string.Empty,
		[5] = "Fable 3's GameAction ids (its control scheme's rows), not Fable 2's; a cancel hides the action from the hero's own receiver",
		[6] = "Fable 3's AbilityType numbers: 16 melee pressed, 17 released and 18 magic charge are the same",
		[7] = "action_type -1 and action_name \"\" (Fable 3's strikes are built in code, not from a scriptable action)",
		[8] = string.Empty,
		[9] = string.Empty,
		[10] = "a hit the block answers itself (its parry flag) is not offered; the hit record is not sent",
		[11] = "action_type -1 and action_name \"\"; raised for every event the base handler receives, one for an animation the action no longer plays included",
		[12] = "spell_type and spell_level are Fable 3's numbers (0..8, -1 none), not Fable 2's spell list; tag is 0",
		[13] = "spell_type and spell_level are Fable 3's numbers (0..8, -1 none); tag is 0",
		[14] = "a reversal is not offered, nor a killing blow",
		[15] = "the quick charge's start",
		[16] = "a written level is clamped to the filled slots",
		[17] = "type is the slot's first spell (Fable 3 weaves two); tag is 0 and not writable; manager is not sent",
		[18] = "manager is not sent",
		[19] = "type is Fable 3's (0 Lightning .. 8 Ice Storm); slot is -1 and tag 0",
		[20] = "pool 4 is FROM_CHEST and 5 LOST_ON_DEATH (Fable 3's own); nested is always false",
		[21] = string.Empty,
		[22] = "raised after the death when no resurrection potion revived the hero",
		[23] = "the hit record is not sent",
		[24] = "level only; from_save is not stated",
		[25] = string.Empty,
		[26] = string.Empty,
		[27] = string.Empty,
		[28] = "a cancel inside the hero's death refuses the resurrection potion's revive",
		[29] = "release is not cancellable",
		[30] = "item is 0 (the record is in definition)",
		[31] = string.Empty,
		[32] = "entity, player, gold, delta; passive and share are not stated",
		[33] = "decided after the answer: a written relation (1, 2, 4) is the answer",
		[34] = string.Empty,
		[35] = "decided (a cancel leaves the quest inactive); the hero is player 1's",
		[36] = "the hero is player 1's",
		[37] = "the hero is player 1's",
		[38] = "read-only (raised when the line plays)",
		[39] = "entity only",
		[40] = "entity only",
		[41] = string.Empty,
		[42] = "delta is a number (followers); share is not stated",
		[43] = string.Empty,
		[44] = "Fable 3's Primal axis, which its own balancing calls purity",
		[45] = string.Empty,
		[46] = "the player is the dog's master's",
		[47] = string.Empty,
		[48] = "effects 1 electrified, 2 chaos, 3 on fire, 4 in a vortex, 6 mauled; the ragdoll, unconscious and crumbled states are not raised yet",
		[49] = string.Empty,
		[50] = string.Empty,
		[51] = "Fable 3 keeps an opinion per hero: player is whose",
		[52] = string.Empty,
		[53] = "a layer-3 (network) blend refused leaves the other machine's sky apart",
		[54] = string.Empty,
		[55] = "after the load starts: a cancel is not honoured; type is Fable 3's (2 load or start, 5 / 6 fast travel free / paid); the traveller is player 1",
		[56] = "the player is player 1",
		[57] = "the GUI screen word leaving none; player 1",
		[58] = "when the GUI manager's screen returns to none (32)",
		[59] = "Fable 3's screen ids: 0 the download box, 6 the epilogue, 8 the credits, 32 none",
		[60] = "day_set is the region's theme id; player 1",
		[61] = "the bed box's sleep row: choice 0 morning, 1 midday, 2 midnight (Fable 3's ESleepType); a cancel leaves the bed unchosen; cancelled is always false",
		[62] = string.Empty,
		[63] = string.Empty,
		[64] = "Fable 3's crime types: 0 attempted murder .. 10 vandalism",
		[65] = "id is the achievement record's XLASTAchievementID; replayed unlocks too",
		[66] = "the Lua setter, the gnome capture and the pickup; record is 0 for a collectable that names its record by reference",
		[67] = "kind is always 0 (a mod's own question, Refii.ask_partner): the question is a message box for the partner (couch) or the peer's (online)",
		[68] = "decided in the two purchase routines; price is the game's own buy price",
		[69] = "price is -1 (not stated yet); not writable",
		[70] = "gold is what reached the hero's purse; drops is 1, away false",
		[71] = string.Empty,
		[72] = "repaired is false (repairs are not told apart yet)",
		[73] = "not cancellable (raised after)",
		[74] = "uid is the spouse's 64-bit UID",
		[75] = "an adoption with the spouse (Fable 3 has no births)",
		[76] = string.Empty,
		[77] = "raised for every removal the game's script makes",
		[78] = string.Empty,
		[79] = string.Empty,
		[80] = string.Empty,
		[81] = "the decorator is player 1",
		[82] = "building is 0; the screen is player 1's",
		[83] = "stat is the FNV-1a hash of the stat's name, as \"0x…\" text",
		[84] = "the game's own multiplayer marriage and business partnership between the two heroes (CECMultiplayerMarriage), raised by the host when it changes them",
		[86] = string.Empty,
		[87] = string.Empty,
		[88] = string.Empty,
		[89] = string.Empty,
		[90] = string.Empty,
		[91] = "priced in guild seals: abilities.json `seals`, one price per level; experience is the seals spent or given back. Bought through Refii.ability_buy / ability_refund (Fable 3 has no abilities book)",
		[92] = "the game's own pick; Fable 3 has no spell wheel",
		[93] = string.Empty,
		[94] = "component is not sent",
		[95] = "damage is 0; lifts is not writable yet",
		[96] = string.Empty,
		[97] = "tag is 0",
		[98] = "mode is Fable 3's: 1 the GUI pause, 2 the load pause, 12 the maps; screen 32 is none",
		[99] = "read-only: not cancellable on Fable 3",
		[100] = "polled each tick: the game's own full-cutscene test, the player component's foreground-cutscene byte and its held-LT camera override",
		[101] = string.Empty,
		[102] = "tag is 0",
		[103] = string.Empty,
		[104] = "polled each tick: the game's own HasPlayerMode(TARGET_LOCK) going from held to not held",
		[105] = string.Empty,
		[106] = "count is 1 per call",
		[108] = "tag is 0",
		[109] = "a charge loop ends a charge only after that hero's charge started",
		[110] = "action_type -1",
		[111] = "action_type -1",
		[112] = "from the core chunk's wrapper on CameraManager:FrameUpdate",
		[113] = string.Empty,
		[115] = "a mod morph's weight changed (morph_set on either machine, or the enabled mods' morphs.json); the hero is redrawn through the game's own morph update",
		[116] = string.Empty,
		[117] = string.Empty,
		[118] = string.Empty,
		[119] = "seals are asked only for an opener (0 when a script opens the chest)",
		[120] = string.Empty,
		[121] = string.Empty,
		[122] = string.Empty,
		[123] = string.Empty,
		[124] = string.Empty,
		[125] = string.Empty,
		[126] = string.Empty,
		[127] = string.Empty,
		[128] = string.Empty,
		[129] = string.Empty,
		[130] = "raised on the machine with authority over the weapon only (elsewhere the check relays a packet)",
		[131] = "a TU3 online client applies the host's state by message 462 without the setter, and does not raise it",
		[132] = "online, the asking machine and the host both decide it",
		[133] = "decided on the machine that performs it (the host for a relayed choice)",
		[134] = string.Empty,
		[135] = string.Empty,
		[136] = string.Empty,
		[137] = "the player is the dog's master's",
		[138] = "the player is the dog's master's",
		[139] = "shown is the caller's r5: a training level-up unlocks its tricks silently",
		[140] = "tracks 0 explore, 1 combat, 2 charisma; a loss is raised too (up false); xp is the track's training XP",
		[141] = "online the spot's script may run after the event (TU3 takes a lock first)",
		[142] = string.Empty,
		[143] = string.Empty,
		[144] = string.Empty,
		[145] = string.Empty,
		[146] = string.Empty,
		[147] = string.Empty,
		[149] = "message and narrator are text keys (\"0x…\"), not tags",
		[150] = "after the fact (the record at +0x3F0)",
		[151] = string.Empty,
		[152] = string.Empty,
		[153] = "also refused by the game without the entity's component 20 or a scar record for the area",
		[154] = string.Empty,
		[155] = "a cancel also skips the age packet to the co-op partner",
		[156] = "the game writes only a different stage",
		[157] = "decided in the add every wear reaches; a cancel answers 0",
		[158] = "an item another wear replaces is removed here too",
		[159] = "every dye is decided (the partner's packet is not raised); dye is 0",
		[162] = "decided only for a change sent to the co-op partner (r5 = 0), else reported; unscaled is r6",
		[164] = "player 1, and player 2 on a couch screen drawn split (player two's own camera manager, keyed 2 by its push); on a shared couch screen player 1 alone, the one camera drawn",
		[165] = "as camera_cut",
		[166] = "the couch co-op screen (shared, split, hybrid) as on Fable 2; outside a couch game shared and split false",
		[167] = "the sets of Fable 3's SACCamera (Sprint, Vault, Falling, ICS ... besides Exterior and Interior)",
		[168] = "as camera_cut",
	};

	/// <summary>Whether <paramref name="title"/> (its folder, <c>Fable2</c> / <c>Fable3</c>) raises the event.</summary>
	public static bool RaisedOn(ModApiEvent e, string title) =>
		title == "Fable3" ? Fable3Raised.ContainsKey(e.Kind) : !Fable3Only.Contains(e.Kind);

	/// <summary>The kinds only Fable 3 raises (its own systems): Fable 2 never raises them.</summary>
	public static readonly IReadOnlySet<int> Fable3Only = new HashSet<int> { 116, 117, 118, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136, 143, 144, 145, 146 };

	/// <summary>REFII_CUTSCENE_* (core/refii_mod_events.h): the cutscene event's kinds and in_cutscene's bits.</summary>
	public const int CutsceneFull = 1;
	public const int CutsceneInteractive = 2;
	public const int CutsceneLookAt = 4;
	public const int CutsceneAll = CutsceneFull | CutsceneInteractive | CutsceneLookAt;

	/// <summary>
	/// THE NAMED NUMBERS every surface states the same: <c>Refii.&lt;Name&gt;</c> in Lua (the preamble's),
	/// <c>RefiiHost.constants()</c> in Godot. SPELL_* the spell types (the hit's spell_type, a slot's
	/// spell, cast_spell's type); ABILITY_* the ability types (ability_level, an ability's costLike);
	/// REQUEST_* the ability_request types; ACTION_* the game_action ids the mods meet; RELATION_* the
	/// faction rule's answers (relation, the relation event); ATTACK_MODE_* the keyboard's attack modes
	/// (attack_mode, attack_mode_changed); CAMERA_* the game's ECameraMode (miscellaneous/cameramodeenum.lua:
	/// camera_mode, camera_changed, camera_override).
	/// </summary>
	public static readonly IReadOnlyList<(string Name, int Value)> Constants = new (string, int)[]
	{
		("SPELL_LIGHTNING", 0), ("SPELL_FIREBALL", 1), ("SPELL_SLOW_TIME", 2), ("SPELL_BLADES", 3),
		("SPELL_VORTEX", 4), ("SPELL_CHAOS", 5), ("SPELL_FORCE_PUSH", 6), ("SPELL_RAISE_DEAD", 7),
		("ABILITY_HEAVY_WEAPONS", 1), ("ABILITY_PHYSIQUE", 2), ("ABILITY_TOUGHNESS", 3), ("ABILITY_RANGED_WEAPONS", 4),
		("ABILITY_ACCURACY", 5), ("ABILITY_SPEED", 6), ("ABILITY_LIGHTNING", 7), ("ABILITY_FIREBALL", 8),
		("ABILITY_SLOW_TIME", 9), ("ABILITY_BLADES", 10), ("ABILITY_VORTEX", 11), ("ABILITY_CHAOS", 12),
		("ABILITY_FORCE_PUSH", 13), ("ABILITY_RAISE_DEAD", 14),
		("REQUEST_MELEE_PRESSED", 16), ("REQUEST_MELEE_RELEASED", 17), ("REQUEST_MAGIC_CHARGE", 18),
		("ACTION_INTERACT", 11), ("ACTION_LOCK_SET", 49), ("ACTION_LOCK_RELEASE", 50),
		("ACTION_SPRINT_SET", 66), ("ACTION_SPRINT_RELEASE", 67),
		("RELATION_NEUTRAL", 1), ("RELATION_ALLY", 2), ("RELATION_ENEMY", 4),
		("ATTACK_MODE_MELEE", 1), ("ATTACK_MODE_RANGED", 2), ("ATTACK_MODE_MAGIC", 3),
		("CREATURE_ELITE", 1), ("CREATURE_LIEUTENANT", 2),
		("CAMERA_DEFAULT", 0), ("CAMERA_SIMPLE", 1), ("CAMERA_GAME", 2), ("CAMERA_RANGED_AIM", 3), ("CAMERA_MAGIC_AIM", 4),
		("CAMERA_ICS", 7), ("CAMERA_FIRST_PERSON", 8), ("CAMERA_COMBAT_CANNED_ACTION", 9), ("CAMERA_CANNED_ACTION", 10),
		("CAMERA_CUTSCENE", 11), ("CAMERA_TRACKING", 12), ("CAMERA_FREE", 13), ("CAMERA_FREEZE", 14),
	};

	/// <summary>
	/// EVERY NAMED NUMBER, the one list both surfaces state: <see cref="Constants"/> and the cutscene
	/// bits (<c>CUTSCENE_FULL</c>, <c>_INTERACTIVE</c>, <c>_LOOK_AT</c>) — Lua's <c>Refii.&lt;NAME&gt;</c>, Godot's
	/// <c>constants()</c>.
	/// </summary>
	public static IEnumerable<(string Name, int Value)> NamedNumbers =>
		Constants.Concat(new[] { ("CUTSCENE_FULL", CutsceneFull), ("CUTSCENE_INTERACTIVE", CutsceneInteractive), ("CUTSCENE_LOOK_AT", CutsceneLookAt) });

	/// <summary>The three statements a cutscene is, the same on every surface.</summary>
	public const string CutsceneKindsMeaning =
		"1 FULL, a full cutscene is the top game-flow state (GUI.PlayFullCutscene queued it; the game's own GUI.IsFullCutscenePlaying answers it) — every player's; " +
		"2 INTERACTIVE, a foreground interactive cutscene holds the hero (Player.SetForegroundCutsceneInEffect, which the shipped scenes set with the vignette and the cutscene microphone); " +
		"4 LOOK_AT, the game has given that hero's held LT a camera (Player.SetOverrideCameraMode with the LOOK_AT scope: a conversation's talking camera on the speaker, a quest's look-at, a scene's own camera) — while it holds, what a held LT frames is the game's choice, not a target lock";

	private const string InCutsceneMeaning =
		"the cutscene statements holding NOW for player 1 or 2 (0: either player), as bits — " + CutsceneKindsMeaning +
		"; 0 for none. The cutscene event is the same three, on each edge. Both players are principals: on an online joiner it answers what that machine's guest states";

	/// <summary>The menu events' player: whose menu it is.</summary>
	private const string MenuPlayerMeaning =
		"whose menu: 2 on an online joiner, and in local co-op while player two owns the pause (the pause screen's owner) or their shop; 1 otherwise";

	/// <summary>The wheel-switched lock, the same on the native and Lua surfaces.</summary>
	private const string WheelSwitchesTargetMeaning =
		"that hero's target lock switches by the MOUSE WHEEL this tick — state it each tick the lock holds (it lapses two ticks later; the Lua tick runs every guest tick, so a lock stated from it never lapses): " +
		"while it holds and the hero is locked under the third-person camera the keyboard and mouse drive, the mouse's movement never switches target " +
		"and each wheel notch is one switch through the camera's own Targeting.CheckForBetterTargetInDirection (down the next target right, up the next left), " +
		"waiting on the camera's ClearToRotate as a stick flick does, at most one notch waiting; without it a mouse flick switches; a pad is untouched";

	/// <summary>The native helpers of <c>core/refii_mod.h</c>, for the Guide and the editor API.</summary>
	/// <summary>The tint primitive, the same on every surface.</summary>
	private const string EntityTintMeaning =
		"tints an entity through the game's own appearance setter (component 3, the one a corpse's grey goes through): " +
		"linear multipliers of its lit colour, 1 untouched, above 1 brighter (the modern look caps a lane at 31.875/4); " +
		"both looks draw it and co-op replicates it; a is stored but drawn by no shader (opacity is not the tint's); " +
		"untint puts back the constructor's 1, 1, 1, 1 — untint only what you tinted. Guest tick";

	private const string AppearanceMeaning =
		"the other setters of the same renderable (component 3), replicated like the tint: alpha 0..1 (0 hides the body), " +
		"a uniform scale, the saturation mask (drawn desaturated), drawable (off takes the body out of the drawn set; it keeps simulating); " +
		"reset = the constructor's 1, 1, off, on. A value the game restates each frame (a Blades sword's scale and alpha, a death's alpha ramp) wins. Guest tick";

	private const string LockMeaning =
		"the target lock as the game's own LT states it, for player 1 or 2: lock_target sets the targeting component's SetTarget (entity 0: the game's own pick), " +
		"the force byte and the look-at flag; release_lock clears the last two (a held LT is the trigger's); switch_target is the camera's own flick, left or right, " +
		"and answers the target held; locked_target is the target while player mode 4 or the force byte holds, else 0. Guest tick";

	private const string QuickActionMeaning =
		"the quick menu's own actions for player 1 or 2's hero, gated as the game gates its quick menu (the hero alive, the d-pad up, CanPlayerOpenQuickMenu for that player), " +
		"true when performed: use_item uses an inventory item by definition (the item handle by record, the game's Use, the handle released; the UI data generation moves); " +
		"equip_weapon is the weapon set's own EquipWeaponByRecord for a weapon the inventory holds — on an online joiner the pick crosses to the co-op kit (REFII_MOD_HOST_HERO_KIND, CoopHeroKit.SetLocalWeapon), " +
		"whose kit the host equips from and replicates back, so the caller never cares; perform_expression is StartExpression on the performer (component 154) for an expression it knows, released at once (a tap: the basic form); " +
		"assign_spell puts spell type 0..7 on every tier the hero knows (the spell manager's known and offered tables) through the game's own assign. Definitions and expressions are \"0x…\" record hashes. Guest tick";

	private const string QuickOptionsMeaning =
		"the quick actions with options: ungated skips the quick menu's own gate (the d-pad up and CanPlayerOpenQuickMenu: a walking-type player mode, no busy action) and is still refused for a hero not alive and while any cutscene kind holds (in_cutscene); " +
		"held (perform_expression) leaves the expression unreleased — an extendable one extends when the game's own one-second countdown (the expression action's +0x90, set in its constructor sub_8284F380) runs out and plays on until release_expression; " +
		"release_expression is the performer's own end of a held expression (sub_82414A50), the call GameAction 25 (the extend release) makes from the hero's action receiver: released before the countdown the basic form plays, released while extending the extension ends. " +
		"Natively flags are REFII_QUICK_UNGATED 1 and REFII_QUICK_HELD 2. Guest tick";

	private const string SprintMeaning =
		"the sprint as the game holds it, by player: sprinting is the sprint component's (146) flag +0x24, the byte the action receiver tests before it ends a sprint on GameAction 67 (sprint release); " +
		"end_sprint is that release's own call, sub_8284A8C0(component, 0): the flag cleared and the movement's sprint mode (52) stopped — a held sprint button sprints again only on its next press. False when not sprinting";

	private const string LocomotionMeaning =
		"per-hero locomotion: each hero's locomotion component (110) copies a LocomotionData table into its own name->value map when it loads one (sub_8283E408: at creation, indoors/outdoors, a movement-mode change), " +
		"and every read takes the active locomotion state's MovementParams first, then that map (sub_821B2280). set_locomotion writes this hero's own entry for one of the ten fields the loader copies " +
		"(MinControlMagForMovement, MinVelMagForMovement, MaxTurnSpeedStatic, MaxTurnSpeedRunning, MaxSpeed, SprintSpeedMultiplier, SprintTerminationSpeedFraction, FrictionCoefficient, MaxSpeedLateralStatic, MaxSpeedLateralRunning) through the map's own operator[], live, " +
		"and holds it across the loader's next copy; a state's own MovementParams (the Idle state's turn cap) still win while it is active. A non-number (false, nil, \"-\") stops holding the field and puts back what the last load copied. " +
		"locomotion reads the hero's own entry (the held value, or what the load copied). Guest tick";

	private const string ParryMeaning =
		"per-hero parry: each hero's player component copies HeroCombatBalanceParams into its own table when it is made (sub_823E9BF0), and the parry reads that copy by name each time (sub_829897B0): " +
		"set_parry writes this hero's InputWindowSecondsForParry (seconds) and MeleeParryWedgeAngle (degrees), live, and holds them for that player's next hero; a missing or negative value leaves that one. parry reads both. Guest tick";

	private const string ExpressionsMeaning =
		"what the quick actions choose from, by player: the performer's known expressions (its +0x88 vector: the record, counted uses; the record is the id perform_expression takes), " +
		"the spell types the hero can assign (known AND offered on some tier: the quick wheel's own test; a bit mask natively, a list in Lua), and the expression the expression wheel highlights while it is up for that player (0 / nil with no wheel up)";

	private const string RecordIntMeaning =
		"a GDB record's integer or enum property, INHERITED, through the game's typed getter (GDBEntry_GetPropTyped on the entry FindEntryByHash gives, as an int then as an enum): " +
		"definition the record's \"0x…\" hash, field the property's name (Category, WeaponType, …); 0 / nil for a record the database lacks or a property it has not got. Guest tick";

	private const string RecordStringMeaning =
		"a GDB record's string property, INHERITED, through the game's own getter (the property name's FNV-1 on the entry FindEntryByHash gives — what the d-pad reads IconGraphic with): " +
		"definition the record's \"0x…\" hash, field the property's name (NameTag, IconGraphic, …); \"\" / nil for a record the database lacks or a property it has not got. Guest tick";

	private const string StatusMeaning =
		"a status through the spell's own per-victim installer, without the spell: kind 1 electrified, 2 chaos, 3 burning (4, a vortex, is refused), " +
		"for seconds at spell level 0..4, from source (0: the entity itself; chaos takes its direction from it), no damage; clear_status runs the game's own remover; " +
		"has_status reads the component. The status_effect event fires either way. Guest tick";

	public const string SpeedMeaning =
		"the player component's own movement-speed override for player 1 or 2, what the scripts' Player.SetOverriddenMovementSpeedMultiplier " +
		"and SetPlayerMovementSpeedOverridden state: the locomotion update multiplies the whole velocity by the factor while the override holds, so 0 holds the hero in place " +
		"(turning, rolls and blocks are untouched); clear puts the byte back; speed_multiplier answers whether one holds and the factor — the game never clears a script's, " +
		"so take it only while none holds, and give it back";

	public const string ActionMeaning =
		"the action an entity's action manager (component 49) runs — 0 for none or once stopped; its address is reused by the next action anyone plays — its clock (vtable slot 7, " +
		"the float the commitment is timed by; what writes it is unproven), and the interrupt permission the rule reads: a same-priority action gets in only once the running one has " +
		"granted ALLOW_ACTION_INTERRUPT (strikes, rolls and plain blocks play at priority 6; the preemptive block and parry at 8 need none); allow_action_interrupt grants it exactly as " +
		"the animation event does — a mod that cancels that anim_event (a committed swing) grants it later. Any creature, both heroes";

	public const string ActionKindMeaning =
		"what an action IS: its definition is a Lua table of the game's scripts (Action.SetCurrentAction(entity, def)), not a GDB record and not kept on the object; every action is " +
		"made from it by the one factory (sub_8218A8E0, hooked as rex_CActionFactory_CreateFromDefinition), which reads the table's Type — EScriptableAction of the game's own " +
		"scriptableactionenum.lua: 0 PLAY_ANIMATION, 1 MELEE_STRIKE, 2 STRAFE (the roll), 16 BATCH, 48 LOOP, ... 88 DIG. action_type answers that id (-1 for an action the factory " +
		"never saw), action_name its enum name (\"\" / nil unknown); melee_swing and anim_event carry both. Named for the last 64 actions made; an address reused later takes the later type. " +
		"A strike is never factory-made (the manager clones it), so it answers -1; melee_swing's strike and flourish state what it is";

	public const string FrontEndMeaning =
		"the front end's own state, on the title and its screens (no game, no hero, no save loaded): the signed-in pad's user index (-1 / nil none: dword_83496AF8 against the " +
		"sign-in mask dword_83496A70, as the New Game guard reads them), the signed-in profile's saved games on every device (rex_lhSaveManager_CountSavedGamesAllDevices on the " +
		"front end's save manager — a content enumeration, ask once a screen; -1 / nil with no front end), and the current Gameface presentation's name (\"FrontendMainMenu\", ...)";

	public const string LivingMeaning =
		"in the world and, for a creature, not yet died: alive's bit and the creature component's own byte the target selector tests (1 from construction, 0 once Die ran); " +
		"an entity without a creature component counts as living while in the world";

	public const string SlotMeaning =
		"the spell player 1 or 2's hero holds in slot 0..4 (0 Lightning .. 7 Dead Rising), -1 for none: the spell manager's slot table, row 0, what the launch reads";

	/// <summary>vortex_held's meaning on every surface.</summary>
	public const string VortexHeldMeaning =
		"who the live vortex of a cast holds now, by the serial its events carry: the component's own held list (+0x1B0, the vector its update appends a capture to and erases a release from), in its order, " +
		"each a handle while it is in the world — whether or not the vortex ever hit it. vortex_caught states each capture and release as it happens";

	/// <summary>spell_balance's meaning on every surface.</summary>
	public const string SpellBalanceMeaning =
		"one power (level 0..4) of a spell for a caster's definitions (its spell manager's +400): the Targeted definition sub_8260D580 hands the factory, fed to the class's own data read as the factory feeds a new spell — its defaults per power under the definition's GDB PowerN values. " +
		"status_seconds / status_spread: the status the class's Start installs on each victim, its seconds before the victim's own factor and the spread of the uniform draw the Start adds (the installer gets seconds × factor + [0, spread)) — Lightning ElectrifyDuration and 5, Fireball CreatureBurnTime and 0, Chaos Duration and 2; " +
		"duration: Vortex's Duration, the twister's life; swords: Blades' NumSwords, the swords one cast makes. A field the class does not carry is absent (-1 on the C and query surfaces)";

	/// <summary>host_serial / local_serial's meaning on every surface.</summary>
	public const string CastSerialMeaning =
		"one cast, one identity online: the game makes a cast's spell on both machines, each with its own serial (the deciding machine's cast action replicates its Start as message 0x5B, 0x5C or 0x5E and the other machine's copy launches through its own factory). " +
		"A cast is paired by that replicated release — the caster's network id and the fields the message carries, in the order the game's guaranteed, sequential channel starts them — and its spells by creation order; the HOST's serial is the shared name (hits are decided there, and a relayed hit_landed carries this machine's serial once paired). " +
		"host_serial(serial) is the host's serial of this machine's spell, local_serial(host_serial) this machine's spell for the host's: each the serial itself on the host, alone and in local co-op; on the joiner 0 / nil / ok false while unpaired. A spell no cast action made (cast_spell) never pairs";

	public const string CastPartsMeaning =
		"what a cast put in the world, by the serial its hits and events carry (spell_created / vortex_started / fireball_impact, a hit's spell_serial), read live and validated: " +
		"the spell object while it still carries the serial, type, targeted, level, caster; the live vortex component and its entity; the fireball's shot entity while it is in the world and whether it detonated; " +
		"the Blades' live swords; and the origin — the twister's point, the fireball's impact point once reported, else its shot's position, else the caster's. Nil / ok false for a cast the factory never saw";

	private const string KnockbackMeaning =
		"a hit of the game's own shape through its one receiver, no damage and no attacker: knockback is a Force Push's knockdown hit along dir scaled by force " +
		"(whether the physics keeps the length is unproven), stagger the flinch a 0-damage melee tap plays. Hit and HitLanded fire; HitResponse is not offered. Guest tick";

	private const string SoundMeaning =
		"the game's own cue player (the body of the scripts' Sound.PlayEvent): play_sound starts a cue on an entity's sound component's emitter " +
		"(or at its position when it has none), play_sound_at on an emitter of the API's own at a point (eight kept, the oldest freed); " +
		"cue names are the audio banks' event names (an .adb's strings), the cue's own volume and attenuation apply, and the tag (may be empty) " +
		"is what stop_sound matches on that entity's emitter (entity 0: the point emitters), fading over milliseconds. " +
		"play_music / stop_music are SoundTools.PlayMusic / StopMusic's own bodies: the MUSIC_* cues, stopped with the game's 2 s fade. Guest tick";

	private const string AnimationMeaning =
		"a named animation as the shipped AI plays one: Action.SetCurrentAction with a PLAY_ANIMATION action (Anim the GDB animation record's name, " +
		"OverrideLooking, SpeedMultiplier, BlendInTime), wrapped in a LOOP with NumLoops for loops above 1, so the game's own evaluator plays it " +
		"and the pose feed shows it; a higher-priority action keeps the body. stop_animation is Action.FinishScriptedActions. Guest tick";

	private const string OpinionSetMeaning =
		"a villager's opinion of a hero, per hero: modify_opinion is the game's own ModifyAxisValue on axis 0 love 1 fear 2 attractiveness 3 morality 4 respect, " +
		"clamped to [-1, 1], in player two's own block for their hero (opinion_modified fires); add_deed posts the deed message the game's debug " +
		"post sends (MESSAGE_EVENT_DEED_PERSON from the hero to the villager at the hero's position, extra_data the Deed record's id), so the " +
		"record's strength and kind and the villager's own rules apply (opinion_deed fires, routed to the doer's player). Guest tick";

	/// <summary>NPC orders, the same on every surface (the quests' own entries, run in the game VM).</summary>
	public const string NpcMeaning =
		"orders a creature as the shipped quests do, through the game's own entries in its Lua VM: npc_follow is Follow.FollowEntity (the follow component, " +
		"radius the quests' 2), npc_go_to ScriptFunction.StartScriptControlledMovement (the AI's script move-to, not waited for), npc_flee the AI's " +
		"Navigation.FleeFromEntity at sprint, npc_attack CombatRegister:SetScriptTarget (the enemy relationship plus the combat info's ScriptTarget), " +
		"npc_set_hostile Faction.AddTemporaryEntityRelationship FACTION_STATUS_ENEMY / RemoveTemporaryEntityRelationship against that entity — a hero is " +
		"its own entity, so player two's hero is hostile on its own — and npc_stop ends them all (StopFollowing, StopScriptControlledMovement, ClearScriptTarget). " +
		"The AI mind may retake the body later (combat, a cutscene), which the call cannot see. Guest tick";

	/// <summary>The game clock and the environment theme, the same on every surface.</summary>
	public const string TimeWeatherMeaning =
		"set_time_of_day sets the game clock to hours 0..24 through Timing.SetTimeOfDayAbsolute's own body on the time manager (clamped; an online session " +
		"tells the partner; time_of_day fires from the next clock update), time_of_day reads it back; set_weather blends the GAME's environment theme — " +
		"its weather and sky look — as the quests do, EnvironmentTheme.BlendToEnvironmentTheme(theme, weight 0..1, seconds), so weather_theme fires and a " +
		"handler may cancel it; theme is an EnvTheme* record name. The Dynamic Sky's own weather override (dynamic_sky_weather) is the host's switch, not this; " +
		"in co-op the host's weather is everyone's. Guest tick";

	public static readonly IReadOnlyList<ModApiHelper> Helpers = new[]
	{
		new ModApiHelper("uint32_t refii_mod_player_hero(base, player)", "player 1's or 2's hero entity, or 0"),
		new ModApiHelper("uint32_t refii_mod_local_hero(base)", "the local player's hero, or 0: player two's on an online joiner, player one's on a host or alone, and in local co-op the hero of whoever owns the open menus (both are local there: name a player when the answer must not follow the menus)"),
		new ModApiHelper("int32_t refii_mod_player_of(base, entity)", "1 or 2 when the entity is that player's hero, else 0"),
		new ModApiHelper("int32_t refii_mod_player_for(ctx, base, entity)", PlayerForMeaning + " — Lua's Refii.player_for, natively"),
		new ModApiHelper("int32_t refii_mod_world_here(base)", "whether this machine runs the shared world (AI, hits, NPCs): alone, local co-op, the online host; make world effects only here"),
		new ModApiHelper("int32_t refii_mod_menu_player(base)", "the player whose menus are open here, 1 or 2: an ability row publishes and buys for them"),
		new ModApiHelper("int32_t refii_mod_opinion(base, villager, player, float out[5])", "how a villager feels about that hero: love, fear, attractiveness, morality, respect; a set per hero, player two's moved only by player two's deeds"),
		new ModApiHelper("int32_t refii_mod_forgive_crimes(ctx, base, villager, hero) / refii_mod_discovered_crime(ctx, base, villager, hero) / refii_mod_perceived_renown(ctx, base, villager, hero, int32_t* out)", "a villager's crimes and renown for one hero: forgives only that hero's crimes the villager knows of (how many; the other hero's stand), whether it discovered one of theirs, the renown it credits them with — the shipped Villager natives name no hero"),
		new ModApiHelper("int32_t refii_mod_coop_mode(base)", "0 alone, 1 local co-op, 2 online hosting, 3 online joined"),
		new ModApiHelper("uint32_t refii_mod_focus(base, player)", "the living entity that hero is focused on (their targeting), or 0; meaningful only where that hero is played (refii_mod_player_here)"),
		new ModApiHelper("void refii_mod_wheel_switches_target(base, hero)", WheelSwitchesTargetMeaning),
		new ModApiHelper("void refii_mod_wheel_switches_target_sticky(ctx, base, hero, on)", WheelStickyMeaning + ". Guest tick"),
		new ModApiHelper("float refii_mod_player_value(player, key, fallback)", "a player's value, the same on both co-op machines (fallback when unset)"),
		new ModApiHelper("int32_t refii_mod_player_get(player, key, float* out)", "whether the player has a value under key, written to out when not null"),
		new ModApiHelper("void refii_mod_player_set(player, key, v) / refii_mod_player_add(player, key, d, saved)", "writes a player's value on either machine; the hero's owner applies it (an add merges)"),
		new ModApiHelper("void refii_mod_player_save(player, key, v)", "a SAVED player value: kept with that hero's own save, back when it loads"),
		new ModApiHelper("int32_t refii_mod_player_text(player, key, out, capacity) / refii_mod_player_set_text(player, key, text) / refii_mod_player_save_text(player, key, text) / refii_mod_player_hash(player, key, uint32_t* out) / refii_mod_player_save_hash(player, key, hash)",
			PlayerTextMeaning + " player_text answers the length (-1 for none); REFII_MOD_PLAYER_TEXT_MAX"),
		new ModApiHelper("int32_t refii_mod_player_here(player)", "whether this machine plays that hero: run a per-player refill or timer only there"),
		new ModApiHelper("int32_t refii_mod_event_replica()", "the event is a copy of something the other co-op machine decides — the game's own copy (verdicts apply) or a RELAYED one (it already happened): player-state writes are dropped; skip world and body effects"),
		new ModApiHelper("int32_t refii_mod_opinion_scope_two()", "1 while the game is moving player two's opinions on this thread (an OpinionModified for hero two)"),
		new ModApiHelper("refii::mods::IsPlayerHero(base, entity)", "whether the entity is either player's hero"),
		new ModApiHelper("int32_t refii_mod_subscribe(kind, fn, user, priority, ...) / void refii_mod_dispatch(kind, event)", "the bus underneath REFII_MOD_ON; a kind at or above REFII_EVENT_COUNT is a mod's own, raised with refii_mod_dispatch"),
		new ModApiHelper("uint32_t refii_mod_component(base, entity, id)", "component id of an entity (33 player, 36 health, 56 spells, 57 stats, 164 experience), or 0"),
		new ModApiHelper("int32_t refii_mod_entity_alive(base, entity)", "whether the entity is in the world"),
		new ModApiHelper("int32_t refii_mod_entity_position(ctx, base, entity, float out[3])", "the entity's position through its own placement getter"),
		new ModApiHelper("int32_t refii_mod_health(base, entity, float* current, float* max)", "current and maximum health"),
		new ModApiHelper("int32_t refii_mod_set_health(base, entity, value)", "writes current health, clamped to [0, max]"),
		new ModApiHelper("int32_t refii_mod_ability_level(base, entity, type)", "the level of ability 1..14 (1 Heavy Weapons, 2 Physique, 3 Toughness, 4 Ranged Weapons, 5 Accuracy, 6 Speed, 7 Lightning .. 14 Raise Dead), or -1"),
		new ModApiHelper("int32_t refii_mod_experience(base, entity, pool, float* out)", "the experience in a pool: 0 General 1 Strength 2 Skill 3 Will"),
		new ModApiHelper("int32_t refii_mod_ability_cost(ctx, base, entity, type, level)", "the game's own price of an ability level"),
		new ModApiHelper("int32_t refii_mod_give_experience(ctx, base, entity, pool, amount)", "adds (or takes, negative) experience through the game's Modify, outside the lifetime total"),
		new ModApiHelper("int32_t refii_mod_spend_experience(ctx, base, entity, pool, amount)", "spends through the game's Spend: a shortfall comes from General, unchecked"),
		new ModApiHelper("uint32_t refii_mod_cast_spell(ctx, base, caster, type, targeted, target, level, pos, dir)", "a real spell through the game's spell factory, no will cost; pos/dir may be null"),
		new ModApiHelper("uint32_t refii_mod_spell_serial(base, spell)", "a spell object's cast number"),
		new ModApiHelper("uint32_t refii_mod_live_spell(ctx, base, type, caster)", "the caster's live spell object of a type (never a Vortex)"),
		new ModApiHelper("uint32_t refii_mod_world(base)", "the world object"),
		new ModApiHelper("uint32_t refii_mod_entity_definition(base, entity)", "the GDB record hash of what the entity is"),
		new ModApiHelper("int32_t refii_mod_entity_name(base, entity, char* out, size)", "the entity's instance name"),
		new ModApiHelper("int32_t refii_mod_relation(ctx, base, entity, other)", "the game's faction rule: 1 neutral, 2 ally, 4 enemy"),
		new ModApiHelper("int32_t refii_mod_set_position(ctx, base, entity, xyz)", "moves the entity through its placement's own setter"),
		new ModApiHelper("int32_t refii_mod_facing(ctx, base, entity, float* out_xyz) / refii_mod_velocity(ctx, base, entity, float* out_xyz) / refii_mod_set_facing(ctx, base, entity, const float* xyz)", "the bodies game Lua's Physics.GetFacingVector / GetVelocity / SetFacingVector bind, on the placement their binder hands them (an entity in the world); guest axes, z up; set_facing keeps the facing level (the game drops z) and does nothing for a vector shorter than 1e-4; a hero turns through Player.SetFacingVector"),
		new ModApiHelper("int32_t refii_mod_entity_tint(ctx, base, entity, r, g, b, a) / refii_mod_entity_untint(ctx, base, entity)", EntityTintMeaning),
		new ModApiHelper("int32_t refii_mod_entity_alpha(ctx, base, entity, alpha) / refii_mod_entity_scale(ctx, base, entity, scale) / refii_mod_entity_saturation_mask(ctx, base, entity, on) / refii_mod_entity_drawable(ctx, base, entity, on)", AppearanceMeaning),
		new ModApiHelper("int32_t refii_mod_lock_target(ctx, base, player, entity) / refii_mod_release_lock(ctx, base, player) / uint32_t refii_mod_switch_target(ctx, base, player, left) / uint32_t refii_mod_locked_target(ctx, base, player)", LockMeaning),
		new ModApiHelper("int32_t refii_mod_in_cutscene(ctx, base, player)", InCutsceneMeaning + ". Guest tick"),
		new ModApiHelper("int32_t refii_mod_apply_status(ctx, base, entity, kind, seconds, level, source) / refii_mod_clear_status(ctx, base, entity, kind) / refii_mod_has_status(base, entity, kind)", StatusMeaning),
		new ModApiHelper("int32_t refii_mod_knockback(ctx, base, entity, dir, force) / refii_mod_stagger(ctx, base, entity)", KnockbackMeaning),
		new ModApiHelper("int32_t refii_mod_set_speed_multiplier(base, player, factor) / refii_mod_clear_speed_multiplier(base, player) / refii_mod_speed_multiplier(base, player, float* out)", SpeedMeaning),
		new ModApiHelper("uint32_t refii_mod_current_action(base, entity) / int32_t refii_mod_action_elapsed(ctx, base, action, float* out) / refii_mod_action_interruptible(base, action) / refii_mod_allow_action_interrupt(base, action)", ActionMeaning),
		new ModApiHelper("int32_t refii_mod_allow_action_interrupt_at(ctx, base, action, seconds)", InterruptAtMeaning + ". Guest tick"),
		new ModApiHelper("int32_t refii_mod_action_type(base, action) / refii_mod_action_name(base, action, char* out, size) / const char* refii_mod_scriptable_action_name(type)", ActionKindMeaning),
		new ModApiHelper("int32_t refii_mod_signed_in_user(base) / refii_mod_save_count(ctx, base) / refii_mod_frontend_screen(ctx, base, char* out, size)", FrontEndMeaning),
		new ModApiHelper("int32_t refii_mod_entity_living(base, entity)", LivingMeaning),
		new ModApiHelper("int32_t refii_mod_spell_in_slot(base, player, slot)", SlotMeaning),
		new ModApiHelper("int32_t refii_mod_spell_parts(ctx, base, serial, refii_mod_cast_parts* out)", CastPartsMeaning + "; out: spell, type, targeted, level, caster, caster_player, vortex, anchor, swords[8], sword_count, origin[3], has_origin, exploded"),
		new ModApiHelper("int32_t refii_mod_vortex_held(base, serial, uint32_t* out, int32_t max)", VortexHeldMeaning + "; writes up to max entities and returns how many it holds, -1 when no live vortex carries serial"),
		new ModApiHelper("int32_t refii_mod_spell_balance(ctx, base, caster, type, level, refii_mod_cast_balance* out)", SpellBalanceMeaning + "; out: status_seconds, status_spread, duration, swords; 0 (all -1) for another type or a caster with no spell manager. Guest tick"),
		new ModApiHelper("uint32_t refii_mod_host_serial(base, serial) / refii_mod_local_serial(base, host_serial)", CastSerialMeaning),
		new ModApiHelper("int32_t refii_mod_play_sound_on(ctx, base, entity, cue, tag) / refii_mod_play_sound_at(ctx, base, xyz, cue, tag) / refii_mod_stop_sound(ctx, base, entity, tag, fade_ms) / refii_mod_play_music(ctx, base, name) / refii_mod_stop_music(ctx, base, name)", SoundMeaning),
		new ModApiHelper("int32_t refii_mod_play_animation(ctx, base, entity, name, loops, blend_seconds, speed) / refii_mod_stop_animation(ctx, base, entity)", AnimationMeaning),
		new ModApiHelper("int32_t refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta) / refii_mod_add_deed(ctx, base, villager, hero, deed)", OpinionSetMeaning),
		new ModApiHelper("int32_t refii_mod_npc_follow(ctx, base, entity, target, radius) / refii_mod_npc_go_to(ctx, base, entity, xyz) / refii_mod_npc_flee(ctx, base, entity, from) / refii_mod_npc_attack(ctx, base, entity, target) / refii_mod_npc_set_hostile(ctx, base, entity, other, on) / refii_mod_npc_stop(ctx, base, entity)", NpcMeaning),
		new ModApiHelper("int32_t refii_mod_set_time_of_day(ctx, base, hours) / refii_mod_time_of_day(base, hours) / refii_mod_set_weather(ctx, base, theme, weight, seconds)", TimeWeatherMeaning),
		new ModApiHelper("int32_t refii_mod_modify_health(ctx, base, entity, delta, attacker)", "heals or hurts through the game's own Modify (death included)"),
		new ModApiHelper("int32_t refii_mod_level_name(base, char* out, size)", "the loaded level's name"),
		new ModApiHelper("int32_t refii_mod_gold(base, entity, int32_t* out) / refii_mod_give_gold(ctx, base, entity, delta)", "a purse, read and changed through the game's own Modify"),
		new ModApiHelper("int32_t refii_mod_item_count(base, entity, definition) / uint32_t refii_mod_add_item(ctx, base, entity, definition)", "an inventory, counted and added to through the game's own add"),
		new ModApiHelper("int32_t refii_mod_add_items(ctx, base, entity, definition, count, uint32_t* out_last)", AddItemsMeaning + ". Guest tick"),
		new ModApiHelper("int32_t refii_mod_run_game_lua(ctx, base, chunk)", "runs a Lua chunk in the game VM"),
		new ModApiHelper("int32_t refii_mod_run_frontend_lua(ctx, base, chunk)", "runs a Lua chunk in the mods' front-end Lua (the title and the front end: the host's state for the current front-end presentation, every mod's mod/frontend.lua run in it first); 0 when no presentation is current"),
		new ModApiHelper("int32_t refii_mod_game_lua_int(ctx, base, expr, lo, hi, int32_t* out)", "an integer Lua expression's value, by a search of assertions"),
		new ModApiHelper("uint32_t refii_mod_lua_generation()", "steps on every Lua rebuild and save load"),
		new ModApiHelper("int32_t refii_mod_save_set_int(ctx, base, key, value)", "RefiiModSave.<key> = value: persists with the save game"),
		new ModApiHelper("int32_t refii_mod_save_get_int(ctx, base, key, lo, hi, int32_t* out)", "reads RefiiModSave.<key>; do it on a generation change"),
		new ModApiHelper("refii::mods::Publish(key, value) / Published(key, out) / Setting(key, fallback)", "mod values to Godot (\"mod:<key>\" in the UI), another mod's values, and settings.json"),
		new ModApiHelper("refii::mods::LoadU8/U16/U32/F32, StoreU8/U32/F32 (base, address)", "big-endian guest memory"),
		new ModApiHelper("refii::mods::CallGuest(ctx, base, address) / CallVirtual(ctx, base, object, slot)", "guest calls; Frame(ctx) for one that needs stack"),
		new ModApiHelper("refii::mods::GetVector(ctx, 1|2, out[3]) / SetVector(ctx, 1|2, xyz)", "guest vector registers v1 / v2: positions and directions"),
		new ModApiHelper("REFII_MOD_ON(Kind, fn) / REFII_MOD_ON_PRIORITY(Kind, fn, p)", "subscribes void fn(refii::mods::KindEvent&)"),
		new ModApiHelper("REFII_MOD_ON_TICK(fn) / refii_mod_register_tick(fn)", "the plain per-tick callback (the Tick event is the same moment)"),
		new ModApiHelper("REX_EXTERN(name) { ... } + REFII_MOD_ORIGINAL(name)", "a direct hook on a game function, for a seam no event covers"),
		new ModApiHelper("uint32_t refii_mod_handle(address) / refii_mod_handle_address(handle)", "the handle Lua and Godot hold for an object, and back"),
		new ModApiHelper("uint32_t refii_mod_fnv1(text)", "the game's FNV-1 of a name: a GDB record's hash, a text tag's key"),
		new ModApiHelper("int32_t refii_mod_renown / refii_mod_morality / refii_mod_purity(ctx, base, entity, out)", "the game's own getters: renown (with bonuses), morality and purity (clamped)"),
		new ModApiHelper("uint32_t refii_mod_cast_spell_with(ctx, base, caster, type, targeted, target, level, pos, dir, const refii_mod_cast_options* options)", "refii_mod_cast_spell with { tag, damage_scale, at_kind, at_entity, at_point }: the tag rides the cast's spell_created, vortex_started, hit, hit_landed, fireball_impact and status_applying by its serial (the registry's counter, read before the factory builds the spell, so a hit inside the factory carries it); its hits deal damage × damage_scale for that delivery, where the hit is decided; at_kind REFII_MOD_CAST_AT_ENTITY / _POINT: at casts the Targeted class by the class's own rule for that place, from pos as the origin (the caster's when absent) and dir as the facing when the origin stands on the place: Lightning at an entity starts on the point its bolt strikes (sub_825FEFC8 with the power's CreateImpactFX and 0.25: a bolt of zero length), at a point one 15.0 reach above it facing down (outside the re-acquire's 1.35 height); Fireball at an entity within the 1.0 its Start impacts at once, set back toward the origin; Blades at the entity's position or the point (its swords are made there); Vortex set back 1.25 along the facing (its twister spawns on the place); Chaos at an entity on the point its stream strikes; Force Push from the origin toward the place; Fireball and Chaos at a point, and Slow Time and Raise Dead, are refused with a mod error"),
		new ModApiHelper("int32_t refii_mod_can_save(ctx, base, player) / refii_mod_save_game(ctx, base, player)", "the game's own save, by player: can_save is Player.IsAbleToSaveGame for that player's hero (saving enabled and the player's user holding a storage device); save_game is AutoSaveGame(true, true) where can_save allows it — the game's whole save on the next world update, each hero into its own package, save_written following per package"),
		new ModApiHelper("int32_t refii_mod_modify_renown(ctx, base, entity, int32_t delta) / refii_mod_modify_morality / refii_mod_modify_purity(ctx, base, entity, float delta)", "the game's own Stats.ModifyRenown / ModifyMorality / ModifyPurity bodies (rex_lhHeroStats_Modify*): the renown / morality / purity events fire, the game clamps, unlocks and splits renown with a co-op partner; morality and purity change only where the session is decided (the host)"),
		new ModApiHelper("int32_t refii_mod_physique(base, entity, float* fat, float* height) / refii_mod_age(base, entity, float* years)", "physique from the stats component, age from the age component (122)"),
		new ModApiHelper("uint32_t refii_mod_morph_id(\"<mod>.<name>\") / int32_t refii_mod_morph_list(uint32_t* out, capacity) / int32_t refii_mod_morph_registered(id, float* default, float* min, float* max) / int32_t refii_mod_morph_weight(player, id, float* out) / int32_t refii_mod_set_morph_weight(player, id, weight)", ModMorphMeaning),
		new ModApiHelper("int32_t refii_mod_appearance(ctx, base, player, refii_mod_appearance_t* out) / void refii_mod_set_appearance(ctx, base, player, const refii_mod_appearance_t* inputs) / void refii_mod_set_appearance_automatic(ctx, base, player, on)", "Murgo's Mirror: the look in force for player's hero (automatic, tan, fat, height, strength, skill, will, morality, purity, age — manual the stated inputs, automatic the game's own) · switch to manual with the inputs that are not NaN, the rest as the hero looks · back to the game's own (1) or the held manual inputs (0); the player store's character_morphs.* set, kept in the owning player's Mirror settings"),
		new ModApiHelper("int32_t refii_mod_inventory_stack(base, entity, index, uint32_t* definition, int32_t* count)", "stack index of an inventory, in the game's order; false past the end"),
		new ModApiHelper("int32_t refii_mod_equipped(base, entity, uint32_t* melee, uint32_t* ranged)", "the weapon set's melee and ranged definition hashes"),
		new ModApiHelper("int32_t refii_mod_use_item(ctx, base, player, definition) / refii_mod_equip_weapon(ctx, base, player, definition) / refii_mod_perform_expression(ctx, base, player, expression) / refii_mod_assign_spell(ctx, base, player, spell)", QuickActionMeaning),
		new ModApiHelper("int32_t refii_mod_quick_action(ctx, base, player, kind, record, flags) / refii_mod_release_expression(ctx, base, player)", QuickOptionsMeaning + "; kind 1 use_item, 2 perform_expression, 3 equip_weapon, 4 assign_spell"),
		new ModApiHelper("int32_t refii_mod_sprinting(base, player) / refii_mod_end_sprint(ctx, base, player)", SprintMeaning),
		new ModApiHelper("int32_t refii_mod_inventory_usable(ctx, base, entity, index)", "whether stack index of the entity's inventory (refii_mod_inventory_stack's order) is usable from the inventory, by the item's own test (sub_827649C8: the definition's InventoryItemComponent's UsableFromInventory — what the Items page's use and A button ask). Guest tick"),
		new ModApiHelper("int32_t refii_mod_set_locomotion(ctx, base, player, const char* key, float value) / refii_mod_locomotion(ctx, base, player, key, float* out)", LocomotionMeaning),
		new ModApiHelper("int32_t refii_mod_set_parry(ctx, base, player, float window_seconds, float wedge_degrees) / refii_mod_parry(ctx, base, player, float* window, float* wedge)", ParryMeaning),
		new ModApiHelper("int32_t refii_mod_expression(base, player, index, uint32_t* record, int32_t* uses) / refii_mod_spells_known(base, player, uint32_t* mask) / uint32_t refii_mod_selected_expression(base, player)", ExpressionsMeaning + "; expression is false past the end"),
		new ModApiHelper("int32_t refii_mod_record_string(ctx, base, definition, field, out, size)", RecordStringMeaning),
		new ModApiHelper("int32_t refii_mod_record_int(ctx, base, definition, field, int32_t* out)", RecordIntMeaning),
		new ModApiHelper("int32_t refii_mod_record_number(ctx, base, definition, field, float* out) / refii_mod_record_bool(ctx, base, definition, field, int32_t* out) / refii_mod_record_exists(ctx, base, definition)", RecordNumberMeaning),
		new ModApiHelper("int32_t refii_mod_record_ref(ctx, base, definition, field, uint32_t* out)", RecordRefMeaning),
		new ModApiHelper("int32_t refii_mod_creature_tier(base, entity)", CreatureTierMeaning + "; -1 without a creature component"),
		new ModApiHelper("int32_t refii_mod_entities_near(ctx, base, xyz, radius, creatures_only, out[], max)", "entities within a radius, nearest first"),
		new ModApiHelper("int32_t refii_mod_line_of_sight(ctx, base, from, to)", "the game's physics raycast: true when nothing is between"),
		new ModApiHelper("uint32_t refii_mod_spawn(ctx, base, definition, xyz)", "an entity created through the game's own CreateEntityFromIDAtPosition"),
		new ModApiHelper("uint32_t refii_mod_play_fx(name, xyz, options) / refii_mod_play_fx_on(name, entity, options)",
			"an effect of the particle bank or a mod's fx/effects/, drawn by Godot at a world position or riding an entity in its own frame (it turns with the entity; offset along the entity's axes); " +
			"options (refii_mod_fx_options, null for refii_mod_fx_default_options()): duration, scale, tint, intensity, loop, offset, at (a model helper: " +
			"the effect stands at that .mdl.gmd point in its own frame, on a rigged body on its bone; refii_mod_play_fx_at(name, entity, helper, options) is the same); returns the id. Any thread"),
		new ModApiHelper("void refii_mod_stop_fx(id)", "stops a played effect by any surface's id (native, Lua or Godot)"),
		new ModApiHelper("uint32_t refii_mod_mark(player, entity, xyz, icon, text) / refii_mod_unmark(id)", MarkMeaning + " Any thread"),
		new ModApiHelper("uint32_t refii_mod_world_meter(player, entity, xyz, value, max, style) / int32_t refii_mod_mark_meter(id, value, max, style)", WorldMeterMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_fade(player, to_black, seconds)", FadeMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_meter(player, id, label, value, max, style) / refii_mod_meter_remove(id)", MeterMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_input_claim(player, action, on)", InputClaimMeaning),
		new ModApiHelper("int32_t refii_mod_press(player, button, held) / refii_mod_tap(player, button, ticks)", PressMeaning + " 1 when posted. Any thread"),
		new ModApiHelper("int32_t refii_mod_charge_spell(ctx, base, player, type, held)", ChargeSpellMeaning + " 1 when taken. Guest tick"),
		new ModApiHelper("int32_t refii_mod_attack_mode(player)", AttackModeMeaning + " REFII_ATTACK_MODE_*; the guest tick holds the last mode Godot stated. Guest thread"),
		new ModApiHelper("int32_t refii_mod_camera_mode(player)", CameraModeMeaning + " REFII_CAMERA_*; stated from the game VM by the core camera chunk after each CameraManager:FrameUpdate. Guest thread"),
		new ModApiHelper("int32_t refii_mod_camera_override(player, mode, fov, pitch_limit, blend, flags)", CameraOverrideMeaning + " fov / pitch_limit 0 or less and blend below 0 are the camera's own; flags REFII_CAMERA_HOLD_AIM (1) is the aim option. Guest thread"),
		new ModApiHelper("float refii_mod_camera_fov(base, player)", CameraFovMeaning + " Guest thread"),
		new ModApiHelper("int32_t refii_mod_camera_state(ctx, base, player, call, arguments)", CameraShapeMeaning + " The native form of every shape call: call its name (\"camera_tune\" ...), arguments what follows the player as the Lua call writes it (\"{pitch_speed=0.09}\", \"0\", \"true,{shared=true}\", \"nil\"); owner \"native\"; camera_offset and camera_shake go the same way (\"{right=0.3,up=0.1}\", \"{kind='quake',level=0.6,seconds=0.4}\"). 1 when the call ran. Guest tick thread"),
		new ModApiHelper("int32_t refii_mod_camera_pose(player, out12)", CameraPoseMeaning + " out12: position, forward, up, fov, vfov (degrees), cut. 1 when drawn. Any thread"),
		new ModApiHelper("int32_t refii_mod_camera_stated(player, out11)", CameraStatedMeaning + " out11: position, forward, up, fov (degrees), cut. 1 when stated. Any thread"),
		new ModApiHelper("int32_t refii_mod_camera_view(player, out4)", CameraViewMeaning + " out4: x, y, width, height. 1 when drawn. Any thread"),
		new ModApiHelper("int32_t refii_mod_camera_screen_mode(split)", CameraScreenModeMeaning + " REFII_CAMERA_SCREEN_* (0 shared, 1 split_vertical, 2 split_horizontal, 3 hybrid), -1 before the shell stated one; split (may be null) " + CameraSplitMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_camera_ray(player, x, y, out6)", CameraRayMeaning + " out6: origin, direction. 1 when drawn. Any thread"),
		new ModApiHelper("int32_t refii_mod_hide_part(entity, part, on) / refii_mod_hide_part_for(entity, part, on, player)", HidePartMeaning + " refii_mod_hide_part is player 0. Posted to Godot. Any thread"),
		new ModApiHelper("int32_t refii_mod_hud_icon(player, element, texture) / refii_mod_hud_slide(player, element, slide) / refii_mod_hud_hold(player, piece, on)", HudOverrideMeaning),
		new ModApiHelper("uint32_t refii_mod_light(entity, xyz, rgb, energy, range, options) / refii_mod_light_remove(id)",
			LightMeaning + " options (refii_mod_light_options, null for none): offset, at (a model helper, as play_fx_on's), flicker; direction, cone, attenuation for a spot. Any thread"),
		new ModApiHelper("int32_t refii_mod_send_to_partner(mod, name, payload)", PartnerMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_signal(name, payload) / refii_mod_publish_text(key, text)", SignalMeaning + " REFII_MOD_SIGNAL_NAME_MAX, REFII_MOD_SIGNAL_PAYLOAD_MAX. 1 when posted. Any thread"),
		new ModApiHelper("int32_t refii_mod_set_menu_rows(player, menu, rows)",
			"states a menu list's rows for player 1 or 2 (0: every player) — what a ui/templates.json menu or entry with \"source\": \"rows\" " +
			"(or rows:<list>) shows, at once if open; rows is one text, \"name|label|command|ghosted\" split by ';' ('\\' escapes), " +
			"command any row verb (Mod:lua:, Opens:, Screen:open:); \"\" clears. Up to REFII_MOD_MENU_ROWS_MAX (32768) bytes cross in parts; past it the " +
			"rows are cut at the last whole row and the mod errors channel says so. Any thread. " + MenuLabelMeaning + " " + HostListMeaning),
		new ModApiHelper("int32_t refii_mod_open_screen(player, screen)",
			"opens a host-owned screen by id for player 1 or 2 (0: the menu owner here) — the Screen:open:<id> verb: the game's (1013 " +
			"achievements, 1014 multiplayer) or a ui/templates.json screens record a mod states (id 1000+, a menu and a presentation, " +
			"filled by its provider — \"data\" binds fields to mod:/player: values); it stands over the pause book. Any thread"),
		new ModApiHelper("int32_t refii_mod_open_menu(player, name)",
			"opens a menu a mod defines in ui/templates.json for player 1 or 2 (0: whoever owns the menus here) over the list that has the input, " +
			"or on its own with none up — in play or on the title (it takes the pad; B at its root closes it); shown only where that player is played. Any thread"),
		new ModApiHelper("uint32_t refii_mod_message_box(player, title, text, accept, cancel)",
			"the game's own box for player 1 or 2 (0: the menu owner here) — in game the query or message box, on the front end its dialog: " +
			"a question with cancel non-null (null or \"\" captions keep the box's own), else a notice; returns the id mod_message_box carries. Any thread"),
		new ModApiHelper("uint32_t refii_mod_notice(player, title, text, seconds)",
			"a notice for player 1 or 2 (0: the menu owner here) on the notice channel the game's co-op notices use — in game the HUD's info box " +
			"for seconds (0: the channel's length), on the front end its dialog; notices queue; returns the id mod_notice carries (0 shown, -1 never). Any thread"),
		new ModApiHelper("int32_t refii_mod_text(text, out, capacity)",
			TextMeaning + " Writes it NUL-terminated into out (cut to capacity) and returns its length; -1 when Godot installed no lookup. Any thread"),
		new ModApiHelper("uint32_t refii_mod_menu_box(player, title, text, options, count, cancelable)",
			"the game's own menu box (the Golden Cock's) for player 1 or 2 (0: the menu owner here), on the front end and in game: up to 16 options, " +
			"paged past six; B closes a cancelable box; returns the id mod_menu_box carries with the option's index (-1 none). Any thread"),
	};

	/// <summary>What a text lookup means on every surface (Engine_ModText).</summary>
	public const string TextMeaning =
		"a mod's text in the culture the game runs in: a bare key (QUICKSAVE_SAVED) or each {KEY} inside a text, looked up in the running book, " +
		"the enabled mods' language/<culture>/text/book.babel.csv, their en-uk rows, then the engine's own words; a key nothing knows stays as written. " +
		"A notice's, message box's and menu box's title, text, captions and options take {KEY}s as they are (docs/modding/gdb-and-text.md, Text keys).";

	/// <summary>What mark means on every surface (Engine_HudModMarkers).</summary>
	public const string MarkMeaning =
		"a marker on the HUD's world-icon channel over an entity or at a world point for player 1 or 2 (0: every player this machine plays): " +
		"icon a game texture path (art/gui/.../x.tex) or an enabled mod's PNG (art/x.png), text a line under it; it rides the drawn body, is placed " +
		"as the game's own icons are (each player's camera and half) and sized by the game's distance curve (sub_821AA758); returns the id unmark takes; " +
		"a marker on an entity goes with the entity.";

	/// <summary>The Lua runtime's own helpers (ModLua.InfrastructureRuntime), one meaning each.</summary>
	public const string TimerMeaning =
		"THE game clock and timers on the guest tick: now() is the sum of the tick's seconds while no paused mode is in force (it stands still while the world does: the pause, every menu, a level change; a hitch counts at most 0.25 s; " +
		"a 32-bit float, exact to a millisecond for four hours); real_now() counts every tick, menus included; after(seconds, fn [, key]) runs fn once, protected, on the first tick the game clock passes now + seconds, " +
		"in due order, and answers key or a number; a key replaces the timer standing under it; cancel(key or id) drops it; a Lua reload drops every timer";

	public const string EntityStoreMeaning =
		"a value per entity HANDLE (entity_set(e, key, v), nil clears; entity_value(e, key [, default])), kept across a Lua reload and forgotten when the bus raises that entity's entity_removed — no handler needed";

	public const string PoolMeaning =
		"a REGENERATING POOL in player p's store under key, so player:key HUD sources and Refii.player_value read it on both co-op machines: pool declares or restates it " +
		"(max 100, floor 0, regen 0 per second, delay 0 seconds after a spend, each a number or function(p) read every tick; max_key key .. \".max\"; the seconds since the last spend under key .. \".rest\") " +
		"and answers value, max; ticked where p's hero is played: regen once delay has passed since a spend, clamped to floor..max; pool_spend(p, key, n) takes n and answers true, " +
		"or refuses (false, nothing taken) when the pool holds less than n — unless floor is below 0 and the pool holds more than 0, which goes into debt down to floor; a pool not stated yet is full (true, nothing taken); " +
		"pool_value(p, key) answers value, max (nil for no such pool)";

	public const string InPlayMeaning =
		"whether player 1 or 2 is IN PLAY: no pause or GUI mode and no level change in force (the paused event's modes), no level loading (level_unloading .. level_loaded), " +
		"that player's hero standing, and no FULL cutscene for them — what a HUD or a timer that must not run behind a menu asks";

	public const string SpellInfoMeaning =
		"a spell type's (0..7) name tag (the wheel's GUI_SCREEN_ABILITIES_WILL_<X>_NAME_LEVEL_06) and wheel icon path (art/gui/wheelicons/abilities/<stem>.tex), the ones the game draws it with — " +
		"a stand-in spell (spells/<id>.json) overlays exactly these, so the answer is the stand-in's; nil / empty for another type";

	public const string ConstantsMeaning =
		"the named numbers, the same on Lua (Refii.<NAME>) and Godot (constants()): SPELL_LIGHTNING 0 .. SPELL_RAISE_DEAD 7 (the spell types); ABILITY_HEAVY_WEAPONS 1, PHYSIQUE 2, TOUGHNESS 3, " +
		"RANGED_WEAPONS 4, ACCURACY 5, SPEED 6, LIGHTNING 7 .. RAISE_DEAD 14 (ability_level's types); REQUEST_MELEE_PRESSED 16, REQUEST_MELEE_RELEASED 17, REQUEST_MAGIC_CHARGE 18 (ability_request's types); " +
		"ACTION_INTERACT 11, ACTION_LOCK_SET 49, ACTION_LOCK_RELEASE 50, ACTION_SPRINT_SET 66, ACTION_SPRINT_RELEASE 67 (game_action ids); RELATION_NEUTRAL 1, RELATION_ALLY 2, RELATION_ENEMY 4; ATTACK_MODE_MELEE 1, ATTACK_MODE_RANGED 2, ATTACK_MODE_MAGIC 3 (attack_mode); CREATURE_ELITE 1, CREATURE_LIEUTENANT 2 (creature_tier's bits); " +
		"CAMERA_DEFAULT 0, SIMPLE 1, GAME 2, RANGED_AIM 3, MAGIC_AIM 4, ICS 7, FIRST_PERSON 8, COMBAT_CANNED_ACTION 9, CANNED_ACTION 10, CUTSCENE 11, TRACKING 12, FREE 13, FREEZE 14 (the game's ECameraMode: camera_mode, camera_override)";

	public const string SettingChangedMeaning =
		"a mod setting changed LIVE (the Options › Mods page, a script's set_setting, the debug channel's mod-setting): the key, its new value and the previous one, " +
		"after Refii.setting answers the new value; only on a real change, never at session start (read settings at the call)";

	public const string RecordNumberMeaning =
		"a GDB record's NUMBER, BOOL or EXISTENCE through the game's own lookup and typed getter (FindEntryByHash, GDBEntry_GetPropTyped — record_int's path), INHERITED: " +
		"record_number a float property (the database's type 3) as stored, else an int or enum one as a number; record_bool a bool property (type 0); record_exists whether the record is there. " +
		"nil / false for a record the database lacks or a property it has not got as that type. Guest tick";

	public const string RecordRefMeaning =
		"a GDB record's REFERENCE property (the database's type 6), INHERITED, through the game's own lookup and typed getter: the hash of the record it names — " +
		"the value word the game itself looks up (GDBManager_FindEntryByHash after the type-6 read). Absent for a record the database lacks or a property it has not got as a reference. Guest tick";

	public const string CreatureTierMeaning =
		"a creature's rank as its definition states it, bits CREATURE_ELITE 1 (the GDB bool IsElite) and CREATURE_LIEUTENANT 2 (IsLieutenant): " +
		"the bytes the creature component's init reads from the definition and the game's own Creature.IsElite / Creature.IsLieutenant answer";

	public const string AddItemsMeaning =
		"n of a definition (1..100) through the game's own add, once per item — each add's notifications and item_added event as one add's — answering how many were added and the last item";

	public const string WheelStickyMeaning =
		"the wheel-switched lock made STICKY: on true states it and the guest tick restates it every tick until on false or that hero's player no longer holds a lock " +
		"(the game's own release, or release_lock) — the one store a Godot script's wheel_switches_target lives in too; at most four heroes at once";

	public const string InterruptAtMeaning =
		"the interrupt grant deferred to the action's own clock: granted at once when the clock already reads seconds, else on the first guest tick it does — no polling; " +
		"dropped, ungranted, when the action stops, its clock runs backwards (the address is another action's now) or a minute of ticks passes; a second call restates the time; " +
		"at most 32 wait (false / 0 past that, or for a stopped action or one +0x4C holds back)";

	/// <summary>What a world meter means on every surface (Engine_HudModMarkers' fill).</summary>
	public const string WorldMeterMeaning =
		"a WORLD METER: a bar filled value of max on the marker channel, by the marker's own rules (each player's camera and half, lifted by the drawn height, " +
		"sized by the game's distance curve, gone with its entity) — world_meter makes a marker that is only the bar, mark_meter puts a fill on any marker " +
		"(or restates it IN PLACE by id: the value changes without re-marking; max 0 takes it off; an empty style keeps the standing one), unmark removes it; " +
		"style \"bar\" (\"\": the HUD's gold), \"health\" (red) or an HTML colour (\"#5fa8ff\"), and \"numbers\" after it (\"health numbers\") draws \"value / max\" " +
		"under a marker with no text, \"x<n>\" (\"health x1.5\") draws the marker n times the curve's size (0.25 .. 4); a drop leaves a pale trail that holds 0.45 s, then drains at 0.9 of a bar a second; returns the id (0 / nil for no max).";

	/// <summary>What fade means on every surface (Engine_UiModFade).</summary>
	public const string FadeMeaning =
		"fades player 1 or 2's screen (0: every player this machine plays) to black or back over seconds (0: at once): a host-drawn layer over that " +
		"player's half (their whole window alone or online), independent of the game's own fade, which level changes own.";

	/// <summary>What meter means on every surface (Engine_HudModMeters).</summary>
	public const string MeterMeaning =
		"shows or restates the HUD meter id for player 1 or 2 (0: every player this machine plays): label above a bar at value of max (a timer, a boss bar) " +
		"in style \"bar\" (the default), \"boss\" (wide, red) or \"timer\" (narrow) — the HUD's own primitives — or in the game's own meter art, " +
		"\"generic\" (hud.bgf's GenericMeter: the ornate bar), \"crucible\" (CrucibleMeter: the plain red bar) or \"price\" (PriceWidget: the bowl, the value as a number), " +
		"a copy lit on its own fader slide while the shipped original stays as authored; stacked under the top centre of that player's screen in the order first " +
		"shown; the same id restates it in place; meter_remove takes it down.";

	/// <summary>What input_claim means on every surface (ModInput.Claim).</summary>
	public const string InputClaimMeaning =
		"claims (or releases) a mod's input action — its scoped input.json name <mod>.<action> — for player 1 or 2 (0: both): while claimed, the " +
		"action's pad buttons, triggers and keys are withheld from the pad the guest sees for that player, and a chord's modifier with them while " +
		"the chord is held (the keyboard's F1-F4 d-pad keys included); the action's own input_action events still fire. Cleared when the mods' Lua " +
		"reloads and when the game goes down.";

	/// <summary>What hud_icon / hud_slide / hud_hold mean on every surface (Engine_UiHudOverrides).</summary>
	public const string HudOverrideMeaning =
		"a HUD override by element name for player 1 or 2 (0: both; the shared HUD is the local player's, the second half local player two's): " +
		"hud_icon states the texture an element shows — a game texture path (Art\\GUI\\WheelIcons\\expressions.tex) or an enabled mod's PNG " +
		"under art/, fitted to the 128 x 128 slot when it is not that size, \"\" clears — and on a MOD'S OWN picture piece of ui/hud.json, named " +
		"by its id, the texture that player's copy of the piece shows, any path (game:// or not, a mod PNG, res://), drawn at the piece's size and " +
		"region, over its own texture; hud_slide the slide it sits on as the game counts them " +
		"(0 clears); hud_hold keeps a HUD piece visible, its slide owners held on their shown (\"in\") slides while the guest would hide it " +
		"and an element of its trunk (its root and each lone child below it) the guest fades by stated opacity alone drawn at its authored opacity — the d-pad, and the health readout " +
		"(PlayerHealth, player two's CoopHealth) with its copies. An element is a slide-toggle binding name (DPadUp) or an element path by its trailing segments " +
		"(dpadmenu.group.up.up). Everything goes when the mods' Lua reloads or the game goes down.";

	/// <summary>What light means on every surface (Engine_ModLights).</summary>
	public const string LightMeaning =
		"an omni light of a colour at an energy (1: a placed lamp's) over a range in metres (0: 6), on an entity in its own frame — riding the body, " +
		"at a model helper when one is named, gone with the entity — or at a world point (guest axes); placed as play_fx_on places an effect " +
		"(Engine_FxPlayer.Mount). A SPOT when a cone is stated (degrees from the axis to the edge, at most 89): it shines along the stated direction " +
		"in the offset's axes (the entity's or the helper's — it turns with them — the world's for a point), the attenuation its edge falloff " +
		"(0 or 1 linear, the title's own). Modern: a Godot light like the FX feed's own (zero decay, no shadow map); classic: a point light — or a " +
		"spot light with that direction and cone — appended to the guest's light list for every draw its sphere meets; returns the id light_remove " +
		"takes; everything goes when the game goes down.";

	/// <summary>What send_to_partner means on every surface (the route's mod lane).</summary>
	public const string PartnerMeaning =
		"sends a mod's own message — name (a word of letters, digits, '_', '.', '-'; at most 32) and payload (any text, at most 256 bytes) from mod — " +
		"to THE OTHER MACHINE of an online session, where it is raised as partner_message (mod, name, payload, from: the player the sender plays) " +
		"once and in the order sent, on the route the game's own cross-machine calls take (core/event_relay.h pm: lines); either machine may send; " +
		"nothing is sent alone or in local co-op (false), for a bad name, or a payload over the limit.";

	/// <summary>What press / tap mean on every surface (ModInputPresses).</summary>
	/// <summary>The keyboard's attack mode, the same on every surface (GameInput's 1 / 2 / 3).</summary>
	public const string AttackModeMeaning =
		"the keyboard's ATTACK MODE for player p: which pad button the attack key presses — 1 melee (X), 2 ranged (Y), 3 magic (B), " +
		"ATTACK_MODE_*, set by the mode keys (Fable III's 1, 2, 3); 0 for a player no keyboard plays on this machine. Host state: the " +
		"attack_mode_changed event follows each change, on the machine whose keyboard plays the player.";

	/// <summary>The camera mode, the same on every surface.</summary>
	public const string CameraModeMeaning =
		"the camera the game has ACTIVE for player p, as the game's ECameraMode (CAMERA_*): the camera its own manager picked (CameraManager:FrameUpdate walks the debug, override, " +
		"multiplayer and normal sets and activates the first valid set's best camera) — 2 GAME the third-person camera, 3 RANGED_AIM, 4 MAGIC_AIM, 7 ICS an interactive scene's, " +
		"8 FIRST_PERSON (the game's own first-person look and a camera_override hold alike), 9 COMBAT_CANNED_ACTION, 10 CANNED_ACTION; -1 before any is stated and for a player " +
		"whose camera runs on the other machine. The camera_changed event follows each change.";

	/// <summary>The camera hold, the same on every surface.</summary>
	public const string CameraOverrideMeaning =
		"HOLDS a camera for player p through the game's own per-player hold, CameraManager.SetCameraOverride (the call the quests make for their first-person shots): mode " +
		"CAMERA_FIRST_PERSON (8) or CAMERA_GAME (2); 0 / nil lets it go. Options: fov (degrees; the camera's own when absent, first person 75), pitch_limit (degrees above and below " +
		"level; first person's own 55), blend (seconds in and out; the camera's own when absent), aim (true: while the player aims a gun the game's aim camera takes over). " +
		"The hold holds in combat. It YIELDS to the game: a scene's or quest's own camera override replaces it, and it comes back once theirs is released (also after a level or save " +
		"load); a full cutscene and the canned-action cameras (opening a chest, digging, a ladder, a door, a finishing move) suspend it. It is NEVER SAVED (the save's camera record " +
		"is written without it) and a held look-at half is not a cutscene (in_cutscene). The eye is the game's (the head's collision point): no option moves it. Cleared when the " +
		"mods' Lua loads (each session): state it from init.lua or on lua_ready. Split screen: each player's own.";

	/// <summary>The live field of view, the same on every surface.</summary>
	public const string CameraFovMeaning =
		"the field of view of player p's live camera in degrees: the horizontal 4:3 angle the game's cameras state (70 the default, first person 75), from the camera manager's " +
		"live block the player's view is drawn from (manager+0x90, +0x30 copied from the active camera each frame), 0 with no camera. Every camera writes its own FOV each update, " +
		"so there is no per-player FOV to set: camera_override's fov is the stated way.";

	/// <summary>The camera's shape, the rule every shape call keeps, the same on every surface.</summary>
	public const string CameraShapeMeaning =
		"THE CAMERA'S SHAPE IS STATED, NOT WRAPPED. A mod states VALUES for player p's camera (1 or 2) and the engine applies them where the camera reads its own, on both titles, " +
		"so the same mod code keeps working when the camera's implementation changes. Each statement belongs to its owner (the mod; a Godot script's is the host's, or the owner it names); " +
		"the value IN FORCE is the one stated last among the owners still stating it, and letting a statement go (nil / null) gives the slot back to the mod under it. " +
		"camera_frame, camera_focus, camera_combat_blend, camera_interest, camera_lock_frame and camera_mode_allow apply to a camera that frames ONE hero (single player, online co-op, split screen): " +
		"a shared couch screen keeps the game's own camera, which frames two heroes; camera_pitch_hold too unless stated shared; camera_tune applies to every camera of that player. " +
		"NEVER SAVED: the values live in the game VM beside Refii, outside every table the save writes. Made new when the mods' Lua loads (each session, each reload): state them from init.lua, camera.lua or on lua_ready. " +
		"Fable 3 answers every call and key as Fable 2 does (fp_* over its own FirstPersonCamera: only GAME_ACTION_CAMERA_ROTATION turns the held view).";

	/// <summary>The camera's tunables, the same on every surface.</summary>
	public const string CameraTuneMeaning =
		"TUNES player p's cameras by name, each key a number (false lets that key go; nil instead of the table lets go every key this owner stated): " +
		"pitch_speed (how fast the stick's Y moves the manual height; the game's 0.05), pitch_max / pitch_min (how far up / down the manual height goes; 0.7 each), " +
		"move_damping (the rolling average of the camera's height and focus; 1 follows at once), lock_strength (the cosine of the cone a lock holds its target inside; 0.9), " +
		"lock_turn_speed (degrees a second the camera turns to a lock; 250); the gun aim: ranged_tightness (the share of the field of view the aim point may wander in before the camera follows), " +
		"ranged_offset (degrees the aim sits off to the side), ranged_pitch_limit (degrees of elevation; 30); the held first-person view (a camera_override hold of CAMERA_FIRST_PERSON): " +
		"fp_view 1 makes it the free-look view — only the look input turns it, the hero is turned to it by fp_face (0 never, 1 while standing, 2 always), the body is not faded, " +
		"a scene's framed point is faced until the player looks away — with fp_fov, fp_pitch_limit (degrees), fp_eye_forward and fp_eye_height (metres from the head) and " +
		"fp_scenes 1 (a scene's own SIMPLE or GAME camera is shown through the held view, never in a full cutscene). The orbit keys are the camera class's own fields, written for the player whose camera updates next.";

	/// <summary>The explore framing, the same on every surface.</summary>
	public const string CameraFrameMeaning =
		"FRAMES player p's exploring camera: distance (metres: the string, the cage and its maximum, so the camera orbits at one distance), interior_scale (the distance indoors is distance times it), " +
		"height (the camera's height over the focus), focus_height (the focus's height over the hero's feet), slot (how far the focus may wander from the hero; 0 pins it), " +
		"track (how much the camera is carried by the hero's movement), levels \"first\" (the game's first exterior and first interior zoom level; the default) or \"all\". " +
		"A key left out stays the game's. The game's own values are kept and put back when the frame is let go (nil).";

	/// <summary>The focus slide, the same on every surface.</summary>
	public const string CameraFocusMeaning =
		"SLIDES the focus of player p's exploring camera right metres to the camera's right and up metres up — an over-the-shoulder frame — moving ease of the way there each camera update " +
		"(1 at once) and back the same way when let go (nil).";

	/// <summary>The combat blend, the same on every surface.</summary>
	public const string CameraCombatBlendMeaning =
		"the weight (0..1) player p's exploring camera blends toward the game's high combat camera with, instead of the game's own (which rises as a foe nears); 0 is no combat camera; nil the game's own.";

	/// <summary>What pulls the frame, the same on every surface.</summary>
	public const string CameraInterestMeaning =
		"WHAT PULLS player p's exploring camera's frame: foes false hides nearby foes from it, passers false nearby villagers and creatures; a quest's own interesting entities always count. nil the game's own.";

	/// <summary>The held pitch, the same on every surface.</summary>
	public const string CameraPitchHoldMeaning =
		"HOLDS the manual pitch of player p's exploring camera where the stick or mouse put it (the game springs it back to level every update). Options: min / max (the pitch range while held, " +
		"down and up; the camera's own bounds when absent), shared true (also on a shared couch screen).";

	/// <summary>The framed lock, the same on every surface.</summary>
	public const string CameraLockFrameMeaning =
		"FRAMES the target player p's hero is locked on (lock_target, the game's own lock) without the look-at button held, as the game frames a held lock: the camera turns to keep the target inside its cone. " +
		"Options: strength (the cone's cosine; the camera's lock_strength when absent), tilt false (do not pitch toward the target). Stated while the mod's own lock stands; false lets it go.";

	/// <summary>Whether a mode may be picked, the same on every surface.</summary>
	public const string CameraModeAllowMeaning =
		"whether the game may PICK camera mode (CAMERA_*) for player p by itself: allow false and the mode's own validity test answers no for that player (CAMERA_MAGIC_AIM: charging and casting keep the exploring camera); " +
		"a quest's or a hold's stated camera of that mode still runs. true gives the game's own test back.";

	/// <summary>The display offset, the same on every surface.</summary>
	public const string CameraOffsetMeaning =
		"OFFSETS the picture drawn for player p: { right, up, back } metres along the drawn camera's own axes, { yaw, pitch, roll } degrees about its up, right and back, fov degrees added to the drawn angle; " +
		"nil lets it go. DISPLAY ONLY: the game's camera, every reader of it (aim, movement, the AI) and the save never see it; the camera_* reads answer the stated camera, camera_pose the picture with the offset. " +
		"Owners stand as the shape calls' do (the last stated among those still stating). Handy from camera_tick for a hit's kick or a photo-style lean.";

	/// <summary>A shake of one player's camera, through the game's own screen shake.</summary>
	public const string CameraShakeMeaning =
		"SHAKES player p's camera through the game's own screen shake (Player.AddScreenShake: a timed source on the camera manager, the largest of a kind's sources in force, added to the camera's shake as it eases): " +
		"{ kind (banshee, troll, explosion, quake, magic_charge, magic_cast, combat or the game's number; quake when left out), level (1), attack, decay (0.5) and seconds (1) in seconds, add (true adds to that kind's level instead of replacing it), open (true holds it until faded) }; " +
		"{ kind, fade = seconds } fades that kind out (Player.FadeScreenShake). The game gives every shake to player one's camera; for player two the call runs inside player two's camera bracket, so on a split screen player two's own camera shakes " +
		"and on a shared screen the one shared camera does. False when player p's picture is not drawn on this machine (an online partner) or the game has no hero. The game's camera, its readers and the save see the shake as they see the game's own.";

	/// <summary>The drawn pose, the same on every surface.</summary>
	public const string CameraPoseMeaning =
		"the pose player p's picture is DRAWN with this frame (the shell's camera, eased between the game's ticks, with any camera_offset): position, forward and up (guest axes), fov (the horizontal angle the game states, degrees), " +
		"vfov (the drawn view's vertical angle, degrees), cut (the frame's camera was cut to, not blended); on a shared couch screen both players' is the one picture; nothing for a player not drawn on this machine (an online partner, no player two). " +
		"In photo mode the last game pose.";

	/// <summary>The stated block, the same on every surface.</summary>
	public const string CameraStatedMeaning =
		"the camera the GAME'S READERS SEE for player p at its last tick — the block its own camera getter built for that player's camera manager (movement, aim, the AI and the save read this one): position, forward, up (guest axes), fov (horizontal, degrees), cut (the cut byte). " +
		"A player with no camera of their own (a shared couch screen) sees the shared camera's. Fable 2 and Fable 3.";

	/// <summary>The active zoom level, the same on every surface.</summary>
	public const string CameraZoomMeaning =
		"the ZOOM LEVEL player p's exploring camera uses now, after its blends: set (the game's name of the zoom set: \"Exterior\", \"Interior\", \"Vanity\" ...; Fable 3 also \"Sprint\", \"Vault\", \"Falling\" ...; \"\" for an unnamed one), " +
		"level (the index in it), combat (the weight toward the combat camera, 0..1), string_length, cage_radius, max_cage_radius, height, focal_offset, fov, max_coop_distance (the leash the camera states in co-op; 0 for none). " +
		"Nothing while the player's camera is not the exploring one. camera_zoom_changed follows a change of set or of combat.";

	/// <summary>The screen mode, the same on every surface.</summary>
	public const string CameraScreenModeMeaning =
		"the CO-OP SCREEN the player chose: \"shared\" (one view of both heroes), \"split_vertical\" (side by side), \"split_horizontal\" (stacked), \"hybrid\" (shared until the heroes part); " +
		"camera_split says whether two views are drawn now. Both titles: Fable 3's couch co-op takes the same three screens. camera_screen_changed follows each change.";

	/// <summary>Whether two views are drawn, the same on every surface.</summary>
	public const string CameraSplitMeaning =
		"true while TWO VIEWS are drawn (local co-op on a split screen, or hybrid while split); split_screen stays as its alias.";

	/// <summary>A player's part of the window, the same on every surface.</summary>
	public const string CameraViewMeaning =
		"the part of the window player p's picture fills: x, y, width, height, each 0..1 from the window's top left (the whole window on a shared screen); nothing for a player not drawn here.";

	/// <summary>A ray through a player's view, the same on every surface.</summary>
	public const string CameraRayMeaning =
		"the LINE through point (x, y) of player p's view — each 0..1 from the view's top left, the centre when left out — from the drawn camera: origin (its position) and direction (unit length), guest axes, " +
		"through the picture exactly as drawn (its own vertical and horizontal angles). What an aim line or a click picks along; nothing for a player not drawn here.";

	/// <summary>The camera events, the same on every surface.</summary>
	public const string CameraEventsMeaning =
		"camera_cut (the game cut player p's camera: its block's cut byte rose), camera_blend (an InterpolateFor started a blend of ticks guest ticks), camera_tick (once per guest tick per player, after the camera's update, with the block its readers see), " +
		"camera_zoom_changed (the zoom set or combat changed), camera_screen_changed (the co-op screen or the split changed): read off the game's own camera statements, at the guest tick.";

	/// <summary>A hidden part, the same on every surface.</summary>
	public const string HidePartMeaning =
		"HIDES a part of an entity in the drawing (on) or shows it again: part a bone of the entity's rig by its rig name, the same in either title (Shadow_Neck: the bone and every bone " +
		"under it — the head, the face, the hair and hat; a skeleton that names its bones by hash, Fable 3's, is matched by the name's hash), drawn at nothing after the game's pose. Drawing only: the game's pose is untouched and the shadow keeps the part. Kept " +
		"per entity handle across the entity's rebuilds until shown again or the mods' Lua loads again (lua_ready). player 1 or 2 hides it in that player's view alone, 0 (or absent) in every view: " +
		"on split screen the other half still draws it; on one view it is hidden when the view is that player's (alone player one's, a shared couch screen both players', an online host " +
		"player one's, an online joiner player two's). Each part and player is its own statement: showing it for player 1 leaves a hide for every view standing.";

	public const string ChargeSpellMeaning =
		"charges spell type 0..7 (SPELL_*) through the player's OWN magic charge: held true holds the magic button with the spell lent to every tier the hero has it (known and offered) and the tiers above emptied, " +
		"so the charge stops where the spell does; held false lets the button go and the cast goes out as the game casts it (spell_launch, spell_launched, spell_created). " +
		"The tiers are lent through the slot table's own writer with its NOTIFY clear — the game's \"not a choice\" write: no spell_pick, nothing sent to the session — and the player's own slots come back the same way once the charge is over " +
		"(the charge ended without a release, the cast's last slot out, a second after a release that charged nothing, the hero gone, the mods' Lua reloaded); a real pick made meanwhile is what comes back for its slot. " +
		"On the machine that plays the player; refused when the hero has not got the spell on its first tier.";

	public const string PressMeaning =
		"PRESSES a pad button on player 1 or 2's pad — the mirror of an input claim: OR'd into that player's pad state after the claims are " +
		"withheld (GameInput, both pad paths), so the guest reads it as the player's own pad, focused or not. Names are input.json's (A B X Y LB " +
		"RB L3 R3 Start Back DpadUp DpadDown DpadLeft DpadRight) and LT / RT; press holds until released (held false / 0), a trigger pulled by " +
		"held (true / 1 full, 2..255 that much); tap holds for at least ticks whole guest game ticks (1..600; game+0xFC, the counter the pad " +
		"publish reads; with no world up it counts frames; a tap whose guest stops ticking goes after 10 s). Cleared with the claims: the mods' " +
		"Lua reloading, the game going down.";

	/// <summary>What signal / publish_text mean on every surface (ModSignals).</summary>
	public const string SignalMeaning =
		"LUA AND NATIVE TO GODOT ON THIS MACHINE, the same-machine twin of send_to_partner: signal(name, payload) — name scoped " +
		"\"<mod>.<name>\" (letters, digits, '_', '.', '-'; at most 96), payload any text up to 1024 bytes carried exactly (longer is cut and a " +
		"mod error says so) — is raised by RefiiHost as mod_signal(mod, name, payload) and mod_event(\"mod_signal\", e) on Godot's next frame; " +
		"publish_text(key, text) keeps a text (the same bounds) that RefiiHost.get_text(key) reads, and value_changed(key, value) fires when a " +
		"published text or number changes.";

	/// <summary>THE PARITY RULE (user 2026-11-02): what Lua can do, Godot can, and the other way round — <see cref="Twins"/> holds it.</summary>
	public const string ParityRule =
		"BOTH SURFACES HAVE THE SAME ACCESS: every Lua call has a Godot form and every Godot call a Lua form, matched by meaning in ModApi.Twins " +
		"(a test fails on an entry with no twin). Where one surface reads at the call and the other cannot, the other ASKS: Godot's ask(what, args, callback) " +
		"and call_lua for what the guest answers, Lua's Refii.ask_host(what, fn, ...) for what the host holds. Inside an event both read at the call: a Lua handler's Refii.* reads, " +
		"a Godot verdict's read(what, args). An entry one surface cannot have is listed with its reason.";

	public const string OffMeaning =
		"takes back a handler Refii.on took (the bus's events and the runtime's own), as Godot's remove_verdict and disconnect; true when it stood. The kind stays subscribed, so a later on() is heard at once";

	public const string TextReadMeaning =
		"the PUBLISHED TEXTS as the host states them: every surface's publish_text (a native hook's, a Godot script's, a Lua mod's own at once) — get_text answers the text, nil for none; has_text whether one stands. " +
		"A new game VM is stated every text again";

	public const string SignalLuaMeaning =
		"GODOT TO LUA ON THIS MACHINE, the other way of Refii.signal: RefiiHost.signal_lua(name, payload) — name scoped \"<mod>.<name>\", payload up to 1024 bytes carried exactly — " +
		"raises Lua's mod_signal { mod, name, payload } in the game VM on the guest's next tick (the game VM only: mod/frontend.lua does not hear it)";

	public const string ValueChangedMeaning =
		"a published number or text CHANGED, whoever published it — the same changes Godot's value_changed(key, value) hears, a frame later: texts always, numbers once a value_changed handler is subscribed";

	public const string AskHostMeaning =
		"THE HOST'S READS AND ACTIONS a Lua mod has no synchronous form of, asked of Godot on this machine: ask_host(what, fn, a, b, c, d) and fn(value) runs on a later tick with the answer " +
		"(plain data: a number, a text, a boolean, a table of them; nil for none), false when it could not be posted; what is one of ModApi.HostCalls — fade_state(p), hud_overrides(p), input_claimed(p, action), input_claims(p), " +
		"input_label(name), inventory_items(p), lights(), markers(), meters(), minimap(p), minimap_point(x, y, z), mods(), move_stick(p), presses(p), snapshot_time(key), touch_mode(), touch_stick(), and the actions inventory_action(p, operation, definition), own_screen(p, on) (the asking mod's), set_setting(key, v), save_snapshot(key), load_snapshot(key), which Refii.set_setting / save_snapshot / load_snapshot name (fn(true) once done)";

	public const string GodotRuntimeMeaning =
		"Godot's: counted by RefiiHost from the same tick and paused statements Lua's runtime counts (heard while a script is loaded), from this host's start — durations agree with Lua's, absolute values need not; " +
		"fn is a Callable run on the main thread; a key is a String; a host made again (Reload mods) starts with no timer";

	public const string GodotPoolMeaning =
		"Godot's pool IS the Lua runtime's (declared there through run_lua and stated again to each new game VM), so a pool Lua and Godot both name is one pool; options max, floor, regen, delay, max_key are numbers (and a String); " +
		"pool_value reads the player store at the call (empty for no maximum stated there); pool_spend runs Refii.pool_spend on the guest's next tick, callback(taken) on a later frame";

	public const string AskMeaning =
		"query with its answer handed to callback(result) instead of reply — the callable form of every guest read a Lua mod makes at the call (each Lua read's Godot twin is the query verb of the same meaning); the id, 0 when not posted";

	/// <summary>Mods' own morph types (Engine_ModMorphs), as every surface states them.</summary>
	public const string ModMorphMeaning =
		"MODS' OWN MORPHS: a mod declares morphs in morphs.json (name, label, default, min, max, step); each one's type is FNV-1 of \"<mod>.<name>\" in lower case, top bit cleared, moved past 0x100 (morph_id), which its GDB records carry as MorphType (a .gdb.csv writes @morph:<name>). " +
		"The weight is the player store's morph.<id>, saved with the hero and the same on both co-op machines: read it (the stored weight clamped to the declared range, else the default) or set it (clamped; the hero is redrawn and morph_changed raised on every machine). " +
		"The morph hook answers the weight function sub_822DA028 with it for either player's hero, manual look or automatic; an unregistered type is the game's own (0).";

	public const string VerdictReadMeaning =
		"a guest read answered AT ONCE from inside an on_verdict handler — the verdict runs on the game's thread inside the seam, so the read runs there too, " +
		"through the same answer as query: the Dictionary ask's callback would get ({ ok, <fields> }), the Lua handler's synchronous Refii.* read. " +
		"Only the query verbs that change nothing (ModApi.VerdictReads: not spawn, not lua_int); outside a verdict handler, or for any other verb, " +
		"{ ok = false, error } and nothing is read — use ask there. A read runs guest code: an event it raises gets no verdict";

	public const string CallLuaMeaning =
		"runs a Lua EXPRESSION once in the game VM on the guest's next tick and calls callback({ ok, values }) on a later frame: ok false when it raised (the error is in the Mods error list), values its first five results " +
		"(a number — a whole one as an int —, a text, a boolean or null; a table crosses as null) — the answering form of run_lua, through which any Refii.* call is reachable";

	/// <summary>What the typed player values mean on every surface (core/player_state.h, the runtime's store).</summary>
	public const string PlayerTextMeaning =
		"TYPED per-player values in the same store, under the same keys, owners and save as the numbers: a text (at most 256 bytes) kept exactly, " +
		"and a hash kept as its \"0x%08x\" text — a 32-bit record hash survives where a float would not. A key holds one type: a number written " +
		"over a text replaces it and the other way round; the number readers answer nil / the fallback for a text key.";

	/// <summary>The host lists a mod's menu sources by name (Engine_UiModListSources, Engine_UiInventorySource).</summary>
	/// <summary>What a stated row's label may be, on every surface (Engine_FrontEndMenuView.Resolve).</summary>
	public const string MenuLabelMeaning =
		"A row's label is a Babel tag, a literal, or a literal with tags in braces (\"{QUICKSLOTS_SLOT_1}: {OBJECT_HEALTH_POTION_NAME}\"): " +
		"each {TAG} (letters, digits, '_') is drawn as the string book says, or as written when the book has no such tag — a mod's Lua has no text lookup of its own.";

	public const string HostListMeaning =
		"a mod's menu need not build the catalogs: an entry's \"source\" may be inventory:usable[:p[:<categories>]] (what player p's hero can use from the inventory; p 0 the menus' owner), " +
		"expressions[:p] (the expressions player p's hero knows) or spells_known[:p] (the wills p's hero can assign), beside abilities:<category>; " +
		"p absent is the player whose menus are open. The rows are typed (item:0x…, expression:0x…, will:<n>) and carry no command, so the menu's " +
		"rowCommand runs for each and mod_menu_row carries subject_kind, definition and spell.";

	/// <summary>The Lua-side calls the init chunk's preamble defines.</summary>
	public static readonly IReadOnlyList<ModApiHelper> LuaHelpers = new[]
	{
		new ModApiHelper("Refii.on(event, function(e) ... end)", "subscribes; e is a table of the event's fields; return true (or set e.cancel = true) to cancel; assigning a writable field (e.damage, e.delta, e.amount, e.level, e.type, e.relation) changes what the game gets. An event raised while a handler runs is delivered nested and live when it has writable fields (the hit a Refii.cast_spell's Start lands: your e.damage reaches the game; one level of nesting, per-depth write target and cancel), else after that chunk returns with e.deferred = true (a Refii.cast_spell's spell_created, the bolt's hit_landed): the game has acted, so a cancel there has no effect"),
		new ModApiHelper("Refii.publish(key, number) / Refii.value(key)", "a mod value Godot reads as \"mod:<key>\" (HUD pieces, FX recipes), and the last value under a key"),
		new ModApiHelper("Refii.log(text)", "a line in the guest log, prefixed [mod lua]"),
		new ModApiHelper("Refii.hash(name)", "the game's FNV-1 of a name as a \"0x…\" string: a GDB record's hash, a text tag's key"),
		new ModApiHelper("Refii.health(e) -> current, max / Refii.set_health(e, v) / Refii.modify_health(e, delta, attacker)", "health, read, written, or changed through the game's own Modify (death included)"),
		new ModApiHelper("Refii.position(e) -> x, y, z / Refii.set_position(e, x, y, z)", "a position through the entity's own placement"),
		new ModApiHelper("Refii.facing(e) -> x, y, z / Refii.velocity(e) -> x, y, z / Refii.set_facing(e, x, y [, z]) -> bool", "the game's own Physics.GetFacingVector / GetVelocity / SetFacingVector (a hero's Player.SetFacingVector); guest axes, z up — the game keeps a facing level (it drops z); nil / false for an entity not in the world"),
		new ModApiHelper("Refii.entity_tint(e, r, g, b [, a]) / Refii.entity_untint(e)", EntityTintMeaning),
		new ModApiHelper("Refii.entity_alpha(e, a) / Refii.entity_scale(e, s) / Refii.entity_saturation_mask(e, on) / Refii.entity_drawable(e, on)", AppearanceMeaning),
		new ModApiHelper("Refii.lock_target(p [, e]) / Refii.release_lock(p) / Refii.switch_target(p, left) -> target / Refii.locked_target(p) -> target", LockMeaning),
		new ModApiHelper("Refii.in_cutscene([p [, kinds]]) -> inside, kinds", "whether any of kinds (bits; nil: all three) holds, and every bit holding — " + InCutsceneMeaning + ". Refii.CUTSCENE_FULL / _INTERACTIVE / _LOOK_AT name the bits"),
		new ModApiHelper("Refii.apply_status(e, kind, seconds [, level [, source]]) / Refii.clear_status(e, kind) / Refii.has_status(e, kind)", StatusMeaning),
		new ModApiHelper("Refii.knockback(e, x, y, z [, force]) / Refii.stagger(e)", KnockbackMeaning),
		new ModApiHelper("Refii.set_speed_multiplier(p, factor) / Refii.clear_speed_multiplier(p) / Refii.speed_multiplier(p) -> overridden, factor", SpeedMeaning),
		new ModApiHelper("Refii.current_action(e) -> action / Refii.action_elapsed(a) -> seconds / Refii.action_interruptible(a) / Refii.allow_action_interrupt(a)", ActionMeaning),
		new ModApiHelper("Refii.allow_action_interrupt_at(a, seconds)", InterruptAtMeaning),
		new ModApiHelper("Refii.action_type(a) -> type / Refii.action_name(a) -> name", ActionKindMeaning),
		new ModApiHelper("Refii.living(e)", LivingMeaning),
		new ModApiHelper("Refii.spell_in_slot(p, slot) -> type", SlotMeaning),
		new ModApiHelper("Refii.spell_parts(serial) -> table", CastPartsMeaning + "; the table: spell, type, targeted, level, caster, caster_player, vortex, anchor, swords = { .. }, origin = { x, y, z }, exploded (absent parts nil)"),
		new ModApiHelper("Refii.vortex_held(serial) -> { e, .. }", VortexHeldMeaning + "; nil when no live vortex carries serial"),
		new ModApiHelper("Refii.spell_balance(caster, type, level) -> table", SpellBalanceMeaning + "; the table: status_seconds, status_spread, duration, swords (absent fields nil); nil for another type or a caster with no spell manager"),
		new ModApiHelper("Refii.host_serial(serial) -> serial · Refii.local_serial(host_serial) -> serial", CastSerialMeaning),
		new ModApiHelper("Refii.play_sound(cue, e [, tag]) / Refii.play_sound_at(cue, x, y, z [, tag]) / Refii.stop_sound(e, tag [, fade_ms]) / Refii.play_music(name) / Refii.stop_music(name)", SoundMeaning),
		new ModApiHelper("Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]]) / Refii.stop_animation(e)", AnimationMeaning),
		new ModApiHelper("Refii.modify_opinion(villager, hero, axis, delta) / Refii.add_deed(villager, hero, deed)", OpinionSetMeaning + "; deed is a \"0x…\" string (Refii.hash of the Deed record's name)"),
		new ModApiHelper("Refii.npc_follow(e, target [, radius]) / Refii.npc_go_to(e, x, y, z) / Refii.npc_flee(e, from) / Refii.npc_attack(e, target) / Refii.npc_set_hostile(e, other, on) / Refii.npc_stop(e)", NpcMeaning),
		new ModApiHelper("Refii.set_time_of_day(hours) / Refii.time_of_day() -> hours / Refii.set_weather(theme [, weight [, seconds]])", TimeWeatherMeaning + "; weight defaults to 1, seconds to 1"),
		new ModApiHelper("Refii.gold(e) / Refii.give_gold(e, delta)", "a purse, read and changed through the game's own Modify"),
		new ModApiHelper("Refii.item_count(e, definition) / Refii.add_item(e, definition)", "an inventory: definitions are \"0x…\" strings (Refii.hash of the record's name)"),
		new ModApiHelper("Refii.add_item(e, definition, n) -> item, added", AddItemsMeaning + "; item a handle, nil for none"),
		new ModApiHelper("Refii.experience(e, pool) / Refii.give_experience(e, pool, amount) / Refii.spend_experience(e, pool, amount)", "experience: pools 0 General 1 Strength 2 Skill 3 Will"),
		new ModApiHelper("Refii.ability_level(e, type)", "the level of ability 1..14, or nil"),
		new ModApiHelper("Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir [, options]]]) -> spell, serial / Refii.spell_serial(spell) -> serial", "a real spell through the game's spell factory, no will cost, started at pos = {x, y, z} facing dir (the caster's own when absent: Fable 2's targeted classes place from the position along the facing — Lightning draws its bolt from there, Fireball spawns its shot there, Force Push shoves away from there); options = { tag = n, damage_scale = f, at = e or {x, y, z} }: the cast's tag, carried as `tag` by its spell_created, vortex_started, hit, hit_landed, fireball_impact and status_applying (as a launch's spell_launch tag is), and every hit carrying its serial deals damage × damage_scale where the hit is decided (refii_mod_cast_options); at casts the Targeted class by the class's own rule for that place, from pos as the origin (the caster's when absent) and dir as the facing when the origin stands on the place: Lightning at an entity starts on the point its bolt strikes (sub_825FEFC8 with the power's CreateImpactFX and 0.25: a bolt of zero length), at a point one 15.0 reach above it facing down (outside the re-acquire's 1.35 height); Fireball at an entity within the 1.0 its Start impacts at once, set back toward the origin; Blades at the entity's position or the point (its swords are made there); Vortex set back 1.25 along the facing (its twister spawns on the place); Chaos at an entity on the point its stream strikes; Force Push from the origin toward the place; Fireball and Chaos at a point, and Slow Time and Raise Dead, are refused with a mod error; the spell object's handle and its cast serial (the number its hits and spell_created carry), nil for no cast; spell_serial reads the serial of a spell handle — the serial, not the handle, is a cast's identity (a spell handle is never forgotten)"),
		new ModApiHelper("Refii.relation(a, b) / Refii.alive(e) / Refii.definition(e) / Refii.name(e) / Refii.player_of(e)", "the faction rule (1 neutral 2 ally 4 enemy), liveness, the definition hash, the instance name, 1/2/0"),
		new ModApiHelper("Refii.level_name() / Refii.player_hero(p) / Refii.local_hero() / Refii.menu_player()", "the loaded level, a player's hero, the local hero (in local co-op: the hero of whoever owns the open menus), whose menus are open (1/2)"),
		new ModApiHelper("Refii.player_value(p, key [, fallback]) / Refii.player_set(p, key, v) / Refii.player_add(p, key, d [, saved]) / Refii.player_save(p, key, v) / Refii.player_here(p) / Refii.event_replica()", "per-player values, the same on both co-op machines; saved ones go home with the hero"),
		new ModApiHelper("Refii.achievement_unlocked(p, id) / Refii.achievements(p) -> \"id,id,…\" / Refii.character_id(p) -> \"…\"", "whether the hero player p plays has unlocked achievement id, every id it has (ascending, \"\" for none), and its character id (the one its saves carry in refii_save.json, \"\" before the game bound one): achievements are per character; the answer is what the machine playing that hero states on the co-op channel, so both machines answer alike, the partner's hero included"),
		new ModApiHelper("Refii.player_value_text(p, key) / Refii.player_set_text(p, key, text) / Refii.player_save_text(p, key, text) / Refii.player_value_hash(p, key) -> \"0x…\" / Refii.player_save_hash(p, key, \"0x…\")", PlayerTextMeaning + " The readers answer nil for none"),
		new ModApiHelper("Refii.player_for(e)", PlayerForMeaning + " — the core co-op layer's own rule for who an NPC acts toward"),
		new ModApiHelper("Refii.acting_player()", "1 or 2: who \"the hero\" (GetPlayerHero) means right now — 2 inside Refii.run_as_player(2, …) and in AI acting for player two, and outside any scope on an online joiner (its own player is player two there), else 1"),
		new ModApiHelper("Refii.run_as_player(p, fn, ...)", "runs fn with GetPlayerHero(), GetPlayerHenchman() and QuestManager.HeroEntity as player p's (the partner as the henchman): a shipped script acts for either hero"),
		new ModApiHelper("Refii.combat_state(p) -> midst, close_to_combat, in_any_combat", "player p's combat state, kept per hero (the shipped global is one state)"),
		new ModApiHelper("Refii.show_to_player(p, name, ...)", "runs the function a global path names (\"GUI.DisplayMessageBox\", \"GUI.AskYesNoQuestion\", \"TutorialManager.DisplayTutorial\", any) for player p on player p's screen: on the other machine when p is played there (a question waits and returns p's answer), else here as p (split screen: p's half). The game's own box, camera, HUD and Player.* calls made while acting as player p (Refii.run_as_player) follow p by themselves"),
		new ModApiHelper("Refii.forgive_crimes(villager, p) / Refii.discovered_crime(villager, p) / Refii.perceived_renown(villager, p)", "a villager's crimes and renown for player p's hero: forgives only p's crimes it knows of (how many), whether it discovered one of p's, the renown it credits p with (nil for no villager). Villager.ForgiveHerosCrimes / HasHeroCommittedCrimeIHaveDiscovered / GetPerceivedRenownOfHero answer for the acting player through these"),
		new ModApiHelper("Refii.world_here()", "true where the shared world runs (alone, local co-op, the online host): make world effects only there"),
		new ModApiHelper("Refii.split_screen()", "true when local co-op runs on split screen (each local player their own camera)"),
		new ModApiHelper("Refii.player_dog(p)", "a player's dog: player one's, or player two's co-op dog bound to them; nil when none. Use this, never the game's GetDog(): inside player two's dog's own AI and item use, GetDog() answers that dog"),
		new ModApiHelper("Refii.opinion(villager, p) -> love, fear, attractiveness, morality, respect", "how a villager feels about player p's hero (-1..1); a set per hero; nil when it holds no opinions"),
		new ModApiHelper("Refii.uid(e)", "the entity's own 64-bit UniqueID as \"0x…\": the same on both co-op machines and across saves (a handle is neither) — a key for per-thing player values"),
		new ModApiHelper("Refii.coop_mode()", "0 alone, 1 local co-op, 2 online and this machine hosts, 3 online and this machine joined"),
		new ModApiHelper("Refii.wheel_switches_target(e)", WheelSwitchesTargetMeaning),
		new ModApiHelper("Refii.wheel_switches_target(e, on)", WheelStickyMeaning),
		new ModApiHelper("Refii.focus(p) / Refii.key_taken(e, p)", "the living entity player p is focused on, or nil — meaningful only where that hero is played (Refii.player_here(p)); whether player p has taken the per-player collectable e (silver keys)"),
		new ModApiHelper("Refii.ask_partner(asker, question) -> ticket / Refii.partner_answer(ticket) -> nil, true, false", "your own yes/no question for the other player, on their screen; nil while pending, 30 s unanswered is no; no partner answers yes"),
		new ModApiHelper("Refii.renown(e) / Refii.morality(e) / Refii.purity(e) / Refii.age(e) / Refii.physique(e) -> fat, height", "the hero stats the game reports"),
		new ModApiHelper("Refii.morph_get(p, morph) -> weight or nil / Refii.morph_set(p, morph, w) -> ok / Refii.morph_id(\"<mod>.<name>\") -> \"0x…\"", ModMorphMeaning + " A morph is \"<mod>.<name>\" or its id \"0x…\" (a Lua number cannot hold one)."),
		new ModApiHelper("Refii.appearance(p) -> {automatic, tan, fat, height, strength, skill, will, morality, purity, age} / Refii.set_appearance(p, {tan = .., ..}) / Refii.set_appearance_automatic(p, on)", "Murgo's Mirror for player p: the look in force (manual the stated inputs, automatic the game's own), or nil · manual with the named inputs, the rest as the hero looks (height -1..1, age 18..60, the rest 0..1) · the game's own look again (true) or the held manual one (false); written where p is played and kept in that player's Mirror settings, the same as the Mirror's Apply"),
		new ModApiHelper("Refii.can_save(p) / Refii.save_game(p) -> bool", "the game's own save for player p: can_save is Player.IsAbleToSaveGame for p's hero; save_game is AutoSaveGame(true, true) where can_save allows it (false otherwise) — the whole save on the next world update, save_written following per package"),
		new ModApiHelper("Refii.modify_renown(e, delta) / Refii.modify_morality(e, delta) / Refii.modify_purity(e, delta) -> bool", "the hero stats changed through the game's own Stats.Modify* (the renown / morality / purity events fire, the game clamps and splits renown with a co-op partner); false for an entity without stats"),
		new ModApiHelper("Refii.inventory(e)", "{ {definition = \"0x…\", count = n, usable = bool}, … } in the game's order; usable is the item's own UsableFromInventory test (sub_827649C8, what the Items page's use and A button ask) — definition is the item ENTITY record, which carries none of the item's own properties: the category, name and icon are on its InventoryItemComponent, c = Refii.record_ref(def, \"InventoryItemComponent\"), then Refii.record_int(c, \"Category\") / record_string(c, \"NameTag\" / \"IconGraphic\")"),
		new ModApiHelper("Refii.equipped(e) -> melee, ranged", "the weapon set's definition hashes, nil for an empty hand"),
		new ModApiHelper("Refii.use_item(p, def) / Refii.equip_weapon(p, def) / Refii.perform_expression(p, expression) / Refii.assign_spell(p, spell)", QuickActionMeaning),
		new ModApiHelper("Refii.use_item(p, def [, opts]) / Refii.equip_weapon(p, def [, opts]) / Refii.perform_expression(p, expression [, held [, opts]]) / Refii.assign_spell(p, spell [, opts]) / Refii.release_expression(p) -> bool", QuickOptionsMeaning + "; opts is {ungated = true}"),
		new ModApiHelper("Refii.sprinting(p) / Refii.end_sprint(p) -> bool", SprintMeaning),
		new ModApiHelper("Refii.set_locomotion(p, {key = value, ..}) or (p, key, value) -> bool / Refii.locomotion(p, key) -> number or nil", LocomotionMeaning),
		new ModApiHelper("Refii.set_parry(p, window, wedge) -> bool / Refii.parry(p) -> window, wedge", ParryMeaning),
		new ModApiHelper("Refii.expressions(p) / Refii.spells_known(p) / Refii.selected_expression(p)", ExpressionsMeaning + "; expressions is { {record = \"0x…\", id = \"0x…\", uses = n}, … } (nil without a performer), spells_known { type, … } (nil without a spell manager), selected_expression the record \"0x…\" or nil"),
		new ModApiHelper("Refii.record_string(def, field)", RecordStringMeaning),
		new ModApiHelper("Refii.record_int(def, field)", RecordIntMeaning),
		new ModApiHelper("Refii.record_number(def, field) / Refii.record_bool(def, field) / Refii.record_exists(def)", RecordNumberMeaning),
		new ModApiHelper("Refii.record_ref(def, field)", RecordRefMeaning + "; \"0x…\" or nil"),
		new ModApiHelper("Refii.creature_tier(e)", CreatureTierMeaning + "; nil without a creature component"),
		new ModApiHelper("Refii.near(x, y, z, radius [, creatures_only])", "handles of the entities within a radius, nearest first (at most 2048)"),
		new ModApiHelper("Refii.line_of_sight(x1, y1, z1, x2, y2, z2)", "true when the game's physics sees nothing between"),
		new ModApiHelper("Refii.spawn(definition, x, y, z)", "a new entity through the game's own CreateEntityFromIDAtPosition: its handle, or nil"),
		new ModApiHelper("Refii.entity(handle) / Refii.handle(entity)", "the game's own Entity userdata for a handle (for the shipped scripts and natives), and back"),
		new ModApiHelper("Refii.play_fx(name, x, y, z [, options]) / Refii.play_fx_on(e, name [, options])",
			"an effect of the particle bank or a mod's fx/effects/, drawn by Godot at a world position or riding an entity handle in its own frame (it turns with the entity; offset along the entity's axes); " +
			"options { duration = s, scale = n, tint = {r, g, b}, intensity = n, loop = false, offset = {x, y, z}, at = \"Weapon.Weapon.Top\" (a model helper: " +
			"the effect stands at that .mdl.gmd point in its own frame, on a rigged body on its bone) }; the id, or nil"),
		new ModApiHelper("Refii.stop_fx(id)", "stops a played effect by any surface's id"),
		new ModApiHelper("Refii.mark(target, icon, text [, p]) / Refii.unmark(id)", MarkMeaning + " target is an entity handle or {x, y, z}"),
		new ModApiHelper("Refii.world_meter(target, value, max [, style [, p]]) -> id / Refii.mark_meter(id, value, max [, style]) / Refii.mark(target, icon, text, p, { meter = {value, max, style} })", WorldMeterMeaning + " target is an entity handle or {x, y, z}"),
		new ModApiHelper("Refii.fade(p, dir [, seconds])", FadeMeaning + " dir is \"to_black\" or \"from_black\" (\"out\" / \"in\")"),
		new ModApiHelper("Refii.meter(id, label, value, max [, p [, style]]) / Refii.meter_remove(id)", MeterMeaning),
		new ModApiHelper("Refii.input_claim(p, action, on)", InputClaimMeaning + " True when posted"),
		new ModApiHelper("Refii.press(p, button, held) / Refii.tap(p, button [, ticks])", PressMeaning + " ticks absent is 1. True when posted"),
		new ModApiHelper("Refii.charge_spell(p, type, held)", ChargeSpellMeaning + " True when taken"),
		new ModApiHelper("Refii.attack_mode(p)", AttackModeMeaning),
		new ModApiHelper("Refii.camera_mode(p)", CameraModeMeaning),
		new ModApiHelper("Refii.camera_override(p, mode [, {fov, pitch_limit, blend, aim}])", CameraOverrideMeaning + " Returns true when stated"),
		new ModApiHelper("Refii.fov(p)", CameraFovMeaning),
		new ModApiHelper("Refii.camera_tune(p, {key = number, ..})", CameraTuneMeaning + " " + CameraShapeMeaning),
		new ModApiHelper("Refii.camera_frame(p, {distance, interior_scale, height, focus_height, slot, track, levels})", CameraFrameMeaning),
		new ModApiHelper("Refii.camera_focus(p, {right, up, ease})", CameraFocusMeaning),
		new ModApiHelper("Refii.camera_combat_blend(p, weight)", CameraCombatBlendMeaning),
		new ModApiHelper("Refii.camera_interest(p, {foes, passers})", CameraInterestMeaning),
		new ModApiHelper("Refii.camera_pitch_hold(p, on [, {min, max, shared}])", CameraPitchHoldMeaning),
		new ModApiHelper("Refii.camera_lock_frame(p, on [, {strength, tilt}])", CameraLockFrameMeaning),
		new ModApiHelper("Refii.camera_mode_allow(p, mode, allow)", CameraModeAllowMeaning),
		new ModApiHelper("Refii.camera_offset(p, {right, up, back, yaw, pitch, roll, fov})", CameraOffsetMeaning),
		new ModApiHelper("Refii.camera_shake(p, {kind, level, attack, decay, seconds, add, open} | {kind, fade})", CameraShakeMeaning + " Returns true when the game took it"),
		new ModApiHelper("Refii.camera_pose(p)", CameraPoseMeaning + " A table { x, y, z, fx, fy, fz, ux, uy, uz, fov, vfov, cut }, or nil"),
		new ModApiHelper("Refii.camera_stated(p)", CameraStatedMeaning + " A table { x, y, z, fx, fy, fz, ux, uy, uz, fov, cut }, or nil"),
		new ModApiHelper("Refii.camera_zoom(p)", CameraZoomMeaning + " A table, or nil"),
		new ModApiHelper("Refii.camera_screen_mode() / Refii.camera_split()", CameraScreenModeMeaning + " " + CameraSplitMeaning),
		new ModApiHelper("Refii.camera_view(p)", CameraViewMeaning + " x, y, w, h, or nil"),
		new ModApiHelper("Refii.camera_ray(p [, x, y])", CameraRayMeaning + " ox, oy, oz, dx, dy, dz, or nil"),
		new ModApiHelper("Refii.hide_part(e, part, on [, p])", HidePartMeaning + " Returns true when posted"),
		new ModApiHelper("Refii.hud_icon(p, element, texture) / Refii.hud_slide(p, element, slide) / Refii.hud_hold(p, piece, on)", HudOverrideMeaning + " True when posted"),
		new ModApiHelper("Refii.light(target, color, energy, range [, o]) / Refii.light_remove(id)",
			LightMeaning + " target an entity handle or {x, y, z}, color {r, g, b}, o = { offset = {x, y, z}, at = \"Weapon.Weapon.Top\", flicker = n, direction = {x, y, z}, cone = degrees, attenuation = n }; the id, or nil"),
		new ModApiHelper("Refii.send_to_partner(mod, name, payload)", PartnerMeaning),
		new ModApiHelper("Refii.signal(name, payload) / Refii.publish_text(key, text)", SignalMeaning + " True when posted"),
		new ModApiHelper("Refii.set_menu_rows(menu, rows [, p])", "states a menu list's rows for player p (nil: every player): strings or { name, label, command, ghosted } — what a menu or entry with \"source\": \"rows\" shows, at once if open; {} clears; true when posted. Up to 32768 bytes of rows; past it they are cut at the last whole row and the mod errors channel says so. " + MenuLabelMeaning + " " + HostListMeaning),
		new ModApiHelper("Refii.open_screen(id [, p])", "opens a host-owned screen (Screen:open:<id>) for player p: the game's or a screens record a mod states (id 1000+); true when posted"),
		new ModApiHelper("Refii.open_menu(name [, p])", "opens a mod's menu (ui/templates.json) for player p (nil: the menu owner here) over the list that has the input, or on its own with none up; true when asked"),
		new ModApiHelper("Refii.message_box(title, text [, accept [, cancel [, p]]])", "the game's own box for player p: a question when cancel is given (nil captions keep the box's own), else a notice; the id mod_message_box carries"),
		new ModApiHelper("Refii.notice(title, text [, seconds [, p]])", "a notice on the notice channel for player p (nil: the menu owner here): the HUD's info box in game, queued behind the others; the id mod_notice carries (0 shown, -1 never)"),
		new ModApiHelper("Refii.text(text [, a .. h])", TextMeaning + " %1 .. %8 take the arguments; in a game and on the front end"),
		new ModApiHelper("Refii.frontend", "true in the front end's own Lua state (mod/frontend.lua), absent in the game VM"),
		new ModApiHelper("Refii.signed_in() -> user / Refii.save_count() -> n / Refii.screen() -> name", "FRONT END ONLY (mod/frontend.lua): " + FrontEndMeaning + "; nil for none. With Refii.hash, setting, log, publish / value, notice, message_box, menu_box, open_menu, menu_player and Refii.on for start_game, mod_menu_row, mod_message_box, mod_notice, mod_menu_box — the whole front-end surface (docs/modding/ui-and-hud.md, The front-end surface)"),
		new ModApiHelper("Refii.menu_box(title, text, options [, p [, cancelable]])", "the game's own menu box for player p: up to 12 options from the table, paged past six; B closes it unless cancelable is false; the id mod_menu_box carries with the option's index (-1 none)"),
		new ModApiHelper("Refii.now() / Refii.real_now() / Refii.after(seconds, fn [, key]) -> id / Refii.cancel(key)", TimerMeaning),
		new ModApiHelper("Refii.entity_value(e, key [, default]) / Refii.entity_set(e, key, v)", EntityStoreMeaning),
		new ModApiHelper("Refii.pool(p, key, { max, floor, regen, delay [, max_key] }) -> value, max / Refii.pool_spend(p, key, n) -> bool / Refii.pool_value(p, key) -> value, max", PoolMeaning),
		new ModApiHelper("Refii.in_play(p)", InPlayMeaning + " (the paused, level_unloading and level_loaded events are the runtime's own: it hears them with no handler)"),
		new ModApiHelper("Refii.spell_info(type) -> name_tag, icon", SpellInfoMeaning),
		new ModApiHelper("Refii.SPELL_* / Refii.ABILITY_* / Refii.REQUEST_* / Refii.ACTION_* / Refii.RELATION_* / Refii.ATTACK_MODE_*", ConstantsMeaning),
		new ModApiHelper("Refii.off(event, fn) -> bool", OffMeaning),
		new ModApiHelper("Refii.get_text(key) / Refii.has_text(key)", TextReadMeaning),
		new ModApiHelper("mod_signal — Refii.on(\"mod_signal\", function(e) ... end)", SignalLuaMeaning + "; e = { mod, name, payload } — the runtime's own event (no bus kind, no cancel)"),
		new ModApiHelper("value_changed — Refii.on(\"value_changed\", function(e) ... end)", ValueChangedMeaning + "; e = { key, value } — the runtime's own event (no bus kind, no cancel)"),
		new ModApiHelper("Refii.ask_host(what, fn, ...) -> bool / Refii.set_setting(key, v) / Refii.save_snapshot(key [, fn]) / Refii.load_snapshot(key [, fn])", AskHostMeaning),
		new ModApiHelper("setting_changed — Refii.on(\"setting_changed\", function(e) ... end)", SettingChangedMeaning + "; e = { key, value, previous } — the runtime's own event, raised by the preamble (no bus kind, no cancel)"),
		new ModApiHelper("Refii.setting(key, default)", "a settings.json value with the player's choice over it, read at the call: RefiiSettings, composed in at session start and kept current — a live change (the Options › Mods page, a script's set_setting, the Studio's live tune) reaches the running VM at the next guest tick, no reload"),
		new ModApiHelper("require(name)", "a mod's own module: mod/<name>.lua of the same mod (the path under mod/ without .lua; . or / between folders), composed into the init chunk and run once on the first require, its return value cached (nil as true); an error names a missing file. Only the three entry points (init.lua, camera.lua, frontend.lua) run on their own"),
		new ModApiHelper("Refii.hero(player)", "the player's hero handle as the bus last stated it, or nil"),
		new ModApiHelper("Refii.api_version", "the API version the chunk was composed for"),
		new ModApiHelper("RefiiModSave.<key>", "state that persists with the save game"),
		new ModApiHelper("RefiiModErrors", "every handler and init error, appended"),
	};

	/// <summary>
	/// The Godot script surface, <c>Engine.get_singleton("RefiiHost")</c> (Engine_RefiiHost.cs): its
	/// members and the <c>query</c> verbs (mod_commands.cpp's grammar), each answering on
	/// <c>reply(id, result)</c> with <c>ok</c> and the named fields. Entities are handles, as in Lua.
	/// A signature's " / "-separated parts name methods (<see cref="GodotMethodNames"/>), except
	/// those that start "signal ", "query " or "e." and a part's "-> result". An argument written
	/// "name = value" may be left off from the right: RefiiHost has an overload for each count, as
	/// the script bridge passes no C# default (RefiiHostTests holds every count to a method).
	/// </summary>
	public static readonly IReadOnlyList<ModApiHelper> GodotHelpers = new[]
	{
		new ModApiHelper("signal <event>(e) / signal mod_event(name, e)", "every catalog event, by its Lua name, with a Dictionary of its fields"),
		new ModApiHelper("on_verdict(event, handler) / remove_verdict(event, handler)", "a handler run inside the game's seam: change a writable field of e or set e.cancel (return true) to refuse; read(what, args) reads the game there at once"),
		new ModApiHelper("hero(player)", "the player's hero handle as the bus last stated it (hero_ready / hero_gone), or 0 — player 1 is the host's hero on BOTH machines, so on a joiner hero(1) is the partner"),
		new ModApiHelper("achievement_unlocked(player, id) -> bool / achievements(player) -> int[] / character_id(player) -> String", "Refii.achievement_unlocked / Refii.achievements / Refii.character_id: whether the hero that player plays has unlocked an achievement, its unlocked ids (ascending), its character id (\"\" before the game bound one) — the same statement Lua reads, on both co-op machines alike"),
		new ModApiHelper("local_hero() / coop_mode() / menu_player() / world_here() / player_here(player)", "the guest's last answers (asked every frame while a script is loaded, one guest tick behind): the local player's hero handle (in local co-op the menu owner's), 0 alone 1 local co-op 2 hosting 3 joined, whose menus are open (1/2), whether this machine runs the shared world, whether this machine plays that player's hero (Refii.player_here: both alone and in local co-op, player one hosting, player two joined)"),
		new ModApiHelper("e.replica", "true in a signal's (or verdict's) dictionary when the event is a copy of the other co-op machine's — the game's own copy or a RELAYED one; absent otherwise"),
		new ModApiHelper("player_set(p, key, v) / player_add(p, key, d, saved) / player_save(p, key, v)", "per-player values, written to the store Refii.player_set / _add / _save write: the same on both co-op machines; saved ones go home with the hero"),
		new ModApiHelper("node_for(handle)", "the Node3D Godot draws that entity with, or null: parent to it, never move it"),
		new ModApiHelper("get_value(key) / setting(key, default) / set_setting(key, v) / run_lua(chunk) / frontend_lua(chunk) / mods()", "a mod's published value, a settings.json value, a live override (in force for native code at once and for the running Lua's Refii.setting at the next guest tick), a Lua chunk in the game VM, a Lua chunk in the mods' front-end Lua (the title and the front end, every mod's mod/frontend.lua in scope), the enabled mods"),
		new ModApiHelper("publish(key, value)", "sets a mod value (\"mod:<key>\" in the UI) on the guest's next tick, for every reader: HUD pieces, menus, Lua and native"),
		new ModApiHelper("set_menu_rows(menu, rows, player = 0)", "states a menu list's rows for player 1 or 2 (0: every player), replacing the last: what a ui/templates.json menu or entry with \"source\": \"rows\" (or rows:<list>) shows, at once if it is open. A row is a string or { name, label, command, ghosted }; label a Babel tag or literal, command any row verb; [] clears. \"source\": \"values:mod:<key>|<format>=<label>,...\" lists published values instead. " + MenuLabelMeaning + " " + HostListMeaning),
		new ModApiHelper("open_screen(screen, player = 0) -> bool", "opens a host-owned screen by id — the Screen:open:<id> verb any row can name: the game's (1013 achievements, 1014 multiplayer) or a ui/templates.json screens record a mod states (id 1000+, with a menu and a presentation; provider \"data\" fills its page from the record's fields, bound to mod:/player: values, row:label, Babel tags). It stands over the pause book, so it opens while the pause list has the input; false says why in the log"),
		new ModApiHelper("register_command(verb, handler) -> bool / register_command(verb, handler, with_row) -> bool", "a row verb every list runs (the front end, the pause book, screen lists, mod menus, a screen's own buttons): a row whose command is verb — or begins with it when it ends in ':' — calls handler(argument, menu, row, player), or with with_row true handler(argument, e) with e the row as a mod_menu_row Dictionary (menu, row, player, mod, command, button and the typed subject_kind, definition, spell: the table a Mod:lua: function is called with); false refuses the row. The game's verbs (PauseTab:, Screen:open:, Opens:, Mod:lua:, setting:, Inventory: ...) stay the game's"),
		new ModApiHelper("open_menu(name, player = 0) -> bool", "opens a menu a mod defines in ui/templates.json for player 1 or 2 (0: whoever owns the menus here) over the menu list that has the input — B returns — or, with no list up (in play, on the title), as a list of its own that takes the pad and closes on B. False for no such menu or a player played on the other machine. A row chosen there raises mod_menu_row here at once, and reaches native handlers (and Lua in a game) on the guest's next tick through the menu_row command"),
		new ModApiHelper("message_box(title, text, buttons, player = 0) -> id / signal message_box_answered(id, button)", "the game's own box for player 1 or 2 (0: the menu owner here) — in game the message box (one button) or query box (two), on the front end its own dialog; more than two buttons is a menu box answered here with the button's index; button is 0 for A, 1 for B, -1 when another box replaced it or the player is played on the other machine. The mod_message_box event carries the same, on every surface"),
		new ModApiHelper("notice(title, text, seconds = 0, player = 0) -> id / signal notice_done(id, outcome)", "a notice on the channel the game's co-op notices use — in game the HUD's info box for seconds (0: its own length), on the front end its dialog — queued behind the others; outcome 0 once shown, -1 when it never showed. The mod_notice event carries the same, on every surface"),
		new ModApiHelper("text(text, args = []) -> String", TextMeaning + " %1, %2 … take args. Any thread"),
		new ModApiHelper("menu_box(title, text, options, player = 0, cancelable = true) -> id / signal menu_box_answered(id, option)", "the game's own menu box (the Golden Cock's) on the front end and in game, for player 1 or 2 (0: the menu owner here): up to 16 options, paged past six; option is the index chosen, -1 for B, a box replacing it or a player played on the other machine. The mod_menu_box event carries the same, on every surface"),
		new ModApiHelper("give_gold / modify_health / set_health / add_item / give_experience / spend_experience / cast_spell / cast_spell_at(caster, type, targeted, target, level, pos, dir) / set_position / save_set", "helper calls, run in the guest tick with the real context; silent when there is nothing to act on; cast_spell_at starts the spell at pos (Vector3, guest axes) facing dir, as refii_mod_cast_spell's pos / dir"),
		new ModApiHelper("use_item(definition) -> bool", "uses one of an item definition (\"0x…\" or a number) from this machine's hero's inventory, as the Items page's Use does, with no menu open: a potion drinks, food is eaten, item_used fires; nothing when the hero carries none or the item states no use. False when the definition does not parse"),
		new ModApiHelper("save_snapshot(key) -> bool / snapshot_time(key) -> int / load_snapshot(key) -> bool / signal save_written(e)", "a copy of this machine's player-one save under key (\"<mod>.<name>\"), kept beside the save and never in the load list: save_snapshot copies the save as it stands on disk (take it on save_written — save, player — after the game's own save), snapshot_time answers when it was taken (unix seconds, 0 for none), load_snapshot loads it back in the running game through the game's own fail-quest reload, loading screen and all, and puts the save folder's own files back once the level stands, so the main save and the quest's fail-quest save stay as they were. False before a save is started or written, for a bad key or no snapshot; load_snapshot also in co-op and while a load is in flight"),
		new ModApiHelper("entity_tint(handle, r, g, b, a = 1) / entity_untint(handle)", EntityTintMeaning),
		new ModApiHelper("entity_alpha(handle, alpha) / entity_scale(handle, scale) / entity_saturation_mask(handle, on) / entity_drawable(handle, on)", AppearanceMeaning),
		new ModApiHelper("lock_target(player, entity = 0) / release_lock(player) / switch_target(player, left)", LockMeaning + "; query locked_target P answers the target held"),
		new ModApiHelper("wheel_switches_target(handle, on = true) -> bool", WheelSwitchesTargetMeaning + ". STICKY for a Godot script (the command wheel_switches_target E ON): a script runs off the guest tick, so the tick restates it every tick until on = false or the hero's player no longer holds a lock"),
		new ModApiHelper("apply_status(handle, kind, seconds, level = 0, source = 0) / clear_status(handle, kind)", StatusMeaning + "; query has_status E KIND asks"),
		new ModApiHelper("knockback(handle, x, y, z, force = 1) / stagger(handle)", KnockbackMeaning),
		new ModApiHelper("set_speed_multiplier(player, factor) / clear_speed_multiplier(player)", SpeedMeaning + "; query speed_multiplier P answers overridden and factor"),
		new ModApiHelper("allow_action_interrupt(action)", ActionMeaning + "; query current_action E answers action, elapsed, interruptible"),
		new ModApiHelper("allow_action_interrupt_at(action, seconds)", InterruptAtMeaning),
		new ModApiHelper("add_item(entity, definition, count)", AddItemsMeaning),
		new ModApiHelper("play_sound(cue, handle, tag = \"\") / play_sound_at(cue, x, y, z, tag = \"\") / stop_sound(handle, tag, fade_ms = 0) / play_music(name) / stop_music(name)", SoundMeaning),
		new ModApiHelper("play_animation(handle, name, loops = 1, blend_seconds = 0.1, speed = 1) / stop_animation(handle)", AnimationMeaning),
		new ModApiHelper("modify_opinion(villager, hero, axis, delta) / add_deed(villager, hero, deed)", OpinionSetMeaning + "; deed is the record's hash, a \"0x…\" string or a number"),
		new ModApiHelper("npc_follow(entity, target, radius = 2) / npc_go_to(entity, x, y, z) / npc_flee(entity, from) / npc_attack(entity, target) / npc_set_hostile(entity, other, on) / npc_stop(entity)", NpcMeaning),
		new ModApiHelper("set_time_of_day(hours) / set_weather(theme, weight = 1, seconds = 1)", TimeWeatherMeaning + "; query time_of_day answers hours"),
		new ModApiHelper("in_play(player) -> bool", InPlayMeaning + " — as this host last heard the paused and level events (always heard while a script is loaded; a script loaded mid-pause sees play until the next edge)"),
		new ModApiHelper("constants() -> Dictionary / spell_info(type) -> Dictionary", ConstantsMeaning + " / " + SpellInfoMeaning + " ({ name_tag, icon })"),
		new ModApiHelper("signal setting_changed(key, value)", SettingChangedMeaning),
		new ModApiHelper("in_cutscene(player = 0, kinds = 7) -> bool / cutscene_kinds(player = 0)", "the cutscene event as this host last heard it (always heard while a script is loaded; seeded by a query when the host is made): whether any of kinds holds for player 1 or 2 (0: either), and the bits — " + InCutsceneMeaning + "; query in_cutscene P asks the guest now"),
		new ModApiHelper("minimap(player) -> Dictionary / minimap_point(x, y, z) -> Vector2", "the current region's map as the game's own minimaps plot it, for player 1 or 2's hero (0 this machine's): known, proper, texture (Godot only), texture_path (its game:// path), north, half_size, hero, facing, quests (the other quests' objectives, from the quest log of the hero whose menus this machine shows); the extent is the level's own heightfields', the places the entity feed's; places in the image's element units around its centre, Y up"),
		new ModApiHelper("query locked_target P / has_status E KIND / in_cutscene P", "target (0 when not locked) · has · kinds (the REFII_CUTSCENE_* bits, 0 none): refii_mod_locked_target / refii_mod_has_status / refii_mod_in_cutscene"),
		new ModApiHelper("query speed_multiplier P / current_action E", "overridden, factor · action (0 for none), elapsed, interruptible: refii_mod_speed_multiplier / refii_mod_current_action, _action_elapsed, _action_interruptible"),
		new ModApiHelper("query action A / frontend", "type, name, elapsed, interruptible (ok false for an unknown action; elapsed and interruptible are Refii.action_elapsed / action_interruptible of that action) · signed_in, saves, screen (ok false with no front end up): " + ActionKindMeaning + " / " + FrontEndMeaning),
		new ModApiHelper("query living E / spell_in_slot P SLOT", "living · type (-1 none): " + LivingMeaning + " / " + SlotMeaning),
		new ModApiHelper("query spell_parts SERIAL", "spell, type, targeted, level, caster, caster_player, vortex, anchor, sword_count, s1..s8, x, y, z, has_origin, exploded: " + CastPartsMeaning),
		new ModApiHelper("query vortex_held SERIAL", "count, v1..v16 (0 past count; ok false when no live vortex carries SERIAL): " + VortexHeldMeaning),
		new ModApiHelper("query spell_balance C T L", "status_seconds, status_spread, duration, swords (-1 a field the class lacks; ok false for another type or a caster with no spell manager): " + SpellBalanceMeaning),
		new ModApiHelper("query host_serial SERIAL / local_serial SERIAL", "serial (ok false while the cast is not paired): " + CastSerialMeaning),
		new ModApiHelper("query time_of_day", "hours (the game clock, 0..24): refii_mod_time_of_day"),
		new ModApiHelper("query(what, args) -> id / signal reply(id, result)", "asks the guest; the answer comes on a later frame with ok and the fields below"),
		new ModApiHelper("query health E / position E / gold E / item_count E DEF / experience E POOL / ability_level E T", "current, max · x, y, z · gold · count · amount · level"),
		new ModApiHelper("cast_spell_with(caster, type, targeted, target, level, options)", "a cast with options { tag, damage_scale, pos, dir, at } (refii_mod_cast_spell_with; the line `cast_spell_with C T TARGETED TARGET LEVEL TAG SCALE [X Y Z [DX DY DZ]] [at E | at X Y Z]`): the tag rides the cast's spell_created, vortex_started, hit, hit_landed, fireball_impact and status_applying; its hits deal damage × damage_scale where the hit is decided; at (an entity handle or a Vector3 point): at casts the Targeted class by the class's own rule for that place, from pos as the origin (the caster's when absent) and dir as the facing when the origin stands on the place: Lightning at an entity starts on the point its bolt strikes (sub_825FEFC8 with the power's CreateImpactFX and 0.25: a bolt of zero length), at a point one 15.0 reach above it facing down (outside the re-acquire's 1.35 height); Fireball at an entity within the 1.0 its Start impacts at once, set back toward the origin; Blades at the entity's position or the point (its swords are made there); Vortex set back 1.25 along the facing (its twister spawns on the place); Chaos at an entity on the point its stream strikes; Force Push from the origin toward the place; Fireball and Chaos at a point, and Slow Time and Raise Dead, are refused with a mod error"),
		new ModApiHelper("set_facing(entity, x, y, z) · query facing E · query velocity E", "an entity turned through Physics.SetFacingVector (a hero's Player.SetFacingVector; the line `set_facing E X Y [Z]`) · x, y, z: Physics.GetFacingVector / GetVelocity, guest axes, z up"),
		new ModApiHelper("query relation A B / alive E / definition E / name E / level_name / hero P / player_of E", "relation · alive · definition · name · level · hero · player"),
		new ModApiHelper("query player_for E / world_here / combat_state P", "player (refii_mod_player_for: who \"the hero\" is for E) · here · midst, close_to_combat, in_any_combat (Refii.combat_state, asked of the game VM); acting_player and run_as_player are Lua's — reach them with run_lua"),
		new ModApiHelper("query renown E / morality E / purity E / physique E / age E / equipped E / inventory_stack E I", "renown · value · value · fat, height · age · melee, ranged · definition, count"),
		new ModApiHelper("query morph P MORPH", "weight, registered (ok false for a morph no enabled mod declares; MORPH \"0x…\" or \"<mod>.<name>\"): Refii.morph_get"),
		new ModApiHelper("morph_get(player, morph) -> float / morph_set(player, morph, weight) (the command morph_set) / morph_id(name) -> String", ModMorphMeaning + " morph_get reads the store at the call (NaN for a morph no enabled mod declares); morph_set is the line morph_set P MORPH W."),
		new ModApiHelper("query appearance P", "Murgo's Mirror for player P: automatic, tan, fat, height, strength, skill, will, morality, purity, age — the look in force (manual the stated inputs, automatic the game's own): Refii.appearance"),
		new ModApiHelper("set_appearance(player, inputs) (the command appearance_set) / set_appearance_automatic(player, on) (the command appearance_automatic)", "Murgo's Mirror: the player's look to manual with the Dictionary's named inputs (tan, fat, height, strength, skill, will, morality, purity, age), the rest as the hero looks · the game's own look again (true) or the held manual one (false); kept in that player's Mirror settings like the Mirror's Apply (the lines appearance_set P NAME=V... and appearance_automatic P ON): Refii.set_appearance / set_appearance_automatic"),
		new ModApiHelper("query inventory P [OFFSET]", "player P's hero's stacks in the game's order, up to 40 a reply from stack OFFSET: total, offset, next, count, d1..dk (definitions \"0x…\"), n1..nk (counts), c1..ck (categories), v1..vk (viewed 0/1), u1..uk (usable 0/1: the item's own UsableFromInventory test, Refii.inventory's usable); ask again from next while next < total"),
		new ModApiHelper("save_game(player) · query can_save P", "the game's own save where player P may save (AutoSaveGame(true, true); save_written follows per package) · able: Player.IsAbleToSaveGame for P's hero"),
		new ModApiHelper("modify_renown(entity, delta) / modify_morality(entity, delta) / modify_purity(entity, delta)", "the hero stats changed through the game's own Stats.Modify*, run in the guest tick (the lines `modify_renown E D` ..); the renown / morality / purity events fire"),
		new ModApiHelper("inventory_items(player) -> Array / inventory_action(player, operation, definition) -> bool", "what player 1 or 2's hero carries (0 the player whose menus are open here) as the game's own inventory pages show it, one Dictionary per stack (definition, count, category, name, description, icon, model, pieces, value, rating, usable, worn, weapon_class, new, lines), and what those pages do to an item: use, equip, wear, outfit, or remove, which empties its clothing slot; either player's, whoever's menus are open: the hero's own inventory as this machine's guest holds it, and an action for the player whose menus are not open is taken on their own hero in local co-op (false for another machine's player)"),
		new ModApiHelper("dress_model(node, mesh)", "gives a MeshInstance3D in a script's own world this mesh and the lit material the game's item previews use: the world's shaders read the level's lighting and draw nothing there"),
		new ModApiHelper("item_pose(item, yaw)", "how the game's item page holds an item of inventory_items(player) after yaw radians of its spin, as a Basis: at 0 its front faces a camera looking down -Z"),
		new ModApiHelper("touch_area(action, rect) -> bool / touch_mode() -> bool / touch_stick() -> Vector2",
			"on a touchscreen a finger on the rect (the root viewport's coordinates, where a script's CanvasLayer draws) presses the action (scoped, as input.json declares it) for player one, held while the finger stays; an empty Rect2 removes it; false for an action no enabled mod declares. " +
			"touch_mode: whether the touchscreen is the device in use (the touch controls are drawn). touch_stick: the on-screen move stick, x right and y up, -1..1; zero when no thumb is on it"),
		new ModApiHelper("move_stick(player) -> Vector2", "player 1 or 2's move stick as the game is handed it, x right and y up, -1..1: player 1's folds the keyboard's movement keys, the touch controls' stick and their joypad, player 2's is their own joypad (the split input_action and claim_input use); zero for a player with no pad. Lua reads it through ask_host as { x, y }"),
		new ModApiHelper("own_screen(player, on, script)", "a mod's screen stands where the pause book would, for player 1 or 2 (0 the player whose menus are open here): while on, the book draws nothing and the pad is held from the game; press Start for that player to enter and leave the pause, and turn it off when the paused signal says the pause ended. script is the calling script's node (self): the screen is that mod's, and only that mod turns it off (Lua's ask_host names its mod itself; own_screen(player, on) names none). One screen at a time, a later take by any mod takes it over; released when the mod set reloads, the game VM's Lua reloads (a Lua mod's), the game is left or a level unloads"),
		new ModApiHelper("input_glyph(name) -> Texture2D / input_label(name) -> String", "the prompt for a mod action (scoped <mod>.<action>, as input.json declares it) or a game pad button (A, B, Start, LT ...) as the game's prompts draw it now: the bound key's keycap on the keyboard and mouse, the bound button in the player's glyph style and pack on a pad, so a rebind shows at once; null / empty with nothing bound on the device in use. Lua reads input_label through ask_host"),
		new ModApiHelper("use_item(player, definition) / equip_weapon(player, definition) / perform_expression(player, expression) / assign_spell(player, spell)", QuickActionMeaning),
		new ModApiHelper("use_item(player, definition, options) / equip_weapon(player, definition, options) / perform_expression(player, expression, held [, options]) / perform_expression(player, expression, held, options, callback) -> bool / assign_spell(player, spell, options) / release_expression(player)", QuickOptionsMeaning + "; options is {\"ungated\": true}; use_item(player, definition, {\"ungated\": true}) is the per-player form of the Items page's use_item(definition); with callback it runs Lua's own Refii.perform_expression and callback(performed) follows on a later frame"),
		new ModApiHelper("end_sprint(player) · query sprinting P", SprintMeaning + " · sprinting=true|false"),
		new ModApiHelper("set_locomotion(player, values) · query locomotion P KEY", LocomotionMeaning + " · value"),
		new ModApiHelper("set_parry(player, window, wedge) · query parry P", ParryMeaning + " · window, wedge"),
		new ModApiHelper("query expressions P / spells_known P / selected_expression P / record_string DEF FIELD", ExpressionsMeaning + " — count, e1..eN (records), u1..uN (uses) · mask, count · expression (ok false with no wheel up) · value (exact but for a quote or a line break, read as _): " + RecordStringMeaning),
		new ModApiHelper("query record_int DEF FIELD", RecordIntMeaning + " — value"),
		new ModApiHelper("query record_number DEF FIELD / record_bool DEF FIELD / record_exists DEF", RecordNumberMeaning + " — value · value · exists"),
		new ModApiHelper("query record_ref DEF FIELD", RecordRefMeaning + " — value ('0x…')"),
		new ModApiHelper("query creature_tier E", CreatureTierMeaning + " — tier; ok false without a creature component"),
		new ModApiHelper("query near X Y Z R [CREATURES [OFFSET]] / line_of_sight X1 Y1 Z1 X2 Y2 Z2 / spawn DEF X Y Z / lua_int EXPR LO HI", "total, offset, count, e1..eN (100 a reply) · clear · entity · value"),
		new ModApiHelper("query local_hero / coop_mode / menu_player / coop_state", "hero · mode · player · local_hero, coop_mode, menu_player, split_screen: Refii.local_hero / coop_mode / menu_player / split_screen, asked now"),
		new ModApiHelper("query player_value P KEY [FALLBACK] / player_here P", "value, has (ok false when neither the value nor a fallback is there) · here: Refii.player_value / player_here"),
		new ModApiHelper("query player_dog P / focus P / opinion E P / uid E", "dog · entity · love, fear, attractiveness, morality, respect · uid (\"0x…\"): Refii.player_dog / focus / opinion / uid"),
		new ModApiHelper("query discovered_crime E P / perceived_renown E P · forgive_crimes E P", "discovered · renown: Refii.discovered_crime / perceived_renown for villager E and player P · the command forgives player P's crimes villager E knows of (Refii.forgive_crimes); show_to_player is Lua's — reach it with run_lua"),
		new ModApiHelper("play_fx(name, x, y, z, options) / play_fx_on(name, handle, options)",
			"an effect of the particle bank or a mod's fx/effects/ as a FableFx node at a world position (guest axes) or riding an entity handle in its own frame (it turns with the entity; offset along the entity's axes); " +
			"options { duration, scale, tint (Color or [r, g, b]), intensity, loop, offset (Vector3 or [x, y, z]), at (a model helper name: " +
			"the effect stands at that .mdl.gmd point in its own frame, on a rigged body on its bone) }; the id, 0 for an unknown effect"),
		new ModApiHelper("stop_fx(id)", "stops a played effect by any surface's id; false when none plays under it"),
		new ModApiHelper("mark(target, icon, text = \"\", player = 0) -> id / unmark(id) / markers()", MarkMeaning + " target is an entity handle or a Vector3; markers() lists what stands"),
		new ModApiHelper("world_meter(target, value, max, style = \"\", player = 0) -> id / mark_meter(id, value, max, style = \"\") -> bool / mark(target, icon, text, player, { \"meter\": { value, max, style } })", WorldMeterMeaning + " target is an entity handle or a Vector3; markers() lists meter_value and meter_max"),
		new ModApiHelper("fade(player, to_black, seconds = 0) -> bool / fade_state(player)", FadeMeaning + " fade_state answers alpha (0 clear .. 1 black), target and seconds"),
		new ModApiHelper("meter(id, label, value, max, player = 0, style = \"\") -> bool / meter_remove(id) / meters()", MeterMeaning + " meters() lists what stands, in stack order"),
		new ModApiHelper("claim_input(player, action, on = true) -> bool / input_claimed(player, action) -> bool / input_claims(player)", InputClaimMeaning + " input_claims lists the actions claimed for a player"),
		new ModApiHelper("press(player, button, held) -> bool / tap(player, button, ticks) -> bool / presses(player)", PressMeaning + " presses lists the buttons held for a player; false for an unknown button or player"),
		new ModApiHelper("charge_spell(player, type, held) / charge_spell(player, type, held, callback) -> bool", ChargeSpellMeaning + " Posted to the guest tick (command charge_spell P TYPE HELD); with callback it runs Lua's own Refii.charge_spell and callback(taken) follows on a later frame"),
		new ModApiHelper("attack_mode(player) -> int · attack_mode PLAYER MODE", AttackModeMeaning + " Read from GameInput at the call; the line attack_mode P MODE is Godot's own statement of it to the guest tick"),
		new ModApiHelper("camera_mode(player) -> int · query camera_mode P", CameraModeMeaning + " As the bus last stated it (camera_changed), a guest tick behind; the query asks the guest now (mode, fov)"),
		new ModApiHelper("camera_override(player, mode [, {fov, pitch_limit, blend, aim}]) · camera_override P MODE [FOV [PITCH [BLEND [FLAGS]]]]", CameraOverrideMeaning + " Posted, stated on the guest's next tick"),
		new ModApiHelper("fov(player) -> float", CameraFovMeaning + " Read at the call from the block this machine draws the player's view from"),
		new ModApiHelper("camera_tune(player, values, owner = \"\") -> bool", CameraTuneMeaning + " " + CameraShapeMeaning + " Posted, stated on the guest's next tick"),
		new ModApiHelper("camera_frame(player, values, owner = \"\") -> bool", CameraFrameMeaning + " Posted"),
		new ModApiHelper("camera_focus(player, values, owner = \"\") -> bool", CameraFocusMeaning + " Posted"),
		new ModApiHelper("camera_combat_blend(player, weight, owner = \"\") -> bool", CameraCombatBlendMeaning + " Posted"),
		new ModApiHelper("camera_interest(player, values, owner = \"\") -> bool", CameraInterestMeaning + " Posted"),
		new ModApiHelper("camera_pitch_hold(player, on, options = {}, owner = \"\") -> bool", CameraPitchHoldMeaning + " Posted"),
		new ModApiHelper("camera_lock_frame(player, on, options = {}, owner = \"\") -> bool", CameraLockFrameMeaning + " Posted"),
		new ModApiHelper("camera_mode_allow(player, mode, allow, owner = \"\") -> bool", CameraModeAllowMeaning + " Posted"),
		new ModApiHelper("camera_offset(player, values, owner = \"\") -> bool", CameraOffsetMeaning + " Posted"),
		new ModApiHelper("camera_shake(player, values) -> bool", CameraShakeMeaning + " Posted; true when the line was posted"),
		new ModApiHelper("camera_pose(player) -> Dictionary", CameraPoseMeaning + " { position, forward, up: Vector3; fov, vfov; cut }, empty where not drawn; read at the call"),
		new ModApiHelper("camera_stated(player) -> Dictionary", CameraStatedMeaning + " { position, forward, up; fov; cut }, from the block this machine rebuilt for the player's manager"),
		new ModApiHelper("camera_zoom(player) -> Dictionary", CameraZoomMeaning + " As the bus last stated it (camera_zoom_changed)"),
		new ModApiHelper("camera_screen_mode() -> String / camera_split() -> bool", CameraScreenModeMeaning + " " + CameraSplitMeaning),
		new ModApiHelper("camera_view(player) -> Rect2", CameraViewMeaning + " An empty Rect2 where not drawn"),
		new ModApiHelper("camera_ray(player) / camera_ray(player, x, y) -> Dictionary", CameraRayMeaning + " { origin, direction: Vector3 }, empty where not drawn"),
		new ModApiHelper("hide_part(handle, part, on, player = 0) -> bool", HidePartMeaning),
		new ModApiHelper("hud_icon(player, element, texture) -> bool / hud_slide(player, element, slide) -> bool / hud_hold(player, piece, on = true) -> bool / hud_overrides(player)", HudOverrideMeaning + " hud_overrides answers icons, slides, holds"),
		new ModApiHelper("light(target, color, energy, range, options = null) -> id / light_remove(id) / lights()",
			LightMeaning + " target an entity handle or a Vector3, color a Color or [r, g, b], options { offset, at, flicker, direction, cone, attenuation }; lights() lists what stands (id, entity, r, g, b, energy, range, x, y, z, cone, dx, dy, dz)"),
		new ModApiHelper("send_to_partner(mod, name, payload = \"\") -> bool (the command partner_send) / signal partner_message(e)", PartnerMeaning + " The signal carries mod, name, payload, from"),
		new ModApiHelper("signal mod_signal(mod, name, payload) / get_text(key) -> String / has_text(key) -> bool / publish_text(key, text) -> bool / signal value_changed(key, value)", SignalMeaning + " get_text answers \"\" for none; value is a float (a number) or a String (a text)"),
		new ModApiHelper("player_value(player, key, fallback = NaN) -> float", "a player's store value NOW, synchronously — the store Refii.player_value reads and a player: UI source resolves (Engine_UiValueSources); NaN (or the fallback) when unset"),
		new ModApiHelper("player_text(player, key) -> String / player_value_hash(player, key) -> String", "a player's store TEXT and HASH NOW, synchronously — what Refii.player_value_text / player_value_hash read: the text kept exactly, \"\" for none; the text as a hash (\"0x…\", a leading 0 octal, else decimal; whole, within 32 bits) as \"0x%08x\", \"\" when it reads as none. " + PlayerTextMeaning),
		new ModApiHelper("player_set_text(p, key, text) / player_save_text(p, key, text) / player_save_hash(p, key, hash) -> bool", PlayerTextMeaning + " Lines the guest tick runs (the commands player_set_text / player_save_text, the text base64); player_save_hash is false for a hash that does not parse"),
		new ModApiHelper("now() -> float / real_now() -> float / after(seconds, fn, key = \"\") -> id / cancel(key) -> bool", TimerMeaning + " — " + GodotRuntimeMeaning),
		new ModApiHelper("entity_value(handle, key, default = null) / entity_set(handle, key, value)", EntityStoreMeaning + " — each surface holds its own values (a Lua value and a Variant do not cross); the host hears entity_removed while it holds any"),
		new ModApiHelper("pool(player, key, options = null) -> bool / pool_value(player, key) -> [value, max] / pool_spend(player, key, n, callback = null) -> bool", PoolMeaning + " — " + GodotPoolMeaning),
		new ModApiHelper("ask(what, args, callback) -> id", AskMeaning),
		new ModApiHelper("read(what, args) -> Dictionary", VerdictReadMeaning),
		new ModApiHelper("call_lua(expression, callback) -> bool", CallLuaMeaning),
		new ModApiHelper("signal_lua(name, payload = \"\") -> bool", SignalLuaMeaning),
		new ModApiHelper("hash(name) -> String", "the game's FNV-1 of a name (its UTF-8 bytes) as a \"0x…\" string, as Refii.hash: a GDB record's hash, a text tag's key — computed at the call"),
		new ModApiHelper("split_screen() -> bool", "Refii.split_screen as the guest last answered (coop_state, asked every frame while a script is loaded): local co-op drawn as two halves"),
		new ModApiHelper("forgive_crimes(villager, player)", "Refii.forgive_crimes: the villager forgets player 1 or 2's crimes (the line forgive_crimes E P, on the guest tick)"),
		new ModApiHelper("ask_partner(asker, question, callback) -> bool / partner_answer(ticket, callback) -> bool / key_taken(handle, player, callback) -> bool / spell_serial(spell, callback) -> bool / save_get(key, callback) -> bool",
			"Refii.ask_partner / partner_answer / key_taken / spell_serial and a RefiiModSave value, run once in the game VM through call_lua: callback(value) on a later frame — the ticket (null when not asked), " +
			"null while pending then true or false, whether that player took the collectable, the cast serial (null for none), RefiiModSave[key] (what save_set writes; null for none)"),
		new ModApiHelper("api_version()", "REFII_MOD_API_VERSION, as Refii.api_version"),
		new ModApiHelper("register_source(name, rows) -> bool / register_condition(name, holds) -> bool", "a row SOURCE a list's template may name (\"source\": \"<name>[:<argument>]\"; rows(argument) answers an Array of row Dictionaries: name, label, icon, command, rating, ghosted, group, emphasised, counted, unviewed) and a row CONDITION its entries may name in \"when\" (holds() answers whether they show), each called by the UI while it builds a list; the game's own names stay the game's (false)"),
		new ModApiHelper("query player_text P KEY", "text (a quote, backslash, comma or line break as '_'), hash (\"0x…\" when the text reads as one), has: the typed text player_set_text / player_save_text / Refii.player_save_hash wrote (ok false for none)"),
	};

	/// <summary>
	/// What Lua's <c>Refii.ask_host(what, fn, ...)</c> may name: the host-side reads a Godot script makes
	/// at the call and the host-side actions, each a RefiiHost method of the same name (RefiiHost.HostCall
	/// answers each).
	/// </summary>
	public static readonly IReadOnlyList<string> HostCalls = new[]
	{
		"fade_state", "hud_overrides", "input_claimed", "input_claims", "input_label", "lights", "markers", "meters", "minimap", "minimap_point", "mods", "presses", "inventory_items", "inventory_action", "own_screen",
		"snapshot_time", "touch_mode", "touch_stick", "move_stick", "set_setting", "save_snapshot", "load_snapshot",
	};

	private static ModApiTwin T(string lua, string godot, string why = "") => new(lua, godot, why);

	/// <summary>
	/// THE QUERY VERBS A VERDICT MAY READ AT ONCE (<c>RefiiHost.read</c>, <see cref="VerdictReadMeaning"/>):
	/// every verb that changes nothing, with the synchronous Lua read of the same meaning — mod_commands.cpp's
	/// <c>kReadOnlyVerbs</c>, in its order. Refused: <c>spawn</c> (makes an entity) and <c>lua_int</c> (runs
	/// any game Lua expression).
	/// </summary>
	public static readonly IReadOnlyList<(string Verb, string Lua)> VerdictReads = new[]
	{
		("health", "health"), ("position", "position"), ("facing", "facing"), ("velocity", "velocity"), ("gold", "gold"),
		("item_count", "item_count"), ("experience", "experience"), ("ability_level", "ability_level"), ("relation", "relation"),
		("alive", "alive"), ("locked_target", "locked_target"), ("in_cutscene", "in_cutscene"), ("camera_mode", "camera_mode fov"),
		("has_status", "has_status"), ("speed_multiplier", "speed_multiplier"), ("current_action", "current_action"),
		("action", "action_type action_name action_elapsed action_interruptible"), ("frontend", "signed_in save_count screen"),
		("living", "living"), ("spell_in_slot", "spell_in_slot"), ("spell_parts", "spell_parts"), ("vortex_held", "vortex_held"),
		("spell_balance", "spell_balance"), ("host_serial", "host_serial"), ("local_serial", "local_serial"), ("time_of_day", "time_of_day"), ("definition", "definition"),
		("name", "name"), ("level_name", "level_name"), ("hero", "player_hero"), ("player_of", "player_of"), ("player_for", "player_for"),
		("world_here", "world_here"), ("combat_state", "combat_state"), ("can_save", "can_save"), ("renown", "renown"),
		("morality", "morality"), ("purity", "purity"), ("physique", "physique"), ("age", "age"), ("equipped", "equipped"),
		("inventory_stack", "inventory"), ("inventory", "inventory"), ("expressions", "expressions"), ("spells_known", "spells_known"),
		("sprinting", "sprinting"), ("locomotion", "locomotion"), ("parry", "parry"), ("selected_expression", "selected_expression"),
		("record_string", "record_string"), ("record_int", "record_int"), ("record_number", "record_number"), ("record_bool", "record_bool"),
		("record_exists", "record_exists"), ("record_ref", "record_ref"), ("creature_tier", "creature_tier"), ("near", "near"),
		("line_of_sight", "line_of_sight"), ("local_hero", "local_hero"), ("coop_mode", "coop_mode"), ("menu_player", "menu_player"),
		("coop_state", "local_hero coop_mode menu_player split_screen"), ("player_value", "player_value"),
		("player_text", "player_value_text player_value_hash"), ("player_here", "player_here"), ("player_dog", "player_dog"),
		("focus", "focus"), ("opinion", "opinion"), ("discovered_crime", "discovered_crime"), ("perceived_renown", "perceived_renown"),
		("uid", "uid"), ("appearance", "appearance"), ("morph", "morph_get"),
	};

	/// <summary>The Lua side of <c>RefiiHost.read</c>'s row: every synchronous read of <see cref="VerdictReads"/>, once each.</summary>
	public static string VerdictReadLua =>
		string.Join(" ", VerdictReads.SelectMany(r => r.Lua.Split(' ')).Distinct(StringComparer.Ordinal));

	/// <summary>
	/// THE PARITY TABLE (<see cref="ParityRule"/>): every Lua call, field, constant and runtime event and
	/// every Godot method, signal and query verb, matched by meaning. A side's tokens, space separated:
	/// Lua a <c>Refii.</c> name, <c>event:</c> a Lua event, <c>ask_host:</c> a host call Lua asks for;
	/// Godot a RefiiHost method, <c>query:</c> a query verb, <c>signal:</c> a signal. A token that is not
	/// a name (<c>e.replica</c>, <c>RefiiModSave.&lt;key&gt;</c>) is written as the catalog writes it. A row
	/// with an empty side says why that side has no form. ModParityTests holds the table to the code
	/// both ways and to reference-calls.md.
	/// </summary>
	public static readonly IReadOnlyList<ModApiTwin> Twins = new[]
	{
		// Events, handlers, the lane between the surfaces.
		T("on", "on_verdict signal:mod_event"),
		T("off", "remove_verdict"),
		T("event:setting_changed setting set_setting", "signal:setting_changed setting set_setting"),
		T("signal event:mod_signal", "signal_lua signal:mod_signal"),
		T("publish value", "publish get_value"),
		T("publish_text get_text has_text event:value_changed", "publish_text get_text has_text signal:value_changed"),
		T("ask_host", "ask query signal:reply"),
		T(VerdictReadLua, "read"),
		T("", "run_lua call_lua frontend_lua query:lua_int",
			"Lua IS the game VM: a Lua mod runs its code there directly, and its front-end code as mod/frontend.lua"),
		T("", "node_for", "a Godot node: Lua has no scene tree (it places with mark, light and play_fx_on on the handle)"),
		T("", "dress_model", "a Godot node: Lua has no scene tree"),
		T("", "item_pose", "a Godot Basis: Lua has no scene tree"),
		T("", "touch_area", "the touchscreen is drawn on: an area is a rectangle of a Godot script's own drawing, and Lua draws nothing"),
		T("", "input_glyph", "a Godot texture: Lua draws nothing, and reads the same binding as text through ask_host:input_label"),
		T("", "register_source register_condition",
			"called by Godot's UI while it builds a list, at once; Lua runs on the guest tick, so it states a list's rows ahead with set_menu_rows (ghosted included)"),
		T("", "register_command", "Lua's row verb is the command Mod:lua:name[:arg], which calls the Lua function of that name with the row"),
		T("log", "", "GDScript's own print and push_warning write Godot's log; Refii.log writes the guest's"),
		T("entity handle", "", "a guest address for game Lua that takes the object itself; a Godot script holds handles only and reaches game Lua through call_lua"),
		T("require", "", "GDScript's own preload and load"),
		T("RefiiModErrors", "", "the Mods error list shows every surface's errors; a GDScript error is Godot's own"),
		T("run_as_player acting_player show_to_player", "",
			"the player a game-Lua call stands for while it runs; a Godot call names its player outright (every UI call takes one), and call_lua reaches these"),
		T("frontend", "", "marks the front end's own Lua state; a Godot script runs in one process across the front end and the game"),
		T("signed_in save_count screen", "query:frontend"),
		T("hash", "hash"),
		T("api_version", "api_version"),
		T("Refii.SPELL_*", "constants"),
		T("spell_info", "spell_info"),
		// The runtime.
		T("now real_now after cancel", "now real_now after cancel"),
		T("entity_value entity_set", "entity_value entity_set"),
		T("pool pool_spend pool_value", "pool pool_spend pool_value"),
		T("in_play", "in_play"),
		// Entities and the world.
		T("health set_health modify_health", "query:health set_health modify_health"),
		T("position set_position", "query:position set_position"),
		T("facing velocity set_facing", "query:facing query:velocity set_facing"),
		T("entity_tint entity_untint entity_alpha entity_scale entity_saturation_mask entity_drawable",
			"entity_tint entity_untint entity_alpha entity_scale entity_saturation_mask entity_drawable"),
		T("lock_target release_lock switch_target locked_target", "lock_target release_lock switch_target query:locked_target"),
		T("wheel_switches_target", "wheel_switches_target"),
		T("in_cutscene", "in_cutscene cutscene_kinds query:in_cutscene"),
		T("apply_status clear_status has_status", "apply_status clear_status query:has_status"),
		T("knockback stagger", "knockback stagger"),
		T("set_speed_multiplier clear_speed_multiplier speed_multiplier", "set_speed_multiplier clear_speed_multiplier query:speed_multiplier"),
		T("current_action action_elapsed action_interruptible allow_action_interrupt allow_action_interrupt_at action_type action_name",
			"query:current_action query:action allow_action_interrupt allow_action_interrupt_at"),
		T("living alive", "query:living query:alive"),
		T("spell_in_slot spell_parts vortex_held", "query:spell_in_slot query:spell_parts query:vortex_held"),
		T("spell_balance", "query:spell_balance"),
		T("host_serial local_serial", "query:host_serial query:local_serial"),
		T("cast_spell spell_serial", "cast_spell cast_spell_at cast_spell_with spell_serial"),
		T("charge_spell", "charge_spell"),
		T("play_sound play_sound_at stop_sound play_music stop_music", "play_sound play_sound_at stop_sound play_music stop_music"),
		T("play_animation stop_animation", "play_animation stop_animation"),
		T("modify_opinion add_deed opinion", "modify_opinion add_deed query:opinion"),
		T("npc_follow npc_go_to npc_flee npc_attack npc_set_hostile npc_stop", "npc_follow npc_go_to npc_flee npc_attack npc_set_hostile npc_stop"),
		T("set_time_of_day time_of_day set_weather", "set_time_of_day query:time_of_day set_weather"),
		T("gold give_gold", "query:gold give_gold"),
		T("item_count add_item inventory equipped", "query:item_count add_item query:inventory query:inventory_stack query:equipped"),
		T("experience give_experience spend_experience ability_level", "query:experience give_experience spend_experience query:ability_level"),
		T("relation definition name level_name uid creature_tier", "query:relation query:definition query:name query:level_name query:uid query:creature_tier"),
		T("near line_of_sight spawn", "query:near query:line_of_sight query:spawn"),
		T("record_string record_int record_number record_bool record_exists record_ref",
			"query:record_string query:record_int query:record_number query:record_bool query:record_exists query:record_ref"),
		T("renown morality purity modify_renown modify_morality modify_purity physique age",
			"query:renown query:morality query:purity modify_renown modify_morality modify_purity query:physique query:age"),
		T("forgive_crimes discovered_crime perceived_renown focus", "forgive_crimes query:discovered_crime query:perceived_renown query:focus"),
		T("appearance set_appearance set_appearance_automatic", "query:appearance set_appearance set_appearance_automatic"),
		T("morph_get morph_set morph_id", "morph_get query:morph morph_set morph_id"),
		// Players and co-op.
		T("hero player_hero", "hero query:hero"),
		T("player_of player_for player_dog combat_state", "query:player_of query:player_for query:player_dog query:combat_state"),
		T("local_hero menu_player coop_mode world_here split_screen",
			"local_hero menu_player coop_mode world_here split_screen query:local_hero query:menu_player query:coop_mode query:world_here query:coop_state"),
		T("player_here event_replica", "player_here query:player_here e.replica"),
		T("achievement_unlocked achievements character_id", "achievement_unlocked achievements character_id"),
		T("player_value player_set player_add player_save", "player_value player_set player_add player_save query:player_value"),
		T("player_value_text player_set_text player_save_text player_value_hash player_save_hash",
			"player_text player_set_text player_save_text player_value_hash player_save_hash query:player_text"),
		T("ask_partner partner_answer key_taken", "ask_partner partner_answer key_taken"),
		T("send_to_partner", "send_to_partner"),
		T("can_save save_game", "query:can_save save_game"),
		T("save_snapshot load_snapshot ask_host:snapshot_time", "save_snapshot load_snapshot snapshot_time"),
		T("RefiiModSave.<key>", "save_set save_get"),
		// The hero's actions, input and camera.
		T("use_item equip_weapon perform_expression assign_spell release_expression", "use_item equip_weapon perform_expression assign_spell release_expression"),
		T("expressions spells_known selected_expression", "query:expressions query:spells_known query:selected_expression"),
		T("sprinting end_sprint set_locomotion locomotion set_parry parry", "query:sprinting end_sprint set_locomotion query:locomotion set_parry query:parry"),
		T("input_claim ask_host:input_claimed ask_host:input_claims", "claim_input input_claimed input_claims"),
		T("press tap ask_host:presses", "press tap presses"),
		T("attack_mode", "attack_mode"),
		T("camera_mode camera_override fov", "camera_mode camera_override fov query:camera_mode"),
		T("camera_tune camera_frame camera_focus camera_combat_blend camera_interest camera_pitch_hold camera_lock_frame camera_mode_allow",
			"camera_tune camera_frame camera_focus camera_combat_blend camera_interest camera_pitch_hold camera_lock_frame camera_mode_allow"),
		T("camera_offset camera_pose camera_stated camera_zoom camera_screen_mode camera_split camera_view camera_ray",
			"camera_offset camera_pose camera_stated camera_zoom camera_screen_mode camera_split camera_view camera_ray"),
		T("camera_shake", "camera_shake"),
		T("hide_part", "hide_part"),
		// What is drawn: effects, lights, the HUD, menus and boxes.
		T("play_fx play_fx_on stop_fx", "play_fx play_fx_on stop_fx"),
		T("light light_remove ask_host:lights", "light light_remove lights"),
		T("mark unmark world_meter mark_meter ask_host:markers", "mark unmark world_meter mark_meter markers"),
		T("fade ask_host:fade_state", "fade fade_state"),
		T("meter meter_remove ask_host:meters", "meter meter_remove meters"),
		T("ask_host:minimap ask_host:minimap_point", "minimap minimap_point"),
		T("ask_host:inventory_items ask_host:inventory_action ask_host:own_screen", "inventory_items inventory_action own_screen"),
		T("ask_host:touch_mode ask_host:touch_stick ask_host:move_stick ask_host:input_label", "touch_mode touch_stick move_stick input_label"),
		T("hud_icon hud_slide hud_hold ask_host:hud_overrides", "hud_icon hud_slide hud_hold hud_overrides"),
		T("set_menu_rows open_menu open_screen", "set_menu_rows open_menu open_screen"),
		T("message_box event:mod_message_box", "message_box signal:message_box_answered"),
		T("notice event:mod_notice", "notice signal:notice_done"),
		T("text", "text"),
		T("menu_box event:mod_menu_box", "menu_box signal:menu_box_answered"),
		T("ask_host:mods", "mods"),
	};

	/// <summary>Lua tokens of <see cref="Twins"/> that are globals of the chunk, not <c>Refii.</c> names.</summary>
	public static readonly IReadOnlyList<string> LuaGlobals = new[] { "require", "RefiiModErrors" };

	/// <summary>
	/// One <see cref="Twins"/> row as the parity table of reference-calls.md writes it: Lua's forms,
	/// Godot's forms, and why a side has none.
	/// </summary>
	public static string TwinMarkdown(ModApiTwin twin)
	{
		static bool IsName(string token) => token.Length != 0 && token.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
		static string Lua(string token) =>
			token.StartsWith("event:", StringComparison.Ordinal) ? $"`Refii.on(\"{token[6..]}\")`" :
			token.StartsWith("ask_host:", StringComparison.Ordinal) ? $"`Refii.ask_host(\"{token[9..]}\", fn)`" :
			IsName(token) && !LuaGlobals.Contains(token) ? $"`Refii.{token}`" : $"`{token}`";
		static string Godot(string token) =>
			token.StartsWith("query:", StringComparison.Ordinal) ? $"`query {token[6..]}`" :
			token.StartsWith("signal:", StringComparison.Ordinal) ? $"`signal {token[7..]}`" : $"`{token}`";
		string lua = twin.Lua.Length == 0 ? "—" : string.Join(" · ", twin.LuaTokens.Select(Lua));
		string godot = twin.Godot.Length == 0 ? "—" : string.Join(" · ", twin.GodotTokens.Select(Godot));
		return $"| {lua} | {godot} | {twin.Why} |";
	}

	/// <summary>The RefiiHost method names one <see cref="GodotHelpers"/> signature states.</summary>
	public static IEnumerable<string> GodotMethodNames(string signature)
	{
		if (signature.StartsWith("query ", StringComparison.Ordinal))
		{
			yield break;
		}

		foreach (string raw in signature.Split(" / "))
		{
			string part = raw.Split(" ->")[0].Trim();
			if (part.Length == 0 || part.StartsWith("signal ", StringComparison.Ordinal) ||
				part.StartsWith("query ", StringComparison.Ordinal) || part.StartsWith("e.", StringComparison.Ordinal))
			{
				continue;
			}

			yield return part.Split('(')[0].Trim();
		}
	}

	/// <summary>The writable numeric fields of each event, as the Lua preamble's write-back needs them.</summary>
	public static IEnumerable<(int Kind, string Field)> WritableFields =>
		Events.SelectMany(e => e.Fields.Where(f => f.Writable && f.Name != "cancel").Select(f => (e.Kind, f.Name)));

	public static ModApiEvent? Find(string name) =>
		Events.FirstOrDefault(e =>
			string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(e.LuaName, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>A native handler source for <paramref name="e"/>, ready to drop under native/.</summary>
	public static string NativeTemplate(ModApiEvent e, string mod)
	{
		string fields = string.Join("\n", e.Fields.Select(f => $"  //   e.{f.Name,-16} {f.Type,-18} {f.Meaning}{(f.Writable ? " (writable)" : string.Empty)}"));
		string cancel = e.Cancellable ? $"  // e.cancel = true;  // {e.Cancel}\n" : string.Empty;
		return
			$"// {mod} — a handler for the {e.Name} event.\n" +
			$"//\n// {e.Summary}\n// Seam: {e.Seam}\n" +
			"\n#include \"core/refii_mod.h\"\n\n" +
			"namespace {\n\n" +
			$"void On{e.Name}(refii::mods::{e.Name}Event& e) {{\n" +
			$"  // Fields:\n{fields}\n" +
			cancel +
			"  (void)e;\n" +
			"}\n\n" +
			$"REFII_MOD_ON({e.Name}, On{e.Name});\n\n" +
			"}  // namespace\n";
	}

	/// <summary>A Lua handler for <paramref name="e"/>, for a mod's init.lua.</summary>
	public static string LuaTemplate(ModApiEvent e)
	{
		string fields = string.Join("\n", e.Fields.Where(f => !f.Type.Contains('*') && !f.NativeOnly).Select(f => $"\t-- e.{f.Name}: {f.Meaning}"));
		string cancel = e.Cancellable ? $"\t-- return true  -- {e.Cancel}\n" : string.Empty;
		return $"Refii.on(\"{e.LuaName}\", function(e)\n{fields}\n{cancel}end)\n";
	}
}
