using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Refii.Bnk;

namespace Refii.Mods;

/// <summary>
/// A mod's own Lua, run in the GAME VM after the shipped scripts: <c>mod/init.lua</c> once the
/// startup scripts have defined their tables, <c>mod/camera.lua</c> each time the camera scripts
/// run.
/// </summary>
/// <remarks>
/// <para>A MOD'S LUA IS ITS OWN CODE, never an edited copy of a shipped script — that is derived
/// game text and cannot be redistributed. It patches what the shipped scripts defined
/// (<c>CombatWheel.ActiveAttackerTimeOut = 6</c>, a field on <c>CombatStyles.X</c>, a wrapped
/// <c>WheelRing.Create</c>).</para>
/// <para>WHEN, from the binary (TU1): every game session builds a fresh VM and
/// <c>rex_lhLuaScriptSystem_LoadStartupScripts</c> (0x82459280) runs every shipped script except
/// the cameras'; <c>rex_lhLuaScriptSystem_RunCameraScripts</c> (0x8245A610) runs those when a
/// player's CameraManager is built — the first level of a session, a save load, a co-op join.
/// The payload hook runs the composed text right after each (Fable2/src/vfs/src/hooks/mod_lua_hooks.cpp),
/// so init.lua runs once per session and camera.lua may run several times: it must be
/// idempotent.</para>
/// <para>Every enabled mod's file is composed, lowest priority first, into one chunk served at
/// <see cref="InitKey"/> / <see cref="CameraKey"/>, each mod inside its own <c>pcall</c> so one
/// mod's runtime error cannot stop another's; the error lands in the global
/// <c>RefiiModErrors</c>. Each file is compiled first with the game's own compiler, and one that
/// does not compile is left out and named — a syntax error would otherwise fail the whole
/// chunk.</para>
/// <para>A MOD'S LUA MAY BE SEVERAL FILES: every other <c>mod/*.lua</c> is a MODULE, reached from
/// the mod's own chunks with <c>require("name")</c> — the path under <c>mod/</c> without
/// <c>.lua</c>, <c>.</c> or <c>/</c> between folders. The game VM has no <c>require</c> and no
/// file system to resolve one from, so each module is composed INTO the init chunk as a function
/// registered under the mod's name (<see cref="RequireRuntime"/>), and <c>require</c> is a local
/// of the mod's block that runs the function once and caches its result, as Lua's does: a module
/// that returns nothing yields <c>true</c>, a module required while it is running (a cycle) sees
/// <c>true</c>. Modules are lazy — only what a chunk requires runs — and each mod sees only its
/// own. The front-end chunk registers them again in its own state. A reload that runs the init
/// chunk again (Live iteration) registers fresh module functions, so the next <c>require</c>
/// runs the new file. Each module is compiled with the game's compiler like the entry points.</para>
/// </remarks>
public static class ModLua
{
	public const string ModInitPath = "mod/init.lua";
	public const string ModCameraPath = "mod/camera.lua";

	/// <summary>The folder every file of a mod's Lua lives in; any other <c>.lua</c> under it is a module.</summary>
	public const string ModLuaFolder = "mod/";

	/// <summary>
	/// The module registry and <c>require</c>, in the preamble: <c>Refii._mod_scope(mod)</c> opens a
	/// fresh registry for a mod (the composed chunk fills <c>modules[name]</c> with each file as a
	/// function), <c>Refii._require(mod)</c> is that mod's <c>require</c>. A name is lower-cased with
	/// <c>.</c> read as <c>/</c>; a missing module is an error naming the file; the first result is
	/// cached, nil as <c>true</c>, and a cycle sees <c>true</c>.
	/// <c>Refii._as_mod(mod)</c> is the <c>Refii</c> a mod's block sees (a local of the block): every field
	/// the global's, read and written through, but its <c>ask_host</c> names the mod, so the host knows
	/// which mod an ask comes from (<c>own_screen</c> is that mod's). Every mod function holds its view
	/// as an upvalue, and a mod function the game keeps (a wrapper stored by value in a combat info)
	/// is written into the save with its upvalues: so <c>Refii</c> and each view are PERMANENTS
	/// (<c>Refii._permanent</c>, the game's <c>AddToPermanentsTables</c> without its throw once the load
	/// table is gone), written by name. Written by value they took the 213 C natives along, Pluto
	/// raised "Attempt to persist a C function" and the game exited part-way through the save.
	/// </summary>
	public const string RequireRuntime =
		"Refii._modules = Refii._modules or {}\n" +
		"function Refii._mod_scope(mod)\n" +
		"\tlocal scope = { modules = {}, loaded = {} }\n" +
		"\tRefii._modules[mod] = scope\n" +
		"\treturn scope\n" +
		"end\n" +
		"function Refii._permanent(value, name)\n" +
		"\tlocal save = PlutoPermanentsSaveTable\n" +
		"\tif save == nil or FNVHash == nil or save[value] ~= nil then return end\n" +
		"\tlocal hash = FNVHash(name)\n" +
		"\tsave[value] = hash\n" +
		"\tif PlutoPermanentsLoadTable ~= nil and IsLoadedFromSaveGame ~= nil and IsLoadedFromSaveGame() then\n" +
		"\t\trawset(PlutoPermanentsLoadTable, hash, value)\n" +
		"\tend\n" +
		"end\n" +
		"function Refii._as_mod(mod)\n" +
		"\tRefii._mod_views = Refii._mod_views or {}\n" +
		"\tlocal view = Refii._mod_views[mod]\n" +
		"\tif view == nil then\n" +
		"\t\tview = setmetatable({ ask_host = function(what, fn, a, b, c, d)\n" +
		"\t\t\tif Refii._ask_as ~= nil then return Refii._ask_as(mod, what, fn, a, b, c, d) end\n" +
		"\t\t\treturn Refii.ask_host(what, fn, a, b, c, d)\n" +
		"\t\tend }, { __index = Refii, __newindex = Refii })\n" +
		"\t\tRefii._mod_views[mod] = view\n" +
		"\tend\n" +
		"\tRefii._permanent(Refii, 'RefiiApi')\n" +
		"\tRefii._permanent(view, 'RefiiModView.' .. mod)\n" +
		"\treturn view\n" +
		"end\n" +
		"function Refii._require(mod)\n" +
		"\treturn function(name)\n" +
		"\t\tname = string.lower((string.gsub(tostring(name), '%.', '/')))\n" +
		"\t\tlocal scope = Refii._modules[mod]\n" +
		"\t\tlocal module = scope and scope.modules[name]\n" +
		"\t\tif module == nil then error('require: no mod/' .. name .. '.lua in ' .. tostring(mod), 2) end\n" +
		"\t\tlocal loaded = scope.loaded[name]\n" +
		"\t\tif loaded ~= nil then return loaded end\n" +
		"\t\tscope.loaded[name] = true\n" +
		"\t\tlocal result = module(name)\n" +
		"\t\tif result == nil then result = true end\n" +
		"\t\tscope.loaded[name] = result\n" +
		"\t\treturn result\n" +
		"\tend\n" +
		"end\n";

	/// <summary>
	/// THE RUNTIME EVERY MOD WOULD OTHERWISE WRITE, in the preamble (hard_mods_plan I.7), on the one
	/// guest tick the bus raises (<c>Refii._fire</c> runs <c>Refii._runtime[kind]</c> before the
	/// handlers; the kinds it needs are wanted here, so it runs with no handler subscribed):
	/// <list type="bullet">
	/// <item><c>Refii.now()</c> — THE game clock: the sum of the tick's seconds while no mode of the
	/// <c>paused</c> event is in force (mode 1 holds the world behind the pause and menus, mode 2 is a
	/// level change), so it stands still exactly while the world does; a hitch counts at most 0.25 s.
	/// <c>Refii.real_now()</c> counts every tick, menus included. A Lua number is a 32-bit float:
	/// exact to a millisecond for the first four hours.</item>
	/// <item><c>Refii.after(seconds, fn [, key]) -> id</c> / <c>Refii.cancel(key or id)</c> — fn run
	/// once, protected, on the first tick the game clock passes now + seconds, in due order; a key
	/// replaces the timer standing under it. A reload drops every timer (its functions are the old
	/// chunk's).</item>
	/// <item><c>setting_changed {key, value, previous}</c> — <c>Refii.on</c> takes it: a live change of a
	/// mod setting (<c>ModSettings.Set</c>'s assignment calls <c>Refii._setting_changed</c>), after
	/// <c>RefiiSettings</c> holds the new value; only on a real change.</item>
	/// <item><c>Refii.entity_value(e, key [, default])</c> / <c>Refii.entity_set(e, key, v)</c> — a value
	/// per entity HANDLE, forgotten at that entity's <c>entity_removed</c>; kept across a reload.</item>
	/// <item><c>Refii.pool(p, key, {max, floor, regen, delay [, max_key]})</c> / <c>Refii.pool_spend(p,
	/// key, n) -> bool</c> / <c>Refii.pool_value(p, key) -> value, max</c> — a regenerating pool in
	/// player p's store under key (so <c>player:key</c> HUD sources read it on every machine), its
	/// maximum under max_key (key .. ".max"), the seconds since the last spend under key .. ".rest";
	/// each field a number or <c>function(p)</c> read every tick. Ticked where p's hero is played:
	/// regen per second once delay seconds have passed since a spend, clamped to floor..max. A spend
	/// is refused (false, nothing taken) when the pool holds less than n — unless floor is below 0
	/// and the pool holds more than 0, which may go into debt down to floor; a pool not stated yet is
	/// full (true, nothing taken).</item>
	/// <item><c>Refii.in_play(p)</c> — player p is in play: no pause or GUI mode and no level change in
	/// force (the paused event's modes), no level loading (level_unloading .. level_loaded), p's hero
	/// standing, and no FULL cutscene for p (in_cutscene).</item>
	/// <item><c>Refii.spell_info(type) -> name_tag, icon</c> and the named numbers of
	/// <see cref="ModApi.Constants"/> (<c>Refii.SPELL_FIREBALL</c> …).</item>
	/// </list>
	/// </summary>
	public static string InfrastructureRuntime()
	{
		int tick = ModApi.Find("tick")!.Kind;
		int removed = ModApi.Find("entity_removed")!.Kind;
		int loaded = ModApi.Find("level_loaded")!.Kind;
		int unloading = ModApi.Find("level_unloading")!.Kind;
		int paused = ModApi.Find("paused")!.Kind;
		var text = new StringBuilder();
		foreach ((string name, int value) in ModApi.Constants)
		{
			text.Append("Refii.").Append(name).Append(" = ").Append(value).Append('\n');
		}

		text.Append("Refii._spell_info = {");
		for (int type = 0; ModSpells.Info(type) is { } info; ++type)
		{
			text.Append('[').Append(type).Append("]={'").Append(info.NameTag).Append("','").Append(info.Icon).Append("'},");
		}

		text.Append("}\n");
		text.Append(
			"function Refii.spell_info(t)\n" +
			"\tlocal s = Refii._spell_info[t]\n" +
			"\tif s == nil then return nil end\n" +
			"\treturn s[1], s[2]\n" +
			"end\n" +
			"Refii._local_events = { setting_changed = true, mod_signal = true, value_changed = true }\n" +
			"Refii._runtime = {}\n" +
			"Refii._clock = Refii._clock or 0\n" +
			"Refii._real_clock = Refii._real_clock or 0\n" +
			"Refii._timers = {}\n" +
			"Refii._timer_seq = Refii._timer_seq or 0\n" +
			"Refii._entity_store = Refii._entity_store or {}\n" +
			"Refii._pools = Refii._pools or {}\n" +
			"Refii._play = Refii._play or { modes = {}, loading = true }\n" +
			"function Refii._want(kind)\n" +
			"\tlocal group = math.floor(kind / " + ModApi.LuaMaskBits + ") + 1\n" +
			"\tlocal bit = 2 ^ (kind % " + ModApi.LuaMaskBits + ")\n" +
			"\tlocal mask = Refii._masks[group] or 0\n" +
			"\tif math.floor(mask / bit) % 2 == 0 then Refii._masks[group] = mask + bit Refii._dirty = true end\n" +
			"end\n" +
			"function Refii.now() return Refii._clock end\n" +
			"function Refii.real_now() return Refii._real_clock end\n" +
			"function Refii.after(seconds, fn, key)\n" +
			"\tif type(fn) ~= 'function' then error('Refii.after: a function is needed', 2) end\n" +
			"\tif key ~= nil then Refii.cancel(key) end\n" +
			"\tRefii._timer_seq = Refii._timer_seq + 1\n" +
			"\tlocal list = Refii._timers\n" +
			"\tlist[#list + 1] = { at = Refii._clock + (tonumber(seconds) or 0), fn = fn, key = key, seq = Refii._timer_seq }\n" +
			"\tif key ~= nil then return key end\n" +
			"\treturn Refii._timer_seq\n" +
			"end\n" +
			"function Refii.cancel(key)\n" +
			"\tlocal list = Refii._timers\n" +
			"\tfor i = #list, 1, -1 do\n" +
			"\t\tlocal t = list[i]\n" +
			"\t\tif t.key == key or (t.key == nil and t.seq == key) then table.remove(list, i) end\n" +
			"\tend\n" +
			"end\n" +
			"local function RunTimers()\n" +
			"\tlocal now, list = Refii._clock, Refii._timers\n" +
			"\tlocal any = false\n" +
			"\tfor i = 1, #list do if list[i].at <= now then any = true break end end\n" +
			"\tif not any then return end\n" +
			"\tlocal due, keep = {}, {}\n" +
			"\tfor i = 1, #list do\n" +
			"\t\tlocal t = list[i]\n" +
			"\t\tif t.at <= now then due[#due + 1] = t else keep[#keep + 1] = t end\n" +
			"\tend\n" +
			"\tRefii._timers = keep\n" +
			"\ttable.sort(due, function(a, b) return a.at < b.at or (a.at == b.at and a.seq < b.seq) end)\n" +
			"\tfor i = 1, #due do\n" +
			"\t\tlocal ok, err = pcall(due[i].fn)\n" +
			"\t\tif not ok then Refii._report('after ' .. tostring(due[i].key or due[i].seq), err) end\n" +
			"\tend\n" +
			"end\n" +
			"local function Field(x, p) if type(x) == 'function' then return x(p) end return x end\n" +
			"local function TickPools(dt)\n" +
			"\tfor p, list in pairs(Refii._pools) do\n" +
			"\t\tif Refii.player_here and Refii.player_here(p) and Refii.player_hero(p) ~= nil then\n" +
			"\t\t\tfor key, pool in pairs(list) do\n" +
			"\t\t\t\tlocal ok, err = pcall(function()\n" +
			"\t\t\t\t\tlocal max, floor = Field(pool.max, p) or 0, Field(pool.floor, p) or 0\n" +
			"\t\t\t\t\tlocal regen, delay = Field(pool.regen, p) or 0, Field(pool.delay, p) or 0\n" +
			"\t\t\t\t\tlocal value = Refii.player_value(p, key, max)\n" +
			"\t\t\t\t\tlocal rest = Refii.player_value(p, key .. '.rest', delay)\n" +
			"\t\t\t\t\tlocal v, r = value, rest + dt\n" +
			"\t\t\t\t\tif r >= delay and v < max then v = v + regen * dt end\n" +
			"\t\t\t\t\tif v > max then v = max elseif v < floor then v = floor end\n" +
			"\t\t\t\t\tif r > delay then r = delay end\n" +
			"\t\t\t\t\tif v ~= value or Refii.player_value(p, key) == nil then Refii.player_set(p, key, v) end\n" +
			"\t\t\t\t\tif r ~= rest then Refii.player_set(p, key .. '.rest', r) end\n" +
			"\t\t\t\t\tif Refii.player_value(p, pool.max_key) ~= max then Refii.player_set(p, pool.max_key, max) end\n" +
			"\t\t\t\tend)\n" +
			"\t\t\t\tif not ok then Refii._report('pool ' .. tostring(key), err) end\n" +
			"\t\t\tend\n" +
			"\t\tend\n" +
			"\tend\n" +
			"end\n" +
			"Refii._runtime[" + tick + "] = function(e)\n" +
			"\tlocal dt = e.seconds or 0\n" +
			"\tRefii._real_clock = Refii._real_clock + dt\n" +
			"\tif next(Refii._play.modes) ~= nil then return end\n" +
			"\tRefii._clock = Refii._clock + dt\n" +
			"\tRunTimers()\n" +
			"\tif next(Refii._pools) ~= nil then TickPools(dt) end\n" +
			"end\n" +
			"function Refii.pool(p, key, o)\n" +
			"\tif p == nil or key == nil then return nil end\n" +
			"\to = o or {}\n" +
			"\tlocal list = Refii._pools[p] or {}\n" +
			"\tRefii._pools[p] = list\n" +
			"\tlocal pool = list[key] or {}\n" +
			"\tpool.max, pool.floor, pool.regen, pool.delay = o.max or 100, o.floor or 0, o.regen or 0, o.delay or 0\n" +
			"\tpool.max_key = o.max_key or (key .. '.max')\n" +
			"\tlist[key] = pool\n" +
			"\treturn Refii.pool_value(p, key)\n" +
			"end\n" +
			"function Refii.pool_value(p, key)\n" +
			"\tlocal pool = Refii._pools[p] and Refii._pools[p][key]\n" +
			"\tif pool == nil then return nil end\n" +
			"\tlocal max = Field(pool.max, p) or 0\n" +
			"\treturn Refii.player_value(p, key, max), max\n" +
			"end\n" +
			"function Refii.pool_spend(p, key, n)\n" +
			"\tlocal pool = Refii._pools[p] and Refii._pools[p][key]\n" +
			"\tif pool == nil then return false end\n" +
			"\tlocal value = Refii.player_value(p, key)\n" +
			"\tif value == nil then return true end\n" +
			"\tn = tonumber(n) or 0\n" +
			"\tlocal floor = Field(pool.floor, p) or 0\n" +
			"\tif value < n and not (floor < 0 and value > 0) then return false end\n" +
			"\tlocal take = n\n" +
			"\tif value - take < floor then take = value - floor end\n" +
			"\tif take > 0 then Refii.player_add(p, key, -take) end\n" +
			"\tRefii.player_set(p, key .. '.rest', 0)\n" +
			"\treturn true\n" +
			"end\n" +
			"function Refii.entity_value(e, key, default)\n" +
			"\tlocal t = e ~= nil and Refii._entity_store[e] or nil\n" +
			"\tlocal v = t and t[key]\n" +
			"\tif v == nil then return default end\n" +
			"\treturn v\n" +
			"end\n" +
			"function Refii.entity_set(e, key, v)\n" +
			"\tif e == nil or e == 0 or key == nil then return end\n" +
			"\tlocal t = Refii._entity_store[e]\n" +
			"\tif t == nil then\n" +
			"\t\tif v == nil then return end\n" +
			"\t\tt = {}\n" +
			"\t\tRefii._entity_store[e] = t\n" +
			"\tend\n" +
			"\tt[key] = v\n" +
			"\tRefii._want(" + removed + ")\n" +
			"end\n" +
			"Refii._runtime[" + removed + "] = function(e) if e.entity ~= nil then Refii._entity_store[e.entity] = nil end end\n" +
			"Refii._runtime[" + paused + "] = function(e) Refii._play.modes[e.mode or 1] = (e.paused == true or e.paused == 1) and true or nil end\n" +
			"Refii._runtime[" + unloading + "] = function(e) Refii._play.loading = true end\n" +
			"Refii._runtime[" + loaded + "] = function(e) Refii._play.loading = false end\n" +
			"function Refii.in_play(p)\n" +
			"\tlocal s = Refii._play\n" +
			"\tif s.loading or next(s.modes) ~= nil then return false end\n" +
			"\tif Refii.player_hero == nil or Refii.player_hero(p or 1) == nil then return false end\n" +
			"\tif Refii.in_cutscene and Refii.in_cutscene(p or 0, Refii.CUTSCENE_FULL) then return false end\n" +
			"\treturn true\n" +
			"end\n" +
			"function Refii._setting_changed(key, value)\n" +
			"\tRefiiSettings = RefiiSettings or {}\n" +
			"\tlocal previous = RefiiSettings[key]\n" +
			"\tRefiiSettings[key] = value\n" +
			"\tif previous == value then return end\n" +
			"\tRefii._raise_local('setting_changed', { key = key, value = value, previous = previous })\n" +
			"end\n" +
			LaneRuntime +
			"Refii._want(" + tick + ") Refii._want(" + paused + ") Refii._want(" + unloading + ") Refii._want(" + loaded + ")\n" +
			"if next(Refii._entity_store) ~= nil then Refii._want(" + removed + ") end\n");
		return text.ToString();
	}

	/// <summary>
	/// THE LUA END OF THE GODOT LANE (<see cref="ModLuaLane"/>), in the runtime: what makes the Lua and
	/// Godot surfaces the same (ModApi.Twins).
	/// <list type="bullet">
	/// <item><c>Refii._raise_local(name, e)</c> — runs the runtime's own events' handlers
	/// (<c>setting_changed</c>, <c>mod_signal</c>, <c>value_changed</c>), each protected.</item>
	/// <item><c>Refii.get_text(key)</c> / <c>Refii.has_text(key)</c> — the published texts as the host
	/// states them (every surface's <c>publish_text</c>; Lua's own at once), nil / false for none.</item>
	/// <item><c>mod_signal {mod, name, payload}</c> — a Godot script's <c>signal_lua(name, payload)</c>
	/// (<c>Refii._godot_signal</c>), the twin of <c>Refii.signal</c> the other way.</item>
	/// <item><c>value_changed {key, value}</c> — a published number or text changed, whoever published it
	/// (<c>Refii._host_values</c>); a handler's subscription asks the host to state numbers
	/// (<c>refii.watch</c>).</item>
	/// <item><c>Refii.ask_host(what, fn, ...)</c> — a read or action of the host (ModApi.HostCalls),
	/// <c>fn(value)</c> called with its answer on a later tick; <c>set_setting</c>, <c>save_snapshot</c> and
	/// <c>load_snapshot</c> are its actions by name.</item>
	/// <item><c>Refii._godot_call(id, fn)</c> — a Godot <c>call_lua</c>: fn's first five values back on the lane.</item>
	/// </list>
	/// </summary>
	public const string LaneRuntime = """
		if Refii._texts == nil and Refii.signal ~= nil then Refii.signal('refii.hello', '') end
		Refii._texts = Refii._texts or {}
		Refii._asks = Refii._asks or {}
		Refii._ask_seq = Refii._ask_seq or 0
		function Refii._raise_local(name, e)
			local list = Refii._handlers[name]
			if list == nil then return end
			for i = 1, #list do
				local ok, err = pcall(list[i], e)
				if not ok then Refii._report(name, err) end
			end
		end
		function Refii._encode(v)
			local t = type(v)
			if t == 'number' then return 'n' .. tostring(v) end
			if t == 'boolean' then return v and 'b1' or 'b0' end
			if t == 'string' then return 's' .. (string.gsub((string.gsub(v, '\\', '\\\\')), '\n', '\\n')) end
			return '-'
		end
		if Refii._publish_text == nil and Refii.publish_text ~= nil then Refii._publish_text = Refii.publish_text end
		function Refii.publish_text(key, text)
			if Refii._publish_text == nil then return false end
			local ok = Refii._publish_text(key, text)
			if ok then Refii._texts[key] = tostring(text) end
			return ok
		end
		function Refii.get_text(key) return Refii._texts[key] end
		function Refii.has_text(key) return Refii._texts[key] ~= nil end
		function Refii._host_values(list, changed)
			for i = 1, #list do
				local item = list[i]
				if item[3] then Refii._texts[item[1]] = item[2] end
				if changed then Refii._raise_local('value_changed', { key = item[1], value = item[2] }) end
			end
		end
		function Refii._godot_signal(scoped, payload)
			local dot = string.find(scoped, '.', 1, true)
			if dot == nil then return end
			Refii._raise_local('mod_signal', { mod = string.sub(scoped, 1, dot - 1), name = string.sub(scoped, dot + 1), payload = payload })
		end
		function Refii.ask_host(what, fn, a, b, c, d) return Refii._ask_as(nil, what, fn, a, b, c, d) end
		function Refii._ask_as(mod, what, fn, a, b, c, d)
			if Refii.signal == nil or what == nil then return false end
			local id = 0
			if type(fn) == 'function' then
				Refii._ask_seq = Refii._ask_seq + 1
				id = Refii._ask_seq
				Refii._asks[id] = fn
			end
			local E = Refii._encode
			local text = tostring(id) .. '\n' .. tostring(what) .. '\n' .. E(a) .. '\n' .. E(b) .. '\n' .. E(c) .. '\n' .. E(d) .. '\n' .. E(mod)
			if Refii.signal('refii.ask', text) then return true end
			Refii._asks[id] = nil
			return false
		end
		function Refii._host_reply(id, value)
			local fn = Refii._asks[id]
			Refii._asks[id] = nil
			if fn == nil then return end
			local ok, err = pcall(fn, value)
			if not ok then Refii._report('ask_host ' .. tostring(id), err) end
		end
		function Refii.set_setting(key, value) return Refii.ask_host('set_setting', nil, key, value) end
		function Refii.save_snapshot(key, fn) return Refii.ask_host('save_snapshot', fn, key) end
		function Refii.load_snapshot(key, fn) return Refii.ask_host('load_snapshot', fn, key) end
		function Refii._godot_call(id, fn)
			local ok, a, b, c, d, e = pcall(fn)
			if not ok then
				Refii._report('call_lua ' .. tostring(id), a)
				a, b, c, d, e = nil, nil, nil, nil, nil
			end
			if Refii.signal == nil then return end
			local E = Refii._encode
			Refii.signal('refii.answer', tostring(id) .. '\n' .. (ok and 'b1' or 'b0') .. '\n' .. E(a) .. '\n' .. E(b) .. '\n' .. E(c) .. '\n' .. E(d) .. '\n' .. E(e))
		end

		""";

	/// <summary>
	/// A mod's Lua for the TITLE and the FRONT END, where there is no game VM: run in the host's Lua
	/// state for the current front-end presentation (Fable2/src/vfs/src/hooks/mod_frontend_lua.cpp:
	/// the game's Lua 5.1 with float numbers, base, string, table and math), with no world, no hero
	/// and no save. Its own entry point, as a client and
	/// a server entry point are separate: init.lua patches the game's scripts, which do not exist
	/// there. What works in it is <see cref="FrontendPreamble"/>'s list.
	/// </summary>
	public const string ModFrontendPath = "mod/frontend.lua";

	/// <summary>
	/// What the init chunk starts with whenever a mod ships one: <c>RefiiModSave</c>, a table
	/// that persists WITH THE SAVE. The game builds its save through the global <c>GetSaveTable</c>
	/// and restores through <c>LoadScriptSave</c> (saveloadsystem.lua; the engine looks both up by
	/// name every time — TU1 0x8245C7B8 / 0x8245CC90), so wrapping them carries one more key. Plain
	/// data only (numbers, strings, tables of them): Pluto copies those by value. The shipped
	/// LoadScriptSave ignores keys it does not know, so a save made with mods loads without them.
	/// </summary>
	private const string SavePreamble =
		"RefiiModSave = RefiiModSave or {}\n" +
		"if GetSaveTable and LoadScriptSave and not RefiiModSaveWrapped then\n" +
		"\tRefiiModSaveWrapped = true\n" +
		"\tlocal get_save, load_save = GetSaveTable, LoadScriptSave\n" +
		"\tGetSaveTable = function() local t = get_save() t.RefiiMods = RefiiModSave return t end\n" +
		"\tLoadScriptSave = function(t)\n" +
		"\t\tif t and type(t.RefiiMods) == 'table' then RefiiModSave = t.RefiiMods end\n" +
		"\t\treturn load_save(t)\n" +
		"\tend\n" +
		"end\n";

	/// <summary>
	/// THE LUA SIDE OF THE MOD API, ahead of every mod's init: <c>Refii.on(event, fn)</c>
	/// subscribes a handler to a named event (the names and fields are
	/// <c>core/refii_mod_events.h</c>'s, lower-case: "hit", "spell_launch", "tick" ...), run by
	/// the payload's event bus as <c>Refii._fire(kind, fields)</c>; a handler that returns
	/// <c>true</c> cancels where the event allows it. <c>Refii.setting(key, default)</c> reads the
	/// mods' settings.json values composed in as <c>RefiiSettings</c>; <c>Refii.hero(player)</c>
	/// is the player's hero id as the bus last stated it. A handler's error lands in
	/// <c>RefiiModErrors</c> and never stops the others. The kinds table mirrors
	/// <see cref="ModApi.Events"/>, which is checked by a test.
	/// </summary>
    public static string RuntimePreamble() => RuntimePreamble(ModSettings.InForce);

	/// <summary>The preamble with <paramref name="settings"/> as its <c>RefiiSettings</c>: the values in force, at run time.</summary>
	public static string RuntimePreamble(IEnumerable<(string Key, float Value)> settings)
	{
		var text = new StringBuilder();
		text.Append("Refii = Refii or {}\n");
		text.Append("Refii.api_version = ").Append(ModApi.Version).Append('\n');
		text.Append("Refii._handlers = Refii._handlers or {}\n");
		// THE SUBSCRIPTION MASKS ARE GROUPS OF ModApi.LuaMaskBits (20) BITS: this Lua's number is a
		// 32-bit float, exact only to 24 bits, so a 31-bit sum loses its low bits once a high one is
		// set. Group g holds kinds 20*(g-1) .. 20*g-1; the payload reads ModApi.LuaMaskGroups of
		// them (mod_events.cpp ReadLuaMask), so the capacity is ModApi.LuaMaskCapacity kinds.
		text.Append("Refii._masks = Refii._masks or {}\n");
		text.Append("Refii._dirty = false\n");
		text.Append("Refii._hero = Refii._hero or {}\n");
		// The cutscene statements' bits (REFII_CUTSCENE_*): the cutscene event's kind and in_cutscene's.
		text.Append("Refii.CUTSCENE_FULL = ").Append(ModApi.CutsceneFull)
			.Append(" Refii.CUTSCENE_INTERACTIVE = ").Append(ModApi.CutsceneInteractive)
			.Append(" Refii.CUTSCENE_LOOK_AT = ").Append(ModApi.CutsceneLookAt).Append('\n');
		text.Append("Refii._kinds = {");
		foreach (ModApiEvent e in ModApi.Events)
		{
			text.Append(e.LuaName).Append('=').Append(e.Kind).Append(',');
		}

		text.Append("}\n");
		// The writable numbers per kind: a handler's change to one goes back through the native
		// Refii._write while the chunk runs (mod_events.cpp WriteLuaField).
		text.Append("Refii._writable = {");
		foreach (IGrouping<int, (int Kind, string Field)> kind in ModApi.WritableFields.GroupBy(w => w.Kind))
		{
			text.Append('[').Append(kind.Key).Append("]={");
			foreach ((_, string field) in kind)
			{
				text.Append('\'').Append(field).Append("',");
			}

			text.Append("},");
		}

		text.Append("}\n");
		text.Append(
			"function Refii.on(name, fn)\n" +
			"\tlocal kind = Refii._kinds[name]\n" +
			// The runtime's own events (setting_changed): raised by the preamble, never by the bus.
			"\tif kind == nil and Refii._local_events and Refii._local_events[name] and type(fn) == 'function' then\n" +
			"\t\tlocal own = Refii._handlers[name] or {}\n" +
			"\t\tRefii._handlers[name] = own\n" +
			"\t\town[#own + 1] = fn\n" +
			// A value_changed handler asks the host to state published numbers (texts it always states).
			"\t\tif name == 'value_changed' and Refii.signal then Refii.signal('refii.watch', 'value_changed') end\n" +
			"\t\treturn\n" +
			"\tend\n" +
			"\tif kind == nil then error('Refii.on: no event \"' .. tostring(name) .. '\"') end\n" +
			"\tif type(fn) ~= 'function' then error('Refii.on: a function is needed for \"' .. name .. '\"') end\n" +
			"\tlocal list = Refii._handlers[kind]\n" +
			"\tif list == nil then list = {} Refii._handlers[kind] = list end\n" +
			"\tlist[#list + 1] = fn\n" +
			"\tlocal group = math.floor(kind / " + ModApi.LuaMaskBits + ") + 1\n" +
			"\tlocal bit = 2 ^ (kind % " + ModApi.LuaMaskBits + ")\n" +
			"\tlocal mask = Refii._masks[group] or 0\n" +
			"\tif math.floor(mask / bit) % 2 == 0 then Refii._masks[group] = mask + bit end\n" +
			"\tRefii._dirty = true\n" +
			"end\n" +
			// off(name, fn): the handler on() took, taken back (Godot's remove_verdict / disconnect); true when it stood.
			"function Refii.off(name, fn)\n" +
			"\tlocal list = Refii._handlers[Refii._kinds[name] or name]\n" +
			"\tif list == nil then return false end\n" +
			"\tfor i = #list, 1, -1 do\n" +
			"\t\tif list[i] == fn then table.remove(list, i) return true end\n" +
			"\tend\n" +
			"\treturn false\n" +
			"end\n" +
			"function Refii._fire(kind, e)\n" +
			// The runtime's own consumers first (the clock and timers, the pools, the entity store, the play state).
			"\tlocal runtime = Refii._runtime and Refii._runtime[kind]\n" +
			"\tif runtime then runtime(e) end\n" +
			"\tlocal list = Refii._handlers[kind]\n" +
			"\tif list == nil then return true end\n" +
			"\tlocal writable = Refii._writable[kind]\n" +
			"\tlocal before = nil\n" +
			"\tif writable then before = {} for i = 1, #writable do before[i] = e[writable[i]] end end\n" +
			"\tlocal keep = true\n" +
			"\tfor i = 1, #list do\n" +
			"\t\tlocal ok, cancel = pcall(list[i], e)\n" +
			"\t\tif not ok then Refii._report('event ' .. tostring(kind), cancel)\n" +
			// A refusal is a handler's `return true` or `e.cancel = true`, as a Godot verdict's.
			"\t\telseif cancel == true or e.cancel == true then keep = false end\n" +
			"\tend\n" +
			"\tif writable and Refii._write then\n" +
			"\t\tfor i = 1, #writable do\n" +
			"\t\t\tlocal v = e[writable[i]]\n" +
			"\t\t\tif v ~= before[i] then\n" +
			"\t\t\t\tif type(v) == 'number' then Refii._write(kind, writable[i], v)\n" +
			"\t\t\t\telseif type(v) == 'boolean' then Refii._write(kind, writable[i], v and 1 or 0) end\n" +
			"\t\t\tend\n" +
			"\t\tend\n" +
			"\tend\n" +
			"\treturn keep\n" +
			"end\n" +
			"function Refii._report(where, err)\n" +
			"\tlocal text = tostring(where) .. ': ' .. tostring(err)\n" +
			"\tRefiiModErrors = (RefiiModErrors or '') .. text .. '\\n'\n" +
			"\tif #RefiiModErrors > 4096 then RefiiModErrors = string.sub(RefiiModErrors, -4096) end\n" +
			"\tif Refii._error then Refii._error(text) end\n" +
			"end\n" +
			"function Refii.setting(key, default)\n" +
			"\tlocal v = RefiiSettings and RefiiSettings[key]\n" +
			"\tif v == nil then return default end\n" +
			"\treturn v\n" +
			"end\n" +
			"function Refii.hero(player)\n" +
			"\tlocal h = Refii._hero[player or 1]\n" +
			"\tif h == nil or h == 0 then return nil end\n" +
			"\treturn h\n" +
			"end\n" +
			MenuRowRuntime +
			MenuRowsRuntime +
			// play_fx / play_fx_on unpack the options table for the native Refii._play_fx (the
			// game's Lua C API here has no array reads): duration, scale, tint {r, g, b},
			// intensity, loop (true unless false), offset {x, y, z}, at (a model helper name).
			"function Refii._fx_options(o)\n" +
			"\to = o or {}\n" +
			"\tlocal t = o.tint or {}\n" +
			"\tlocal f = o.offset or {}\n" +
			"\treturn o.duration or 0, o.scale or 1, t[1] or t.r or 1, t[2] or t.g or 1, t[3] or t.b or 1,\n" +
			"\t\to.intensity or 1, (o.loop == false) and 0 or 1, f[1] or f.x or 0, f[2] or f.y or 0, f[3] or f.z or 0, o.at or ''\n" +
			"end\n" +
			"function Refii.play_fx(name, x, y, z, o)\n" +
			"\tif Refii._play_fx == nil then return nil end\n" +
			"\treturn Refii._play_fx(name, x or 0, y or 0, z or 0, 0, Refii._fx_options(o))\n" +
			"end\n" +
			"function Refii.play_fx_on(e, name, o)\n" +
			"\tif Refii._play_fx == nil or e == nil or e == 0 then return nil end\n" +
			"\treturn Refii._play_fx(name, 0, 0, 0, e, Refii._fx_options(o))\n" +
			"end\n" +
			// cast_spell(caster, type, targeted, target, level [, pos [, dir [, options]]]) -> spell, serial:
			// the native Refii._cast_spell with the start position and facing unpacked from {x, y, z}
			// tables (the caster's own when absent) and options { tag, damage_scale, at }
			// (refii_mod_cast_options; `at` a handle is REFII_MOD_CAST_AT_ENTITY, a {x, y, z} table
			// REFII_MOD_CAST_AT_POINT); targeted is a boolean or 0/1.
			"function Refii.cast_spell(caster, spell_type, targeted, target, level, pos, dir, options)\n" +
			"\tif Refii._cast_spell == nil or caster == nil or caster == 0 then return nil end\n" +
			"\tlocal p = pos or {}\n" +
			"\tlocal d = dir or {}\n" +
			"\tlocal o = options or {}\n" +
			"\tlocal t = (targeted == true or (type(targeted) == 'number' and targeted ~= 0)) and 1 or 0\n" +
			"\tlocal ak, ae, a = 0, 0, {}\n" +
			"\tif type(o.at) == 'table' then ak, a = 2, o.at elseif o.at ~= nil then ak, ae = 1, o.at end\n" +
			"\treturn Refii._cast_spell(caster, spell_type, t, target or 0, level or 0, pos ~= nil and 1 or 0,\n" +
			"\t\tp[1] or p.x or 0, p[2] or p.y or 0, p[3] or p.z or 0, dir ~= nil and 1 or 0, d[1] or d.x or 0, d[2] or d.y or 0, d[3] or d.z or 0,\n" +
			"\t\ttonumber(o.tag) or 0, tonumber(o.damage_scale) or 1, ak, ae, a[1] or a.x or 0, a[2] or a.y or 0, a[3] or a.z or 0)\n" +
			"end\n" +
			// set_locomotion(p, {key = value, ..}) or (p, key, value) -> bool: each LocomotionData field
			// on player p's hero's own table through the native Refii._set_locomotion, held across the
			// game's reloads of it; false as a value stops holding that field (refii_mod_set_locomotion).
			"function Refii.set_locomotion(p, values, value)\n" +
			"\tif Refii._set_locomotion == nil then return false end\n" +
			"\tif type(values) ~= 'table' then return Refii._set_locomotion(p, values, value) end\n" +
			"\tlocal ok = true\n" +
			"\tfor k, v in pairs(values) do ok = Refii._set_locomotion(p, k, v) and ok end\n" +
			"\treturn ok\n" +
			"end\n" +
			// set_appearance(p, {tan, fat, height, strength, skill, will, morality, purity, age}): player
			// p's look to manual with the named inputs, the rest as the hero looks (Murgo's Mirror's own
			// look, refii_mod_set_appearance through the native Refii._set_appearance).
			"function Refii.set_appearance(p, t)\n" +
			"\tif Refii._set_appearance == nil or type(t) ~= 'table' then return end\n" +
			"\tRefii._set_appearance(p, t.tan, t.fat, t.height, t.strength, t.skill, t.will, t.morality, t.purity, t.age)\n" +
			"end\n" +
			// camera_override(p, mode [, o]): player p's camera hold through the game's own
			// CameraManager.SetCameraOverride (refii_mod_camera_override): mode Refii.CAMERA_FIRST_PERSON or
			// CAMERA_GAME, nil or 0 lets it go; o = {fov, pitch_limit, blend, aim}.
			"function Refii.camera_override(p, mode, o)\n" +
			"\tif Refii._camera_override == nil then return false end\n" +
			"\to = type(o) == 'table' and o or {}\n" +
			"\treturn Refii._camera_override(p or 0, mode or 0, o.fov or 0, o.pitch_limit or 0, o.blend or -1, o.aim and 1 or 0)\n" +
			"end\n" +
			// mark(target, icon, text [, p [, o]]): a HUD world marker over an entity handle or at a point
			// {x, y, z} for player p (nil: every player played here); o.meter = {value, max, style}
			// puts a fill on it (mark_meter). The id unmark and mark_meter take, or nil.
			"function Refii.mark(target, icon, text, p, o)\n" +
			"\tif Refii._mark == nil or target == nil then return nil end\n" +
			"\tlocal id = nil\n" +
			"\tif type(target) == 'table' then\n" +
			"\t\tid = Refii._mark(0, target[1] or target.x or 0, target[2] or target.y or 0, target[3] or target.z or 0, icon or '', text or '', p or 0)\n" +
			"\telseif target ~= 0 then\n" +
			"\t\tid = Refii._mark(target, 0, 0, 0, icon or '', text or '', p or 0)\n" +
			"\tend\n" +
			"\tlocal m = type(o) == 'table' and o.meter or nil\n" +
			"\tif id == nil and type(m) == 'table' then return Refii.world_meter(target, m[1] or m.value or 0, m[2] or m.max or 0, m[3] or m.style, p) end\n" +
			"\tif id ~= nil and type(m) == 'table' and Refii.mark_meter then Refii.mark_meter(id, m[1] or m.value or 0, m[2] or m.max or 0, m[3] or m.style or '') end\n" +
			"\treturn id\n" +
			"end\n" +
			// world_meter(target, value, max [, style [, p]]): a bar on the marker channel over an
			// entity handle or at {x, y, z}; mark_meter(id, value, max) restates it. The id, or nil.
			"function Refii.world_meter(target, value, max, style, p)\n" +
			"\tif Refii._world_meter == nil or target == nil then return nil end\n" +
			"\tif type(target) == 'table' then\n" +
			"\t\treturn Refii._world_meter(0, target[1] or target.x or 0, target[2] or target.y or 0, target[3] or target.z or 0, value or 0, max or 0, style or '', p or 0)\n" +
			"\tend\n" +
			"\tif target == 0 then return nil end\n" +
			"\treturn Refii._world_meter(target, 0, 0, 0, value or 0, max or 0, style or '', p or 0)\n" +
			"end\n" +
			// light(target, color, energy, range [, o]): a mod light on an entity handle or at a
			// point {x, y, z}; color {r, g, b}; o = { offset = {x, y, z}, at = "helper", flicker = n,
			// direction = {x, y, z}, cone = degrees, attenuation = n } — a cone above 0 is a spot
			// along the direction. The id light_remove takes, or nil.
			"function Refii.light(target, color, energy, range, o)\n" +
			"\tif Refii._light == nil or target == nil then return nil end\n" +
			"\to = o or {}\n" +
			"\tlocal c = color or {}\n" +
			"\tlocal f = o.offset or {}\n" +
			"\tlocal d = o.direction or {}\n" +
			"\tlocal r, g, b = c[1] or c.r or 1, c[2] or c.g or 1, c[3] or c.b or 1\n" +
			"\tif type(target) == 'table' then\n" +
			"\t\treturn Refii._light(0, target[1] or target.x or 0, target[2] or target.y or 0, target[3] or target.z or 0, r, g, b,\n" +
			"\t\t\tenergy or 1, range or 0, f[1] or f.x or 0, f[2] or f.y or 0, f[3] or f.z or 0, o.at or '', o.flicker or 0,\n" +
			"\t\t\td[1] or d.x or 0, d[2] or d.y or 0, d[3] or d.z or 0, o.cone or 0, o.attenuation or 0)\n" +
			"\tend\n" +
			"\tif target == 0 then return nil end\n" +
			"\treturn Refii._light(target, 0, 0, 0, r, g, b, energy or 1, range or 0, f[1] or f.x or 0, f[2] or f.y or 0, f[3] or f.z or 0, o.at or '', o.flicker or 0,\n" +
			"\t\td[1] or d.x or 0, d[2] or d.y or 0, d[3] or d.z or 0, o.cone or 0, o.attenuation or 0)\n" +
			"end\n" +
			// text(key_or_text [, a .. h]): a mod's text in the culture the game runs in — a bare key, or
			// each {KEY} inside a text, through the host's lookup (the native Refii._text, refii_mod_text:
			// Engine_ModText) — then %1 .. %8 replaced by the arguments (a marker with none stays); as
			// written when nothing knows it.
			"function Refii.text(text, a, b, c, d, e, f, g, h)\n" +
			"\tlocal s = tostring(text or '')\n" +
			"\tif Refii._text ~= nil then s = Refii._text(s) or s end\n" +
			"\tlocal args = { a, b, c, d, e, f, g, h }\n" +
			"\treturn (string.gsub(s, '%%(%d+)', function(i)\n" +
			"\t\tlocal v = args[tonumber(i)]\n" +
			"\t\tif v == nil then return '%' .. i end\n" +
			"\t\treturn tostring(v)\n" +
			"\tend))\n" +
			"end\n" +
			// message_box(title, text [, accept [, cancel [, p]]]): a question when cancel is given
			// (nil captions keep the box's own), else a notice; the id mod_message_box carries.
			"function Refii.message_box(title, text, accept, cancel, p)\n" +
			"\tif Refii._message_box == nil then return nil end\n" +
			"\treturn Refii._message_box(title or '', text or '', accept or '', cancel or '', cancel ~= nil and 1 or 0, p or 0)\n" +
			"end\n" +
			// menu_box(title, text, options [, p [, cancelable]]): up to 12 options from the table
			// (cancelable unless false); the id mod_menu_box carries.
			"function Refii.menu_box(title, text, options, p, cancelable)\n" +
			"\tif Refii._menu_box == nil or type(options) ~= 'table' then return nil end\n" +
			"\tlocal o = {}\n" +
			"\tfor i = 1, 12 do o[i] = options[i] ~= nil and tostring(options[i]) or '' end\n" +
			"\tlocal n = 0\n" +
			"\twhile n < 12 and options[n + 1] ~= nil do n = n + 1 end\n" +
			"\treturn Refii._menu_box(title or '', text or '', p or 0, cancelable == false and 0 or 1, n,\n" +
			"\t\to[1], o[2], o[3], o[4], o[5], o[6], o[7], o[8], o[9], o[10], o[11], o[12])\n" +
			"end\n");

		text.Append(RequireRuntime);
		text.Append(InfrastructureRuntime());
		// The values IN FORCE, the live overrides over the files': what the native table holds, so
		// a session or a reload composed after a live change starts from that change.
		text.Append("RefiiSettings = {");
		foreach ((string key, float value) in settings)
		{
			text.Append("[\"").Append(key.Replace("\\", "\\\\").Replace("\"", "\\\""))
				.Append("\"]=").Append(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
		}

		text.Append("}\n");
		return text.ToString();
	}

	/// <summary>
	/// Lua that OTHER composers contribute to the init chunk, after every mod's own init.lua:
	/// the spells kind's generated handlers (Engine_ModSpells), a template's glue. Each entry is
	/// (a label for errors, the source); it is wrapped like a mod's file and runs in the game VM.
	/// Cleared and refilled on every mount by whoever contributes (register before
	/// <see cref="Apply"/> runs, i.e. during the overlay pass).
	/// </summary>
	public static readonly List<(string Label, string Source)> ExtraInitSources = new();

	/// <summary>
	/// <c>Refii._menu_row(path, arg, e)</c>, which a mod menu row's <c>Mod:lua:&lt;function&gt;[:&lt;arg&gt;]</c>
	/// calls (Engine_UiModMenuCommand.LuaCall): the function is a global or a dotted path through
	/// tables, LOOKED UP, never compiled, and run protected as <c>function(arg, e)</c>.
	/// </summary>
	public const string MenuRowRuntime =
		"function Refii._menu_row(path, arg, e)\n" +
		"\tlocal f = _G\n" +
		"\tlocal at = 1\n" +
		"\twhile f ~= nil do\n" +
		"\t\tlocal dot = string.find(path, '.', at, true)\n" +
		"\t\tlocal part = string.sub(path, at, (dot or 0) - 1)\n" +
		"\t\tif type(f) ~= 'table' then f = nil break end\n" +
		"\t\tf = f[part]\n" +
		"\t\tif dot == nil then break end\n" +
		"\t\tat = dot + 1\n" +
		"\tend\n" +
		"\tlocal where = 'menu row ' .. tostring(e.menu) .. '/' .. tostring(e.row)\n" +
		"\tif type(f) ~= 'function' then Refii._report(where, 'no Lua function ' .. tostring(path)) return end\n" +
		"\tlocal ok, err = pcall(f, arg, e)\n" +
		"\tif not ok then Refii._report(where, err) end\n" +
		"end\n";

	/// <summary>
	/// <c>Refii.set_menu_rows(menu, rows [, p])</c>: writes the rows table — strings, or
	/// <c>{ name, label, command, ghosted }</c> — as the bus text Engine_UiModRows reads
	/// (<c>name|label|command|ghosted</c> by <c>;</c>, <c>\</c> escaping) for the native
	/// <c>Refii._menu_rows</c>, which the game's Lua C API needs: it has no table reads.
	/// </summary>
	public const string MenuRowsRuntime = """
		function Refii.set_menu_rows(menu, rows, p)
			if Refii._menu_rows == nil or type(rows) ~= 'table' then return false end
			local function esc(v)
				v = string.gsub(tostring(v or ''), '\\', '\\\\')
				v = string.gsub(v, '|', '\\|')
				return (string.gsub(v, ';', '\\;'))
			end
			local text = ''
			for i = 1, #rows do
				local r = rows[i]
				local row
				if type(r) == 'table' then
					row = esc(r.name or r.label or i) .. '|' .. esc(r.label or '') .. '|' .. esc(r.command or '') .. '|' .. (r.ghosted and '1' or '0')
				else
					row = esc(r) .. '|' .. esc(r) .. '||0'
				end
				if i > 1 then text = text .. ';' end
				text = text .. row
			end
			return Refii._menu_rows(menu or '', text, p or 0)
		end

		""";

	/// <summary>
	/// A mod's Lua text as it runs in the init chunk: the body of a protected function, its error
	/// appended to <c>RefiiModErrors</c>. The Studio's live run sends this through the debug
	/// channel so a chunk run live behaves exactly as it does at session start.
	/// </summary>
	public static string Wrap(string mod, string source)
	{
		var text = new StringBuilder();
		AppendWrapped(text, mod, source);
		return text.ToString();
	}

	/// <summary>The keys the payload hook reads the composed chunks from.</summary>
	public const string InitKey = "refii/mod_init.lua";
	public const string CameraKey = "refii/mod_camera.lua";

	/// <summary>The front end's composed chunk (<see cref="FrontendPreamble"/> and every mod's
	/// <see cref="ModFrontendPath"/>), read by the payload when a presentation state lacks it.</summary>
	public const string FrontendKey = "refii/mod_frontend.lua";

	/// <summary>
	/// Every <c>.lua</c> under <c>mod/</c>: the three entry points and the modules. All are composed
	/// (never served as assets); the entry points by their role, the rest by name through <c>require</c>.
	/// </summary>
	public static bool IsModLuaPath(string path)
	{
		string normalized = path.Replace('\\', '/').TrimStart('/');
		return normalized.StartsWith(ModLuaFolder, StringComparison.OrdinalIgnoreCase) &&
			normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
			normalized.Length > ModLuaFolder.Length + ".lua".Length;
	}

	/// <summary>Whether a package path is one of the three entry points.</summary>
	public static bool IsEntryPath(string path)
	{
		string normalized = path.Replace('\\', '/').TrimStart('/');
		return normalized.Equals(ModInitPath, StringComparison.OrdinalIgnoreCase) ||
			normalized.Equals(ModCameraPath, StringComparison.OrdinalIgnoreCase) ||
			normalized.Equals(ModFrontendPath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A module's name from its package path: <c>mod/ai/wheel.lua</c> is <c>ai/wheel</c>, what
	/// <c>require("ai/wheel")</c> or <c>require("ai.wheel")</c> resolves. Lower case, as the registry
	/// keys it. False for an entry point or anything outside <c>mod/</c>.
	/// </summary>
	public static bool TryModuleName(string path, out string name)
	{
		name = string.Empty;
		if (!IsModLuaPath(path) || IsEntryPath(path))
		{
			return false;
		}

		string normalized = path.Replace('\\', '/').TrimStart('/');
		name = normalized[ModLuaFolder.Length..^".lua".Length].ToLowerInvariant();
		return name.Length != 0 && !name.Contains('"') && !name.Contains('\\');
	}

	public static bool IsFrontendKey(string path) => path.Equals(FrontendKey, StringComparison.OrdinalIgnoreCase);

	/// <summary>Composes the chunks from <paramref name="overlays"/> (mount order) into the mount.</summary>
	public static void Apply(BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		List<ModSources> sources = Read(overlays);
		Compose(mounted, overlays, sources, ModInitPath, InitKey);
		Compose(mounted, overlays, sources, ModCameraPath, CameraKey);
		Compose(mounted, overlays, sources, ModFrontendPath, FrontendKey);
	}

	/// <summary>One mod's Lua as the package ships it: the entry points by path, the modules by name.</summary>
	public sealed class ModSources
	{
		public string Mod { get; init; } = string.Empty;

		/// <summary>Entry point text by its package path (<see cref="ModInitPath"/> ...).</summary>
		public Dictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Module text by module name, in name order.</summary>
		public SortedDictionary<string, string> Modules { get; } = new(StringComparer.Ordinal);
	}

	/// <summary>
	/// Every enabled mod's Lua files, each checked with the game's compiler: a file that does not
	/// compile is left out and named, so a syntax error cannot fail a whole chunk.
	/// </summary>
	private static List<ModSources> Read(IReadOnlyList<ModOverlay> overlays)
	{
		var sources = new List<ModSources>();
		foreach (ModOverlay overlay in overlays)
		{
			if (!F2ModPackage.TryOpen(overlay.PackagePath, out F2ModPackage? package, out _) || package is null)
			{
				continue;
			}

			var mod = new ModSources { Mod = overlay.Name };
			using (package)
			{
				foreach (F2ModEntry entry in package.Entries)
				{
					string path = entry.Path.Replace('\\', '/').TrimStart('/');
					if (path.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
					{
						path = path["files/".Length..];
					}

					if (!IsModLuaPath(path))
					{
						continue;
					}

					string source = Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
					if (IsEntryPath(path))
					{
						if (Checked(overlay.Name, path, source, source))
						{
							mod.Entries[path.ToLowerInvariant()] = source;
						}
					}
					else if (TryModuleName(path, out string name) &&
						Checked(overlay.Name, path, source, WrapModule(overlay.Name, name, source)))
					{
						mod.Modules[name] = source;
					}
				}
			}

			sources.Add(mod);
		}

		return sources;
	}

	/// <summary>The compile check, on the text as it will be composed; true to include the file.</summary>
	private static bool Checked(string mod, string path, string source, string composed)
	{
		string chunkName = $"@{mod}/{path}";
		if (Refii.Lua.Engine_LuaDecompiler.TryCheckSource(composed, chunkName, out string error))
		{
			return true;
		}

		if (error.Contains("not available", StringComparison.Ordinal))
		{
			GD.PushWarning($"[mods] lua: {mod}/{path} could not be checked ({error}); included unchecked");
			return true;
		}

		GD.PushWarning($"[mods] lua: {mod}/{path} does not compile and is NOT run: {error}");
		return false;
	}

	/// <summary>
	/// THE FRONT END'S RUNTIME, ahead of every mod's mod/frontend.lua: a fresh <c>Refii</c> (so a
	/// state that runs it again — a remount — does not subscribe twice) with the game preamble's pure
	/// Lua (<c>Refii.on</c>, <c>_fire</c>, <c>_report</c>, <c>setting</c> over <c>RefiiSettings</c>,
	/// <c>_menu_row</c>, <c>message_box</c>), and the natives the payload registers in that state as
	/// globals (<c>__refii_*</c>, mod_frontend_lua.cpp), moved under <c>Refii</c>:
	/// <list type="bullet">
	/// <item><c>publish(key, n)</c>, <c>value(key)</c>: the shared value store, which every surface reads.</item>
	/// <item><c>log(text)</c>; a handler's error goes to the Mods error list as in a game.</item>
	/// <item><c>open_menu(name [, p])</c>, <c>message_box(...)</c>, <c>notice(title, text [, seconds [, p]])</c>,
	/// <c>menu_box(title, text, options [, p [, cancelable]])</c>: the host's list, dialog, notices and
	/// menu box, which the front end has (G.14, G.18 #6).</item>
	/// <item><c>menu_player()</c>; <c>_text(text)</c>, the host's text lookup under <c>Refii.text</c>.</item>
	/// </list>
	/// The events heard there are <c>mod_menu_row</c>, <c>mod_message_box</c>, <c>mod_notice</c> and
	/// <c>mod_menu_box</c> — the only ones raised without a world. Everything that needs a world, a hero, an entity or the save (health,
	/// gold, spawn, play_fx, player values, <c>RefiiModSave</c>) is absent: <c>Refii.frontend</c> is
	/// true. Lua globals live in the presentation's state, so another presentation starts fresh;
	/// published values persist.
	/// </summary>
	public static string FrontendPreamble()
	{
		var text = new StringBuilder("Refii = {}\n");
		text.Append(RuntimePreamble());
		text.Append(FrontendNatives);
		return text.ToString();
	}

	/// <summary>
	/// The front-end natives (registered as globals by mod_frontend_lua.cpp's table) under
	/// <c>Refii</c>, and the marks the payload's probe reads (<c>Refii._frontend</c>).
	/// </summary>
	public const string FrontendNatives =
		"Refii._frontend = true\n" +
		"Refii.frontend = true\n" +
		"Refii.publish = __refii_publish\n" +
		"Refii.value = __refii_value\n" +
		"Refii.log = __refii_log\n" +
		"Refii._error = __refii_error\n" +
		"Refii.open_menu = __refii_open_menu\n" +
		"Refii._message_box = __refii_message_box\n" +
		"Refii.notice = __refii_notice\n" +
		"Refii._text = __refii_text\n" +
		"Refii._menu_box = __refii_menu_box\n" +
		"Refii.menu_player = __refii_menu_player\n" +
		"Refii.hash = __refii_hash\n" +
		"Refii.signed_in = __refii_signed_in\n" +
		"Refii.save_count = __refii_save_count\n" +
		"Refii.screen = __refii_screen\n";

	/// <summary>
	/// A remount that changed <see cref="FrontendKey"/> while the front end is up: the current
	/// presentation's state is marked stale, so the next front-end chunk runs the new composition
	/// first. True when posted.
	/// </summary>
	public static bool RefreshFrontend(IReadOnlyList<string> changed)
	{
		if (!changed.Any(IsFrontendKey) || !Refii.Host.HostBridge.IsGuestUp || Refii.Host.HostBridge.IsInGame)
		{
			return false;
		}

		Refii.Host.HostBridge.PostModCommand(FrontendCommand + "if Refii then Refii._frontend = false end");
		return true;
	}

	/// <summary>The guest tick's command running a chunk in the front end's mod Lua (mod_commands.cpp).</summary>
	public const string FrontendCommand = "frontend_lua ";

	private static void Compose(
		BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays, IReadOnlyList<ModSources> sources,
		string modPath, string key)
	{
		var text = new StringBuilder();
		var included = new List<string>();
		foreach (ModSources mod in sources)
		{
			bool entry = mod.Entries.TryGetValue(modPath, out string? source);
			// THE MODULES RIDE THE INIT CHUNK (the game VM's registry, which the camera chunk shares)
			// AND THE FRONT-END CHUNK (its own state). A mod with modules and no entry point still
			// registers them: another entry point of the same mod may require one.
			bool modules = mod.Modules.Count != 0 && key != CameraKey;
			if (!entry && !modules)
			{
				continue;
			}

			text.Append("do\n");
			AppendRequire(text, mod.Mod);
			if (modules)
			{
				AppendModules(text, mod.Mod, mod.Modules);
			}

			if (entry)
			{
				AppendProtected(text, mod.Mod, source!);
				included.Add(mod.Mod);
			}

			text.Append("end\n");
		}

		if (key == InitKey)
		{
			foreach ((string label, string source) in ExtraInitSources)
			{
				if (!Refii.Lua.Engine_LuaDecompiler.TryCheckSource(source, "@" + label, out string extraError) &&
					!extraError.Contains("not available", StringComparison.Ordinal))
				{
					GD.PushWarning($"[mods] lua: {label} does not compile and is NOT run: {extraError}");
					continue;
				}

				AppendWrapped(text, label, source);
				included.Add(label);
			}
		}

		// THE PREAMBLES STAND WHENEVER A MOD IS ENABLED, init.lua or not: a native-only mod keeps
		// its state in RefiiModSave and subscribes nothing in Lua, and without the preamble that
		// table was never carried through the save — Spell Weaving alone lost its level and its
		// partners on every load.
		if (key == InitKey && overlays.Count != 0)
		{
			text.Insert(0, SavePreamble + RuntimePreamble());
		}
		else if (key == FrontendKey && overlays.Count != 0)
		{
			text.Insert(0, FrontendPreamble());
		}
		else if (included.Count == 0)
		{
			return;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
		mounted.AddVirtualFile(key, key, bytes.Length, () => bytes, string.Join(",", included));
		GD.Print($"[mods] lua: {modPath} from {(included.Count == 0 ? "the preamble only" : string.Join(", ", included))} " +
			$"composed into {key} ({bytes.Length} bytes)");
	}

	/// <summary>The mod's own <c>require</c>, a local of the block its chunks and modules close over.</summary>
	private static void AppendRequire(StringBuilder text, string mod)
	{
		text.Append("local require = Refii._require(\"").Append(Escape(mod)).Append("\")\n");
		text.Append("local Refii = Refii and Refii._as_mod and Refii._as_mod(\"").Append(Escape(mod)).Append("\") or Refii\n");
	}

	/// <summary>
	/// The mod's modules registered under a fresh scope, each as <c>function(...)</c> over its text —
	/// so a module's top-level <c>return</c> and <c>local</c>s are its own, and it runs only when
	/// required.
	/// </summary>
	private static void AppendModules(StringBuilder text, string mod, IReadOnlyDictionary<string, string> modules)
	{
		text.Append("local __scope = Refii._mod_scope(\"").Append(Escape(mod)).Append("\")\n");
		foreach ((string name, string source) in modules)
		{
			text.Append("__scope.modules[\"").Append(Escape(name)).Append("\"] = function(...)\n");
			text.Append(source);
			text.Append("\nend\n");
		}
	}

	/// <summary>
	/// One module's text as the composer registers it, standing on its own for a compile check: the
	/// scope line and the function the module becomes.
	/// </summary>
	public static string WrapModule(string mod, string name, string source)
	{
		var text = new StringBuilder();
		AppendModules(text, mod, new Dictionary<string, string> { [name] = source });
		return text.ToString();
	}

	private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

	/// <summary>
	/// One mod's text as the body of a protected function. A function, so a top-level
	/// <c>return</c> or a <c>local</c> in the mod's file stays the mod's own.
	/// </summary>
	private static void AppendWrapped(StringBuilder text, string mod, string source)
	{
		text.Append("do\n");
		AppendRequire(text, mod);
		AppendProtected(text, mod, source);
		text.Append("end\n");
	}

	/// <summary>The protected call alone, inside a block that already holds the mod's <c>require</c>.</summary>
	private static void AppendProtected(StringBuilder text, string mod, string source)
	{
		string label = Escape(mod);
		text.Append("local ok, err = pcall(function()\n");
		text.Append(source);
		text.Append("\nend) if not ok then if Refii and Refii._report then Refii._report(\"")
			.Append(label)
			.Append("\", err) else RefiiModErrors = (RefiiModErrors or \"\") .. \"")
			.Append(label)
			.Append(": \" .. tostring(err) .. \"\\n\" end end\n");
	}
}
