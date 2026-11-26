using System;
using System.Text;
using Godot;
using Refii.Bnk;

namespace Refii.Titles.Fable2;

using Refii.Host;

/// <summary>
/// Refii's own Lua for Fable 3-style co-op, run in the GAME VM after the shipped scripts and before
/// any mod's (<c>refii/core_init.lua</c>, the payload's LoadStartupScripts hook). Every session
/// builds a fresh VM, so it runs once per session.
/// </summary>
/// <remarks>
/// <para>Like a mod's Lua it is OUR code patching what the shipped scripts defined, never an edited
/// copy of a shipped script.</para>
/// <para>THE HERO, PER COROUTINE. The shipped scripts take "the hero" from <c>GetPlayerHero()</c>
/// (1,281 calls in 166 files), which is always player one. A usable object's script runs in its
/// own coroutine — it yields while the user's animation plays — and the native use routine now
/// hands it the real user (coop_hero_hooks.cpp). Inside a coroutine a script started for player
/// two, <c>GetPlayerHero()</c> answers with player two; everywhere else it is unchanged. A
/// wrapper cannot use <c>pcall</c>: Lua 5.1 cannot yield across it. A coroutine that dies
/// mid-script leaves only a weak key behind.</para>
/// <para>SAVE-SAFE: a save keeps every running action-use and quest thread with its stack, and a
/// frame's function is found again BY NAME on load at its old pc. So only the frozen trampolines
/// <c>C.Call1</c> / <c>C.Wait1</c> are ever a frame under a yield; everything else tail-calls into
/// yielding code, permanent names are never retired, and the per-coroutine scope
/// (<c>C.CoState</c>) is saved with the threads the save holds. CoopLuaSaveSafeTests checks the
/// compiled bytecode.</para>
/// <para>CHESTS: <c>OnActionUseTakeItems:Update</c> gives the chest to the hero whenever player two
/// is alive ("entity_receiving_items = hero"); run as the user, it gives it to whoever opened
/// it.</para>
/// </remarks>
public static class CoopHeroLua
{
	public const string Key = "refii/core_init.lua";

	private const string Source = """
		-- Refii core: co-op heroes. Patches the shipped scripts' tables; nothing replaces a file.
		RefiiCoop = RefiiCoop or {}
		local C = RefiiCoop

		-- SAVED COROUTINES CARRY OUR FUNCTIONS. A save persists every running quest and script
		-- thread with Pluto, and a thread parked under one of our wrappers holds that wrapper on its
		-- stack. Only permanents are written by name (_G, the class tables, the shipped functions:
		-- saveloadsystem.lua, postscriptsloaded.lua); anything else is written by value, upvalues
		-- and all — RefiiCoop and the C natives it holds. So every function of ours that can sit
		-- under a yield is registered as a permanent under a fixed name, through the game's own
		-- AddToPermanentsTables, which also maps the name back when a save is loaded.
		function C.Permanent(fn, name)
			if AddToPermanentsTables ~= nil then
				AddToPermanentsTables(fn, "Refii." .. name)
			end
			return fn
		end

		-- THE FROZEN TRAMPOLINES. A saved thread keeps each frame's pc and finds the frame's function
		-- again BY NAME on load (TU1 pluto_unpersistthread 0x82BFBA60: savedpc = the code of whatever
		-- the name holds now + the saved pc; pluto_unpersist 0x82BFBF90 looks a permanent up in the
		-- load table and gets nil for a name nobody registered, so that frame loads as garbage). The
		-- only functions of ours that may be a frame under a yield are these two, and they are
		-- FROZEN: never edit their text (CoopLuaSaveSafeTests pins their bytecode). A different
		-- trampoline is a new name; these stay registered forever.
		--   C.Call1(after, state, fn, ...)  after(state, fn(...))
		--   C.Wait1(poll, state, yield)     yield until poll(state) answers done, then its value
		-- Every other function reaches yielding code only by a TAIL call, its own frame gone first:
		-- work after the call is a permanent `after(state, ...)` run through Call1, a wait is Wait1
		-- with a permanent `poll(state)`, a loop is a tail-recursive step through Call1, and `state`
		-- is plain data, entities or permanents, never a closure. Every function that can be held on
		-- a stack as a value (an after, a poll, an fn, a resolver, a wrapper a coroutine was created
		-- over) is a permanent whose NAME IS NEVER RETIRED: a removed one keeps a compatible stub.
		if C.Call1 == nil then
			C.Call1 = C.Permanent(function(after, state, fn, ...) return after(state, fn(...)) end, "Call1")
		end
		if C.Wait1 == nil then
			C.Wait1 = C.Permanent(function(poll, state, yield) while true do local done, value = poll(state) if done then return value end yield() end end, "Wait1")
		end

		-- PER-COROUTINE SCOPE: one record per coroutine (weak keys: a dead coroutine takes its record
		-- with it). Its fields, each set around a call and put back after it (C.WithFlag, C.RunScoped):
		--   acting      the acting entry (C.Entry)         hidden      GetPlayerHenchman() answers nil
		--   job         a job sign's thread                dog         GetDog() is player two's dog
		--   sleep_user  who sleeps (SleepFunction.Sleep)   presenting  the joiner's own bed scene
		--   silent      the host's silent bed thread       despawn     either hero's view counts
		--   hud_both    a HUD suggestion leaves both heroes
		-- SAVED WITH THE GAME for every thread the save holds (below): a thread loaded mid-scope
		-- resumes inside it. An outer scope's value waits in the Call1 frame that puts it back, which
		-- the save carries too, so the innermost value is all a record keeps.
		C.CoState = C.CoState or setmetatable({}, { __mode = "k" })

		function C.CoGet(co, field)
			if co == nil then
				return nil
			end
			local record = C.CoState[co]
			if record == nil then
				return nil
			end
			return record[field]
		end

		function C.CoSet(co, field, value)
			if co == nil then
				return
			end
			local record = C.CoState[co]
			if record == nil then
				if value == nil then
					return
				end
				record = {}
				C.CoState[co] = record
			end
			record[field] = value
		end

		-- Runs fn with this coroutine's `field` set to `value`, the value before it put back when fn
		-- returns (C.FlagLeave). Outside a coroutine nothing yields and nothing is set.
		function C.WithFlag(field, value, fn, ...)
			local co = coroutine.running()
			if co == nil then
				return fn(...)
			end
			local before = C.CoGet(co, field)
			C.CoSet(co, field, value)
			return C.Call1(C.FlagLeave, { field = field, before = before }, fn, ...)
		end
		C.Permanent(C.WithFlag, "WithFlag")
		C.FlagLeave = C.Permanent(function(state, ...)
			C.CoSet(coroutine.running(), state.field, state.before)
			return ...
		end, "FlagLeave")

		-- THE SAVE CARRIES THE SCOPES of the threads it holds. The engine builds and restores a save's
		-- Lua through the globals GetSaveTable / LoadScriptSave, looked up by name each time (TU1
		-- 0x8245C7B8 / 0x8245CC90; Engine_ModLua wraps them the same way). Every thread the shipped
		-- save table reaches — action-use scripts, quest threads, the behaviours AIMan keeps — goes
		-- out with its record as RefiiCoop = { Version = 1, Threads = { [thread] = record } } and
		-- comes back into C.CoState. The walk is over tables only and stops at permanents (written by
		-- name: nothing of theirs is saved), so it never reaches C.CoState, _G or the host's threads.
		-- A record keeps only what Pluto writes without dragging our code along: data, entities,
		-- permanents and resolver records over permanents.
		function C.SavedThreads(root)
			local found, seen, stack = {}, {}, { root }
			local permanents = PlutoPermanentsSaveTable
			seen[C] = true
			seen[C.CoState] = true
			while #stack > 0 do
				local t = stack[#stack]
				stack[#stack] = nil
				if not seen[t] and not (permanents ~= nil and permanents[t] ~= nil) then
					seen[t] = true
					for k, v in pairs(t) do
						if type(k) == "thread" then
							found[k] = true
						elseif type(k) == "table" then
							stack[#stack + 1] = k
						end
						if type(v) == "thread" then
							found[v] = true
						elseif type(v) == "table" then
							stack[#stack + 1] = v
						end
					end
				end
			end
			return found
		end

		function C.Saveable(value)
			local kind = type(value)
			if kind == "function" then
				return PlutoPermanentsSaveTable ~= nil and PlutoPermanentsSaveTable[value] ~= nil
			elseif kind == "table" and rawget(value, "RefiiResolve") ~= nil then
				return C.Saveable(rawget(value, "RefiiResolve"))
			end
			return kind ~= "thread"
		end

		if GetSaveTable ~= nil and LoadScriptSave ~= nil and not C.SaveWrapped then
			C.SaveWrapped = true
			local get_save, load_save = GetSaveTable, LoadScriptSave
			GetSaveTable = function(...)
				local t = get_save(...)
				if type(t) ~= "table" then
					return t
				end
				-- A camera_override hold stays out of the save (the camera chunk's C.CameraSaveTable).
				if C.CameraSaveTable ~= nil then
					pcall(C.CameraSaveTable, t)
				end
				local threads = {}
				for co in pairs(C.SavedThreads(t)) do
					local record = nil
					for field, value in pairs(C.CoState[co] or {}) do
						if C.Saveable(value) then
							record = record or {}
							record[field] = value
						end
					end
					threads[co] = record
				end
				t.RefiiCoop = { Version = 1, Threads = threads }
				return t
			end
			LoadScriptSave = function(t, ...)
				local saved = type(t) == "table" and t.RefiiCoop or nil
				if type(saved) == "table" and saved.Version == 1 and type(saved.Threads) == "table" then
					for co, record in pairs(saved.Threads) do
						if type(co) == "thread" and type(record) == "table" then
							C.CoState[co] = record
						end
					end
				end
				return load_save(t, ...)
			end
		end

		-- The hero a script acts for, by the coroutine it runs in (its record's `acting`, above).
		-- Outside any coroutine (a behaviour's PrepareToRun, a plain
		-- call) it is C.MainActing: the main thread cannot yield, so it is set and restored
		-- exactly around the call, and a coroutine resumed inside that call is inside it too.
		-- Last comes C.AIActing, the hero the AI is running for (below).
		--
		-- AN ENTRY is the hero itself, C.PinOne (player one, stated: the AI's nearer hero does not
		-- override it), or a resolver record { RefiiResolve = <permanent function>, RefiiArg = v }
		-- asked on every read (a demon door's nearer hero, a cutscene's held hero). An entry is
		-- never a fresh closure: a saved coroutine parked under a wrapper holds the entry on its
		-- stack, and only permanents and plain data persist by reference-free value.
		-- C.Acting() resolves it to player two or nil (player one) — never player one itself.
		C.PinOne = C.PinOne or C.Permanent(function() return nil end, "PinOne")

		function C.Entry()
			local entry = C.CoGet(coroutine.running(), "acting")
			if entry == nil then
				entry = C.MainActing
			end
			if entry == nil then
				entry = C.AIActing
			end
			return entry
		end

		function C.Resolve(entry)
			if entry == nil then
				return nil
			end
			local hero = entry
			local kind = type(entry)
			if kind == "function" then
				hero = entry()
			elseif kind == "table" and rawget(entry, "RefiiResolve") ~= nil then
				hero = entry.RefiiResolve(entry.RefiiArg)
			end
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if hero ~= nil and two ~= nil and hero == two then
				return two
			end
			return nil
		end

		function C.Acting()
			return C.Resolve(C.Entry())
		end

		-- An acting scope that names player one: GetPlayerHero() is player one by statement, not
		-- by default (the opinion natives then skip their own nearer-hero choice).
		function C.IsPinnedOne()
			local entry = C.Entry()
			return entry ~= nil and C.Resolve(entry) == nil
		end

		function C.IsHero(entity)
			if entity == nil then
				return false
			end
			local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return (one ~= nil and entity == one) or (two ~= nil and entity == two)
		end

		-- The other hero: player one for player two, else player two (nil without a partner).
		function C.Other(hero)
			local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if two ~= nil and hero ~= nil and hero == two then
				return one
			end
			return two
		end

		if C.ShippedGetPlayerHero == nil and GetPlayerHero ~= nil then
			C.ShippedGetPlayerHero = GetPlayerHero
			GetPlayerHero = C.Permanent(function(...)
				local acting = C.Acting()
				if acting ~= nil then
					return acting
				end
				return C.ShippedGetPlayerHero(...)
			end, "GetPlayerHero")
		end

		-- Runs fn under an acting entry. On the main thread nothing can yield, so the call is
		-- protected and the value always restored (C.RunScopedMain). In a coroutine the wrapped
		-- scripts yield, so there is no pcall: the entry is the coroutine's `acting` and
		-- C.ScopeLeave puts the one before back when fn returns.
		function C.RunScoped(entry, fn, ...)
			local co = coroutine.running()
			if co == nil then
				return C.RunScopedMain(entry, fn, ...)
			end
			local before = C.CoGet(co, "acting")
			C.CoSet(co, "acting", entry)
			return C.Call1(C.ScopeLeave, before, fn, ...)
		end
		C.Permanent(C.RunScoped, "RunScoped")
		C.ScopeLeave = C.Permanent(function(before, ...)
			C.CoSet(coroutine.running(), "acting", before)
			return ...
		end, "ScopeLeave")

		function C.RunScopedMain(entry, fn, ...)
			local before = C.MainActing
			C.MainActing = entry
			local results = { pcall(fn, ...) }
			C.MainActing = before
			if not results[1] then
				error(results[2], 0)
			end
			return unpack(results, 2, table.maxn(results))
		end

		-- Runs fn as `entity` when it is either hero: GetPlayerHero() answers that hero and
		-- GetPlayerHenchman() the other — each the other's partner, as Fable 3's two heroes are.
		-- Player one is pinned (C.PinOne), so the AI's nearer hero does not replace them. Any other
		-- entity, or no partner in the game, runs fn as it stands.
		function C.RunAs(entity, fn, ...)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if entity == nil or two == nil then
				return fn(...)
			end
			if entity == two then
				return C.RunScoped(two, fn, ...)
			end
			if entity == C.ShippedGetPlayerHero() then
				return C.RunScoped(C.PinOne, fn, ...)
			end
			return fn(...)
		end
		C.Permanent(C.RunAs, "RunAs")

		-- Runs fn with the acting hero asked of `resolve(arg)` on every read: a hero, or nil for
		-- player one (pinned). `resolve` must be a permanent (see the entry note above).
		function C.RunResolved(resolve, arg, fn, ...)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if two == nil then
				return fn(...)
			end
			return C.RunScoped({ RefiiResolve = resolve, RefiiArg = arg }, fn, ...)
		end
		C.Permanent(C.RunResolved, "RunResolved")

		-- Player two while player two is nearer `entity` than player one, else nil (player one):
		-- the native Refii.player_for's answer (see C.AIHeroFor) where the payload has it.
		C.NearerPartner = C.Permanent(function(entity)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			if entity == nil or not entity:IsAlive() or partner == nil or not partner:IsAlive() then
				return nil
			end
			if Refii ~= nil and Refii.player_for ~= nil then
				return Refii.player_for(entity) == 2 and partner or nil
			end
			if hero == nil or not hero:IsAlive() then
				return partner
			end
			return GetDistanceBetweenEntities(partner, entity) < GetDistanceBetweenEntities(hero, entity) and partner or nil
		end, "NearerPartner")

		-- Runs fn with the acting hero the one nearer `entity`, asked on every read (a quest thread).
		function C.RunNear(entity, fn, ...)
			if entity == nil then
				return fn(...)
			end
			return C.RunResolved(C.NearerPartner, entity, fn, ...)
		end
		C.Permanent(C.RunNear, "RunNear")

		-- A message's sender, or nil (a message is a native object; anything else answers nil).
		local function SenderOf(message)
			return message:GetEntitySentBy()
		end
		function C.SenderOf(message)
			if message == nil or type(message) == "boolean" or type(message) == "number" then
				return nil
			end
			local ok, sender = pcall(SenderOf, message)
			return ok and sender or nil
		end

		-- The sender of the newest message of `kind` sent to `entity` after `last` when it is a
		-- hero, else nil. A peek: nothing is consumed.
		function C.HeroSentTo(kind, entity, last)
			if entity == nil or MessageEvents == nil or kind == nil then
				return nil
			end
			local posted, message
			if last ~= nil then
				posted, message = MessageEvents.IsMessageSentTo(kind, entity, last)
			else
				posted, message = MessageEvents.IsMessageSentTo(kind, entity)
			end
			if not posted then
				return nil
			end
			local sender = C.SenderOf(message)
			return C.IsHero(sender) and sender or nil
		end

		-- Wraps Class.Method (a class by its global name; `class` overrides) once: the shipped
		-- function is kept under rawget(class, "RefiiShipped<tag><method>") so a re-run wraps
		-- the same function, an inherited method is looked up and set on the class itself, and
		-- the wrapper `make(shipped)` returns is a permanent named "<tag>.<name>.<method>".
		function C.Wrap(name, method, tag, make, class)
			local class_ = class or _G[name]
			if type(class_) ~= "table" then
				return
			end
			local key = "RefiiShipped" .. tag .. method
			local shipped = rawget(class_, key)
			if shipped == nil then
				shipped = class_[method]
				if type(shipped) ~= "function" then
					return
				end
				rawset(class_, key, shipped)
			end
			class_[method] = C.Permanent(make(shipped), tag .. "." .. name .. "." .. method)
		end

		-- A class's own methods, by name (CreateSubClass and New excluded).
		function C.OwnMethods(class)
			local names = {}
			if type(class) ~= "table" then
				return names
			end
			for key, value in pairs(class) do
				if type(key) == "string" and type(value) == "function" and key ~= "CreateSubClass" and key ~= "New"
					and key:sub(1, 5) ~= "Refii" then
					names[#names + 1] = key
				end
			end
			return names
		end

		-- Wraps methods of a class (all its own when `methods` is nil) to run as the hero
		-- `resolver(self, method, ...)` names (C.RunAs: either hero, else as it stands).
		function C.WrapAs(name, methods, tag, resolver)
			local class = _G[name]
			if type(class) ~= "table" then
				return
			end
			for _, method in ipairs(methods or C.OwnMethods(class)) do
				C.Wrap(name, method, tag, function(shipped)
					return function(self, ...)
						return C.RunAs(resolver(self, method, ...), shipped, self, ...)
					end
				end)
			end
		end

		-- Resolver for the hero a behaviour holds in `field` (permanent, one per field).
		C.FieldResolvers = C.FieldResolvers or {}
		function C.FieldHero(field)
			local resolver = C.FieldResolvers[field]
			if resolver == nil then
				resolver = C.Permanent(function(self)
					local hero = type(self) == "table" and self[field] or nil
					return C.IsHero(hero) and hero or nil
				end, "Field." .. field)
				C.FieldResolvers[field] = resolver
			end
			return resolver
		end

		-- A behaviour that keeps its hero in `field`: PrepareToRun runs as `latch(self, ...)`'s
		-- hero (nil: as it stands) and stores it when `store` is set, and every other listed
		-- method runs as the hero the field holds, read on every ask (it may change mid-run).
		function C.LatchAs(name, methods, field, latch, store)
			-- Registered now, not on first use: a save being loaded maps it by name.
			local resolver = C.FieldHero(field)
			for _, method in ipairs(methods) do
				C.Wrap(name, method, "Latch", function(shipped)
					if method == "PrepareToRun" then
						return function(self, ...)
							local hero = latch(self, ...)
							if store and self ~= nil then
								self[field] = hero
							end
							return C.RunAs(hero, shipped, self, ...)
						end
					end
					return function(self, ...)
						local hero = self ~= nil and self[field] or nil
						if not C.IsHero(hero) then
							return shipped(self, ...)
						end
						return C.RunResolved(resolver, self, shipped, self, ...)
					end
				end)
			end
		end

		-- Wraps Class.Method (a class by its global name) so it runs as the entity the script's
		-- own `field` names. The wrapper is a permanent named after the method.
		function C.RunMethodAsUser(name, method, field, class)
			local table_ = class or _G[name]
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShipped" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				return C.RunAs(self and self[field], shipped, self, ...)
			end, "User." .. name .. "." .. method)
		end

		C.RunMethodAsUser("OnActionUseTakeItems", "Update", "EntityUsingItem")

		-- RINGS AND GIFTS: OnCarriedActionUseRing:Update takes the target and the giver from
		-- GetPlayerHero(); run as the user, player two gives their own gift to their own target.
		C.RunMethodAsUser("OnCarriedActionUseRing", "Update", "EntityUsingItem")

		-- A GIFT'S ANSWER IS FOR ITS GIVER. BehaviourReceiveGift takes "the hero" from
		-- GetPlayerHero() throughout: who to face and answer, whose inventory the gift leaves
		-- (RemoveGiftFromHero), who a ring's AgreeToMarryHero goes to. The gift is a message
		-- (GetGift: MessageEvents' gift event) whose sender is the giver; PrepareToRun reads it, and
		-- the behaviour runs as them from then on (self.RefiiGiver, kept with the behaviour).
		function C.GiftGiver(behaviour)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if behaviour == nil or partner == nil or type(behaviour.GetGift) ~= "function" then
				return nil
			end
			-- GetGift answers (given, message): the first is a boolean, never the message. Indexing
			-- it threw inside AIManager:Update's best-behaviour pass and stopped every brain.
			local given, message = behaviour:GetGift()
			if not given or message == nil then
				return nil
			end
			local from = C.SenderOf(message)
			return C.IsHero(from) and from or nil
		end
		if type(BehaviourReceiveGift) == "table" and not rawget(BehaviourReceiveGift, "RefiiGiverWrapped") then
			rawset(BehaviourReceiveGift, "RefiiGiverWrapped", true)
			local methods = {}
			for key, value in pairs(BehaviourReceiveGift) do
				if type(value) == "function" and key ~= "CreateSubClass" and key ~= "GetGift" then
					methods[key] = value
				end
			end
			for key, shipped in pairs(methods) do
				BehaviourReceiveGift[key] = C.Permanent(function(self, ...)
					if key == "PrepareToRun" and self ~= nil then
						self.RefiiGiver = C.GiftGiver(self)
					end
					return C.RunAs(self and self.RefiiGiver, shipped, self, ...)
				end, "Gift.BehaviourReceiveGift." .. key)
			end
		end

		-- QUEST GIVERS: either player. The shipped prompts show a quest's box when
		-- `ScriptFunction.GetPlayersTarget() == giver and Targeted.IsHeroWithinInteractionDistance(giver)`,
		-- both player one only (QuestManager.HeroEntity). The game's own two-player rule is
		-- QuestEntityThreadBase:ShowAcceptanceBoxWithHelp's: player one targeting the giver from
		-- interaction range, or player two (alive) doing the same. GetPlayersTarget answers with the
		-- target of the player who is engaging — player one's when they are in range of it, else
		-- player two's — and the distance test accepts player two targeting the giver from range.
		local function HenchmanTargeting(entity)
			local henchman = GetPlayerHenchman and GetPlayerHenchman()
			if henchman == nil or not henchman:IsAlive() then
				return false
			end
			local target = Targeting.GetTarget(henchman)
			if target == nil or (entity ~= nil and target ~= entity) then
				return false
			end
			return Targeted.IsHenchmanWithinInteractionDistance(target) == true
		end

		if Targeted ~= nil and Targeted.IsHeroWithinInteractionDistance ~= C.IsHeroWithinInteractionDistance then
			local shipped = Targeted.IsHeroWithinInteractionDistance
			C.ShippedIsHeroWithinInteractionDistance = shipped
			C.IsHeroWithinInteractionDistance = C.Permanent(function(entity, ...)
				if shipped(entity, ...) then
					return true
				end
				return HenchmanTargeting(entity)
			end, "IsHeroWithinInteractionDistance")
			Targeted.IsHeroWithinInteractionDistance = C.IsHeroWithinInteractionDistance
		end

		if ScriptFunction ~= nil and ScriptFunction.GetPlayersTarget ~= C.GetPlayersTarget then
			local shipped = ScriptFunction.GetPlayersTarget
			C.GetPlayersTarget = C.Permanent(function(...)
				local target = shipped(...)
				if target ~= nil and target:IsAlive() and C.ShippedIsHeroWithinInteractionDistance ~= nil
					and C.ShippedIsHeroWithinInteractionDistance(target) then
					return target
				end
				local henchman = GetPlayerHenchman and GetPlayerHenchman()
				if HenchmanTargeting(nil) then
					return Targeting.GetTarget(henchman)
				end
				return target
			end, "GetPlayersTarget")
			ScriptFunction.GetPlayersTarget = C.GetPlayersTarget
		end

		-- SHOPS RUN BY A BEHAVIOUR (the barman, the town crier): their Update says
		-- TEXT_AI_HENCHMAN_REJECT when `self.InteractEntity == GetPlayerHenchman()`, then opens the
		-- shop for the LOCAL player (GUI.DisplayShopScreen, the same buyer rule as the native shop).
		-- So player two is served exactly where player two is this machine's local hero.
		-- GetPlayerHenchman() is the other hero of the acting one; hidden (nil) inside a coroutine
		-- whose record says `hidden` or a main-thread call under C.HideHenchman.
		C.HiddenDepth = C.HiddenDepth or 0
		if C.ShippedGetPlayerHenchman == nil and GetPlayerHenchman ~= nil then
			C.ShippedGetPlayerHenchman = GetPlayerHenchman
			GetPlayerHenchman = C.Permanent(function(...)
				if C.CoGet(coroutine.running(), "hidden") or C.HiddenDepth > 0 then
					return nil
				end
				if C.Acting() ~= nil then
					return C.ShippedGetPlayerHero()
				end
				return C.ShippedGetPlayerHenchman(...)
			end, "GetPlayerHenchman")
		end

		-- Runs fn (which never yields) with GetPlayerHenchman() answering nil.
		function C.HideHenchman(fn, ...)
			C.HiddenDepth = C.HiddenDepth + 1
			local results = { pcall(fn, ...) }
			C.HiddenDepth = C.HiddenDepth - 1
			if not results[1] then
				error(results[2], 0)
			end
			return unpack(results, 2, table.maxn(results))
		end

		-- QuestManager.HeroEntity, "the hero" in 8,660 places, is a plain field (set at
		-- gameflow.lua:2027 and QuestManager:LoadFromSave), so the per-coroutine GetPlayerHero()
		-- never reached it. It is served per acting player: the stored value moves to
		-- RefiiHeroEntity, and reading HeroEntity answers the acting player first. QuestManager is
		-- reached only through _G, a Pluto permanent (ctypesserialisefunctions.lua), and nothing
		-- stores the table itself, so the metatable never enters a save; the save table copies
		-- the field (QuestManager.GetSaveTable), which outside any acting player is player one.
		if QuestManager ~= nil and getmetatable(QuestManager) == nil and not rawget(QuestManager, "RefiiHeroServed") then
			rawset(QuestManager, "RefiiHeroEntity", rawget(QuestManager, "HeroEntity"))
			rawset(QuestManager, "HeroEntity", nil)
			rawset(QuestManager, "RefiiHeroServed", true)
			setmetatable(QuestManager, {
				__index = function(t, key)
					if key == "HeroEntity" then
						local acting = C.Acting()
						if acting ~= nil then
							return acting
						end
						return rawget(t, "RefiiHeroEntity")
					end
					return nil
				end,
				__newindex = function(t, key, value)
					if key == "HeroEntity" then
						rawset(t, "RefiiHeroEntity", value)
					else
						rawset(t, key, value)
					end
				end,
			})
		end

		local function PlayerTwoIsLocal(entity)
			if entity == nil or Refii == nil or Refii.handle == nil or Refii.local_hero == nil then
				return false
			end
			local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return henchman ~= nil and entity == henchman and Refii.handle(entity) == Refii.local_hero()
		end

		local function ServePlayerTwo(name, method)
			local table_ = _G[name]
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShipped" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				local co = coroutine.running()
				local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
				if co == nil or not PlayerTwoIsLocal(self and self.InteractEntity) then
					-- The shipped pair (player one the hero, player two the henchman), whichever
					-- hero the AI is running for: a remote player two is turned away as shipped.
					return C.RunAs(one, shipped, self, ...)
				end
				return C.WithFlag("hidden", true, C.RunAs, self.InteractEntity, shipped, self, ...)
			end, "Serve." .. name .. "." .. method)
		end

		ServePlayerTwo("BehaviourBarmanOpenShopUI", "Update")
		ServePlayerTwo("BehaviourTownCrierOpenGUI", "Update")

		-- Their PrepareToRun's ghost test names the henchman of the shipped pair too. The crier's
		-- customer is kept by the crier: a title it then announces is the customer's (below).
		C.CrierCustomer = C.CrierCustomer or {}
		C.Wrap("BehaviourBarmanOpenShopUI", "PrepareToRun", "Serve", function(shipped)
			return function(self, ...)
				return C.RunAs(C.ShippedGetPlayerHero(), shipped, self, ...)
			end
		end)
		C.Wrap("BehaviourTownCrierOpenGUI", "PrepareToRun", "Serve", function(shipped)
			return function(self, ...)
				return C.Call1(C.CrierAfter, self, C.RunAs, C.ShippedGetPlayerHero(), shipped, self, ...)
			end
		end)
		C.CrierAfter = C.Permanent(function(self, ...)
			if (...) and self ~= nil and self.Entity ~= nil and C.IsHero(self.InteractEntity) and Refii ~= nil then
				C.CrierCustomer[Refii.handle(self.Entity)] = self.InteractEntity
			end
			return ...
		end, "CrierAfter")

		-- BED CONSENT (Fable 3): sleeping moves the clock for both players and sex fades both
		-- screens, so the other player is asked first, on their own screen (GameUiCoopConsent).
		-- The script waits for the answer; a refusal (or no answer) is the bed not used.
		-- C.ConsentBegin asks and never waits: true and the answer when there is nobody to ask, else
		-- false and the ticket C.ConsentPoll waits on (through C.Wait1). A ticket is this session's
		-- own: after a load it is unknown, and an unknown ticket is a refusal, never an endless wait.
		C.ConsentOpen = C.ConsentOpen or {}
		function C.ConsentBegin(user, kind, detail)
			local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if henchman == nil or not henchman:IsAlive() or Refii == nil or Refii._consent_ask == nil then
				return true, true
			end
			local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero() or nil
			local asker = (user ~= nil and user == henchman) and 2 or 1
			if user ~= nil and user ~= henchman and user ~= hero then
				return true, true
			end
			local ticket = { id = Refii._consent_ask(kind, asker, detail or 0) }
			C.ConsentOpen[ticket.id] = ticket
			return false, ticket
		end
		C.ConsentPoll = C.Permanent(function(ticket)
			if type(ticket) ~= "table" or ticket.id == nil or C.ConsentOpen[ticket.id] ~= ticket then
				return true, false
			end
			local answer = Refii._consent_answer(ticket.id)
			if answer ~= nil and answer >= 0 then
				C.ConsentOpen[ticket.id] = nil
				return true, answer == 1
			end
			return false
		end, "ConsentPoll")

		-- Sleep: the type the sleep menu chose is read right after it closes, before the fade
		-- (SleepFunction.Sleep); the partner is asked there, and a refusal reads as no sleep.
		if SleepFunction ~= nil and SleepFunction.Sleep ~= nil and SleepFunction.Sleep ~= C.Sleep then
			local shipped = SleepFunction.Sleep
			C.Sleep = C.Permanent(function(entity, bed, ...)
				return C.WithFlag("sleep_user", entity or false, shipped, entity, bed, ...)
			end, "SleepFunction.Sleep")
			SleepFunction.Sleep = C.Sleep
		end
		if Bed ~= nil and Bed.GetSleepType ~= nil and Bed.GetSleepType ~= C.GetSleepType then
			local shipped = Bed.GetSleepType
			C.GetSleepType = C.Permanent(function(bed, ...)
				local sleep_type = shipped(bed, ...)
				local co = coroutine.running()
				local user = C.CoGet(co, "sleep_user")
				if user ~= nil and sleep_type ~= nil and ESleepType ~= nil and sleep_type ~= ESleepType.SLEEP_NONE then
					C.CoSet(co, "sleep_user", nil)
					local done, answer = C.ConsentBegin(user or nil, 1, sleep_type)
					if done then
						return C.SleepTypeAfter({ bed = bed, sleep_type = sleep_type }, answer)
					end
					return C.Call1(C.SleepTypeAfter, { bed = bed, sleep_type = sleep_type }, C.Wait1, C.ConsentPoll, answer,
						coroutine.yield)
				end
				return sleep_type
			end, "Bed.GetSleepType")
			Bed.GetSleepType = C.GetSleepType
		end
		C.SleepTypeAfter = C.Permanent(function(state, agreed)
			if agreed then
				return state.sleep_type
			end
			Bed.SetSleepType(state.bed, ESleepType.SLEEP_NONE)
			return ESleepType.SLEEP_NONE
		end, "SleepTypeAfter")

		-- CRIME IS THE CRIMINAL'S. The crime manager records player two's crimes as player two's
		-- (coop_hero_hooks.cpp RefiiCoopCrimeIsTheirs; shipped AddCrime made every player criminal
		-- hero one), and VillageCrimeManager.IsResistingArrest / IsCriminal answer for the hero
		-- they are given (`sub_827CCED8` compares each crime's criminal with its argument). The
		-- group minds that ask about "the hero" are restated for both heroes (GUARDS AND VILLAGES,
		-- below); the native itself is left as shipped.
		if VillageCrimeManager ~= nil and VillageCrimeManager.IsResistingArrest ~= nil then
			if rawget(VillageCrimeManager, "RefiiShippedIsResistingArrest") == nil then
				rawset(VillageCrimeManager, "RefiiShippedIsResistingArrest",
					C.ShippedIsResistingArrest or VillageCrimeManager.IsResistingArrest)
			end
			C.ShippedIsResistingArrest = rawget(VillageCrimeManager, "RefiiShippedIsResistingArrest")
			C.IsResistingArrest = C.ShippedIsResistingArrest
			VillageCrimeManager.IsResistingArrest = C.ShippedIsResistingArrest
		end

		-- ONE-TIME COLLECTABLES PER PLAYER (Fable 3): a silver key one player has taken stays for the
		-- other (coop_world_hooks.cpp keeps it). The player who has it cannot take it again;
		-- with no partner in the game, a key its only player already has is simply cleared away.
		if OnActionUsePickUp ~= nil and OnActionUsePickUp.Update ~= nil and OnActionUsePickUp.Update ~= C.PickUp then
			local shipped = OnActionUsePickUp.Update
			C.PickUp = C.Permanent(function(self, ...)
				local item = self and self.ItemBeingUsed
				local user = self and self.EntityUsingItem
				if item ~= nil and user ~= nil and item:IsAlive() and Refii ~= nil and Refii._key_taken ~= nil then
					local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
					local player = (henchman ~= nil and user == henchman) and 2 or 1
					if Refii._key_taken(Refii.handle(item), player) == 1 then
						if henchman == nil or not henchman:IsAlive() then
							item:Destroy()
						end
						return
					end
				end
				return shipped(self, ...)
			end, "OnActionUsePickUp.Update")
			OnActionUsePickUp.Update = C.PickUp
		end

		-- SILVER-KEY CHESTS PER PLAYER (Fable 3's one-time chests: each hero opens one for themselves).
		-- A chest's loot lives in its own inventory and the shipped script moves it to the opener;
		-- an empty chest stops being targetable (the chest's tick, `sub_82295D60`). With a partner
		-- who has not opened it, the chest is closed again after the first opening
		-- (Chest.SnapToClosed, which the shipped script itself uses), its counted byte restored
		-- (Refii._chest_counted) and its loot and gold put back, so the partner opens it through the
		-- same script. Who has opened which chest is a SAVED player value (refii.chest.<UniqueID>),
		-- so it goes home with each hero; a player cannot open a chest twice.
		if OnActionUseTakeItems ~= nil and OnActionUseTakeItems.Update ~= nil and OnActionUseTakeItems.Update ~= C.TakeItems then
			local shipped = OnActionUseTakeItems.Update
			C.TakeItems = C.Permanent(function(self, ...)
				local chest = self and self.ItemBeingUsed
				local user = self and self.EntityUsingItem
				if chest == nil or user == nil or not chest:IsAlive() or not Chest.IsAvailable(chest) or Refii == nil
					or Refii.uid == nil or Chest.GetNumberOfSilverKeysNeeded(chest) <= 0 then
					return shipped(self, ...)
				end
				local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
				local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				local player = (two ~= nil and user == two) and 2 or 1
				local partner = player == 2 and one or two
				local handle = Refii.handle(chest)
				local key = "refii.chest." .. tostring(Refii.uid(handle))
				if Refii.player_value(player, key, 0) ~= 0 then
					return
				end
				local loot = Refii.inventory(handle) or {}
				local money = Money.IsAvailable(chest) and Money.Get(chest) or 0
				return C.Call1(C.TakeItemsAfter, { chest = chest, player = player, partner = partner, handle = handle, key = key,
					loot = loot, money = money, other = player == 1 and 2 or 1 }, shipped, self, ...)
			end, "OnActionUseTakeItems.Update")
			OnActionUseTakeItems.Update = C.TakeItems
		end
		-- After the chest's own script: recorded as opened by its player, and closed and refilled for
		-- the partner who has not opened it (the handle asked again: a loaded save's handles are new).
		C.TakeItemsAfter = C.Permanent(function(state, ...)
			local chest = state.chest
			local handle = chest:IsAlive() and Refii.handle(chest) or state.handle
			local after = chest:IsAlive() and Refii.inventory(handle) or {}
			local opened = Chest.IsOpen(chest) or (#state.loot > 0 and #after == 0)
			if not opened then
				return ...
			end
			Refii.player_save(state.player, state.key, 1)
			local partner = state.partner
			if partner ~= nil and partner:IsAlive() and Refii.player_value(state.other, state.key, 0) == 0 then
				Chest.SnapToClosed(chest)
				Refii._chest_counted(handle)
				for _, stack in ipairs(state.loot) do
					for i = 1, (stack.count or 1) do
						Refii.add_item(handle, stack.definition)
					end
				end
				if state.money > 0 and Money.IsAvailable(chest) then
					Money.Set(chest, state.money)
				end
			end
			return ...
		end, "TakeItemsAfter")

		-- A SILVER-KEY CHEST OPENED BEFORE THE PARTNER WAS THERE: open, emptied, and no longer
		-- targetable (the chest's tick `sub_82295D60` keys targeting on the inventory), so the wrapper
		-- above never sees player two use it. Its loot is its definition's InitialItems, put in once at
		-- creation (`sub_824EEF48` → `sub_824EFFC8`, Inventory.AddInitialItems) and saved empty after,
		-- and its gold the definition's "Money". Near player two, such a chest they have not opened is
		-- given back to them: player one is recorded as having opened it, it is closed again, its
		-- counted byte kept, and refilled the game's own way — a randomly generated entry rolls again,
		-- as it would for a fresh chest. The world is the host's, so the host does it.
		function C.RestoreChestsNear(hero)
			local x, y, z = Refii.position(Refii.handle(hero))
			if x == nil or Inventory == nil or Inventory.AddInitialItems == nil then
				return
			end
			for _, handle in ipairs(Refii.near(x, y, z, 20, false) or {}) do
				local chest = Refii.entity(handle)
				if chest ~= nil and chest:IsAlive() and Chest.IsAvailable(chest) and Chest.IsOpen(chest)
					and Chest.GetNumberOfSilverKeysNeeded(chest) > 0 then
					local key = "refii.chest." .. tostring(Refii.uid(handle))
					local items = Refii.inventory(handle)
					if items ~= nil and #items == 0 and Refii.player_value(2, key, 0) == 0 then
						if Refii.player_value(1, key, 0) == 0 then
							Refii.player_save(1, key, 1)
						end
						Chest.SnapToClosed(chest)
						Refii._chest_counted(handle)
						Inventory.AddInitialItems(chest)
						Refii._chest_money(handle)
					end
				end
			end
		end

		function C.ChestWatch()
			C.ChestTicks = (C.ChestTicks or 0) + 1
			if C.ChestTicks % 90 ~= 0 or Refii == nil or Refii.coop_mode == nil or Refii._chest_money == nil then
				return
			end
			local mode = Refii.coop_mode()
			if mode ~= 1 and mode ~= 2 then
				return
			end
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if two ~= nil and two:IsAlive() then
				C.RestoreChestsNear(two)
			end
		end

		-- KNOCKED DOWN, PLAYER TWO TAKES THE HERO'S PENALTY (Fable 3: one death tracker per hero).
		-- Shipped, Henchman_DeathTracker (gameflow.lua) revives player two with
		-- MESSAGE_EVENT_REVIVING_HENCHMAN and no scars, where Hero_DeathTracker gives hero one a
		-- scar (+1 with the scar augment, -1 with stoneskin). Player two is a hero now: the same
		-- scars land when their tracker revives them. The trackers are running coroutines, so the
		-- revive message is where the rule goes in.
		if MessageEvents ~= nil and MessageEvents.PostMessage ~= nil and MessageEvents.PostMessage ~= C.PostMessage then
			local shipped = MessageEvents.PostMessage
			C.PostMessage = C.Permanent(function(message, ...)
				return C.Call1(C.ReviveScars, message, shipped, message, ...)
			end, "MessageEvents.PostMessage")
			MessageEvents.PostMessage = C.PostMessage
		end
		C.ReviveScars = C.Permanent(function(message, ...)
			if type(message) == "table" and EMessageEventType ~= nil and
				message.type == EMessageEventType.MESSAGE_EVENT_REVIVING_HENCHMAN then
				local two = message.to
				local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if two ~= nil and two == henchman and two:IsAlive() and Morph ~= nil and Morph.AddRandomScar ~= nil then
					local scars = 1
					if Creature.IsAugmentedWithType(two, EAugmentationType.AUGMENTATION_SCAR) then
						scars = scars + 1
					end
					if Creature.IsAugmentedWithType(two, EAugmentationType.AUGMENTATION_STONESKIN) then
						scars = scars - 1
					end
					for i = 1, scars do
						Morph.AddRandomScar(two)
					end
				end
			end
			return ...
		end, "ReviveScars")

		-- Sex: asked before it starts (OnActionUseBed:Sex); a refusal uses neither bed nor partner.
		if OnActionUseBed ~= nil and OnActionUseBed.Sex ~= nil and OnActionUseBed.Sex ~= C.BedSex then
			local shipped = OnActionUseBed.Sex
			C.ShippedBedSex = shipped
			C.BedSex = C.Permanent(function(self, ...)
				local done, answer = C.ConsentBegin(self and self.EntityUsingItem, 2, 0)
				if done then
					return C.BedSexAfter({ self = self, n = select("#", ...), ... }, answer)
				end
				return C.Call1(C.BedSexAfter, { self = self, n = select("#", ...), ... }, C.Wait1, C.ConsentPoll, answer, coroutine.yield)
			end, "OnActionUseBed.Sex")
			OnActionUseBed.Sex = C.BedSex
		end
		C.BedSexAfter = C.Permanent(function(state, agreed)
			if not agreed then
				return false, false
			end
			return C.ShippedBedSex(state.self, unpack(state, 1, state.n))
		end, "BedSexAfter")

		-- EACH VILLAGER'S OPINION OF EACH HERO (Fable 3 keeps a record per hero). The villagers'
		-- brains ask OpinionReaction.* with the villager alone and the game answers for hero one;
		-- each native now runs for the hero it concerns — a hero passed to it, else the one the
		-- villager notices (the nearer, the game's own GetEntityToNoticeExpressionsOf rule) — with
		-- that hero's opinion swapped in (mod_events.cpp OpinionHeroScope). Natives never yield.
		if OpinionReaction ~= nil and Refii ~= nil and Refii._opinion_enter ~= nil and not C.OpinionWrapped then
			C.OpinionWrapped = true
			local skip = { GetECType = true, IsAvailable = true, IsValidAxis = true }
			local function finish(ok, ...)
				Refii._opinion_leave()
				if not ok then
					error((...), 0)
				end
				return ...
			end
			for name, shipped in pairs(OpinionReaction) do
				if type(shipped) == "function" and not skip[name] then
					OpinionReaction[name] = C.Permanent(function(villager, ...)
						local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
						if two == nil or villager == nil then
							return shipped(villager, ...)
						end
						local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
						-- A hero named in the arguments wins; else code acting for a hero asks about
						-- that hero — player two's record in their scope, player one's (the shipped
						-- record, no nearer-hero choice) when player one is pinned.
						local hero = nil
						for i = 1, select("#", ...) do
							local v = select(i, ...)
							if v ~= nil and v == two then
								hero = two
								break
							elseif v ~= nil and v == one then
								return shipped(villager, ...)
							end
						end
						if hero == nil then
							hero = C.Acting()
							if hero == nil and C.IsPinnedOne() then
								return shipped(villager, ...)
							end
						end
						local entered = Refii._opinion_enter(Refii.handle(villager), hero ~= nil and Refii.handle(hero) or nil)
						if entered ~= 1 then
							return shipped(villager, ...)
						end
						return finish(pcall(shipped, villager, ...))
					end, "Opinion." .. name)
				end
			end
		end

		-- DOGS, ONE PER PLAYER. Shipped, GetDog() is the one global "the dog" slot (hero one's);
		-- player two's dog is the co-op one (coop_dog_hooks.cpp), Refii.player_dog(2). Each dog
		-- serves Dog.GetMaster(dog), so a behaviour that names GetDog() or GetPlayerHero() where
		-- it means "this dog" or "this dog's master" is restated for the dog it runs on.
		function C.PartnerDog()
			local handle = Refii ~= nil and Refii.player_dog ~= nil and Refii.player_dog(2) or nil
			return handle ~= nil and Refii.entity(handle) or nil
		end

		-- BOTH DOGS ALWAYS THINK. AIManager:IsEntityToUpdate updates GetDog() every time and any
		-- other creature only inside its update zone, so player two's dog stopped following and
		-- answering once it left that zone (seen on the host: no TrackHero, its navigation target
		-- frozen at player two's old spot). Fable 3's names both heroes' dogs (GetLocalHeroDog,
		-- GetRemoteHeroDog); player two's dog is the second.
		if AIManager ~= nil and AIManager.IsEntityToUpdate ~= nil and AIManager.IsEntityToUpdate ~= C.IsEntityToUpdate then
			local shipped = AIManager.IsEntityToUpdate
			C.IsEntityToUpdate = C.Permanent(function(self, entity, ...)
				if entity ~= nil then
					local partner = C.PartnerDog()
					if partner ~= nil and entity == partner then
						return true
					end
				end
				return shipped(self, entity, ...)
			end, "AIManager.IsEntityToUpdate")
			AIManager.IsEntityToUpdate = C.IsEntityToUpdate
		end

		-- ITEMS USED ON "THE DOG" (treat, collar, breed potions and books, the dig's happy dog)
		-- act on the user's own dog. Only GetDog() is redirected: GetPlayerHero() is left alone.
		-- Otherwise "the dog" is the acting hero's: a villager reacting to the dog (aibase.lua
		-- LookWhileSittingOrCarrying(EOT_DOG), PerformReaction(..., GetDog())) while the AI runs
		-- for player two names player two's dog. C.DogPlayer (1 or 2) states it outright.
		C.DogSelf = C.DogSelf or 0
		if C.ShippedGetDog == nil and GetDog ~= nil then
			C.ShippedGetDog = GetDog
			GetDog = C.Permanent(function(...)
				if C.DogPlayer == 1 then
					return C.ShippedGetDog(...)
				end
				if C.CoGet(coroutine.running(), "dog") or C.DogSelf > 0 or C.DogPlayer == 2 then
					return C.PartnerDog()
				end
				if C.Acting() ~= nil then
					local dog = C.PartnerDog()
					if dog ~= nil then
						return dog
					end
				end
				return C.ShippedGetDog(...)
			end, "GetDog")
		end

		-- Runs fn (which never yields) with GetDog() answering `hero`'s own dog.
		function C.WithDogOf(hero, fn, ...)
			local before = C.DogPlayer
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			C.DogPlayer = (two ~= nil and hero == two) and 2 or 1
			local results = { pcall(fn, ...) }
			C.DogPlayer = before
			if not results[1] then
				error(results[2], 0)
			end
			return unpack(results, 2, table.maxn(results))
		end

		-- AN NPC DEALS WITH ITS OWN HERO. The shipped AI names GetPlayerHero() (590 places in
		-- scripts/ai) and QuestManager.HeroEntity (80) for "the hero" — one shared screen. With two
		-- heroes roaming apart every brain answered player one: a travelling trader near player
		-- two walked off to player one, patrols and wanders centred on player one. While the AI
		-- runs for an entity — choosing a behaviour, resuming its update and exit coroutines — the
		-- acting hero (C.Acting, so GetPlayerHero/GetPlayerHenchman/QuestManager.HeroEntity swap as
		-- a pair, as C.RunAs does) is the one nearer that entity, Fable 3's rule; a player's dog
		-- acts for its own master.
		--
		-- PLAYER TWO'S DOG IS "THE DOG" IN ITS OWN BRAIN. Shipped AI code names GetDog() where it
		-- means the dog it is running for: every BehaviourBase mover sets the dog's speed only
		-- `if self.Entity == GetDog()` (aibase.lua MoveToPosition..MoveToEntityTimeLimited), so
		-- player two's dog chose its dig-spot lead and pathed there at no speed, standing still;
		-- `BehaviourDogInjured` asks `Dog.IsInjured(GetDog())`. While the AI runs for that dog,
		-- GetDog() answers it.
		--
		-- These entry points never yield (they resume), so setting and restoring around them is
		-- exact; a coroutine with its own acting hero (C.RunAs, C.RunNear) keeps it. For an
		-- entity nearer player one the scope is C.PinOne, so the opinion natives answer the same
		-- hero the AI chose rather than making their own nearer-hero choice.
		--
		-- WHICH HERO AN ENTITY DEALS WITH is the native Refii.player_for (mod_api.cpp
		-- refii_mod_player_for): a hero itself, a player's dog its master, anything else the nearer
		-- living hero, kept per entity until the other hero is more than 3 m nearer (an NPC between
		-- the two does not flip heroes every frame); one hero standing, that hero. Player two, or
		-- nil for player one.
		function C.AIHeroFor(entity)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if entity == nil or partner == nil then
				return nil
			end
			if Refii ~= nil and Refii.player_for ~= nil then
				return Refii.player_for(entity) == 2 and partner or nil
			end
			-- A payload without the native: the nearer living hero, no hysteresis.
			if not partner:IsAlive() then
				return nil
			end
			if entity == partner then
				return partner
			end
			local dog = C.PartnerDog()
			if dog ~= nil and entity == dog then
				return partner
			end
			if C.ShippedGetDog ~= nil and entity == C.ShippedGetDog() then
				return nil
			end
			return C.NearerPartner(entity)
		end

		local function AsItsEntity(table_, method, entity_of)
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShippedItsAI" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				local entity = entity_of(self, ...)
				local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if entity == nil or partner == nil then
					return shipped(self, ...)
				end
				local own_dog = C.PartnerDog()
				local is_dog = own_dog ~= nil and entity == own_dog
				local acting = C.AIHeroFor(entity) or C.PinOne
				local before = C.AIActing
				C.AIActing = acting
				if is_dog then
					C.DogSelf = C.DogSelf + 1
				end
				local results = { pcall(shipped, self, ...) }
				if is_dog then
					C.DogSelf = C.DogSelf - 1
				end
				C.AIActing = before
				if not results[1] then
					error(results[2], 0)
				end
				return unpack(results, 2, table.maxn(results))
			end, "ItsAI." .. method)
		end

		-- EACH HERO THEIR OWN FIGHT. CombatRegister:Update (combatregister.lua) zeroes ONE
		-- MidstCount / CloseToCombat / InAnyCombat, adds every attacker of a player or a player's
		-- ally to it (AddToMidstCountScore, from each CombatInfo update) and sets the three on
		-- QuestManager.HeroEntity — player one — so a fight anywhere put player one in combat (the
		-- camera's alert, Player.GetMidstOfCombatCounter) and player two's own fights never set
		-- theirs. Each score now counts for the hero it is about: the target when it is a hero,
		-- else the hero nearer the attacker (a player's ally under attack), run as that hero for the
		-- ally test. The pass's three setters then give each hero their own; an online host states
		-- player two's in the player store (coop.combat.*), and the joiner, which plays player two,
		-- applies them to its hero each frame (C.CombatApply, from the camera's FrameUpdate).
		if CombatRegister ~= nil and Player ~= nil and type(CombatRegister.AddToMidstCountScore) == "function"
			and type(CombatRegister.UpdateFormations) == "function" then
			local function Fresh() return { midst = 0, close = false, any = false } end
			C.CombatAcc = C.CombatAcc or { Fresh(), Fresh() }
			C.CombatSent = C.CombatSent or {}
			-- The last published state per hero (Refii.combat_state).
			C.CombatLast = C.CombatLast or { Fresh(), Fresh() }

			if rawget(CombatRegister, "RefiiShippedUpdateFormations") == nil then
				rawset(CombatRegister, "RefiiShippedUpdateFormations", CombatRegister.UpdateFormations)
			end
			local shipped_formations = rawget(CombatRegister, "RefiiShippedUpdateFormations")
			CombatRegister.UpdateFormations = C.Permanent(function(self, ...)
				C.CombatAcc = { Fresh(), Fresh() }
				return C.Call1(C.FormationsAfter, nil, shipped_formations, self, ...)
			end, "CombatRegister.UpdateFormations")
			C.FormationsAfter = C.Permanent(function(_, ...)
				C.CombatPass = true
				return ...
			end, "FormationsAfter")

			if rawget(CombatRegister, "RefiiShippedAddToMidstCountScore") == nil then
				rawset(CombatRegister, "RefiiShippedAddToMidstCountScore", CombatRegister.AddToMidstCountScore)
			end
			local shipped_score = rawget(CombatRegister, "RefiiShippedAddToMidstCountScore")
			CombatRegister.AddToMidstCountScore = C.Permanent(function(self, info, ...)
				local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if partner == nil or not partner:IsAlive() or info == nil or info.Target == nil then
					return shipped_score(self, info, ...)
				end
				local player = 1
				if info.Target == partner then
					player = 2
				elseif info.Target ~= C.ShippedGetPlayerHero() and info.Combatant ~= nil
					and C.NearerPartner(info.Combatant) ~= nil then
					player = 2
				end
				local acc = C.CombatAcc[player]
				self.MidstCount, self.CloseToCombat, self.InAnyCombat = acc.midst, acc.close, acc.any
				C.RunAs(player == 2 and partner or C.ShippedGetPlayerHero(), shipped_score, self, info, ...)
				acc.midst, acc.close, acc.any = self.MidstCount, self.CloseToCombat, self.InAnyCombat
			end, "CombatRegister.AddToMidstCountScore")

			local function PerHero(name, field, last)
				if rawget(Player, "RefiiShipped" .. name) == nil then
					rawset(Player, "RefiiShipped" .. name, Player[name])
				end
				local shipped = rawget(Player, "RefiiShipped" .. name)
				Player[name] = C.Permanent(function(entity, value, ...)
					local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
					local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
					if not C.CombatPass or entity == nil or entity ~= one
						or partner == nil or not partner:IsAlive() or Refii == nil or Refii.coop_mode == nil then
						if entity ~= nil and entity == one then
							C.CombatLast[1][field] = value
						end
						return shipped(entity, value, ...)
					end
					if last then
						C.CombatPass = false
					end
					C.CombatLast[1][field] = C.CombatAcc[1][field]
					C.CombatLast[2][field] = C.CombatAcc[2][field]
					local mode = Refii.coop_mode()
					if mode == 3 then
						return
					end
					shipped(entity, C.CombatAcc[1][field], ...)
					local two = C.CombatAcc[2][field]
					if mode == 1 then
						return shipped(partner, two, ...)
					end
					local stated = type(two) == "boolean" and (two and 1 or 0) or two
					local key = "coop.combat." .. field
					if C.CombatSent[key] ~= stated then
						C.CombatSent[key] = stated
						Refii.player_set(2, key, stated)
					end
				end, "Combat." .. name)
			end
			PerHero("SetMidstOfCombatCounter", "midst", false)
			PerHero("SetCloseToCombat", "close", false)
			PerHero("SetInAnyCombat", "any", true)

			function C.CombatApply()
				if Refii == nil or Refii.coop_mode == nil or Refii.coop_mode() ~= 3 then
					return
				end
				local hero = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if hero == nil or not hero:IsAlive() or not Player.IsAvailable(hero) then
					return
				end
				rawget(Player, "RefiiShippedSetMidstOfCombatCounter")(hero, Refii.player_value(2, "coop.combat.midst", 0))
				rawget(Player, "RefiiShippedSetCloseToCombat")(hero, Refii.player_value(2, "coop.combat.close", 0) ~= 0)
				rawget(Player, "RefiiShippedSetInAnyCombat")(hero, Refii.player_value(2, "coop.combat.any", 0) ~= 0)
			end
		end

		local function OwnEntity(self) return self ~= nil and self.Entity or nil end
		AsItsEntity(BehaviourBase, "RunUpdateFunction", OwnEntity)
		AsItsEntity(BehaviourBase, "RunExitFunction", OwnEntity)
		AsItsEntity(BehaviourBase, "RunFastExitFunctions", OwnEntity)
		AsItsEntity(AIManager, "FindBestBehaviourAndGroup", function(_, _, entity) return entity end)
		-- The running behaviour's own IsStillRunnable and IsFinished (aimanager.lua
		-- UpdateCurrentBehaviour, around its RunUpdateFunction) ask for the same hero its Update does.
		AsItsEntity(AIManager, "UpdateCurrentBehaviour", function(_, groups)
			local current = type(groups) == "table" and groups.CurrentBehaviour or nil
			local instance = type(current) == "table" and current.BehaviourInstance or nil
			return type(instance) == "table" and instance.Entity or nil
		end)

		local function UsesOwnDog(name, method)
			local table_ = _G[name]
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShippedDog" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if henchman == nil or self == nil or self.EntityUsingItem ~= henchman then
					return shipped(self, ...)
				end
				return C.WithFlag("dog", true, shipped, self, ...)
			end, "OwnDog." .. name .. "." .. method)
		end

		UsesOwnDog("OnCarriedActionUseDogTreat", "Update")
		UsesOwnDog("OnCarriedActionUseDogAccessory", "Update")
		UsesOwnDog("OnCarriedActionUseBonusEffects", "Update")
		UsesOwnDog("OnCarriedActionUseDig", "Update")

		-- A BREED CHANGE OF PLAYER TWO'S DOG makes the new dog player two's, not the quest dog:
		-- ApplyDogChange creates it (CreateEntityFromIDAtPosition, named "CreatureDogHero"),
		-- destroys the old one and stores it in QuestManager.DogEntity. Player two's dog is the
		-- one named RefiiPartnerDog on both machines (the name rides the create message), so the
		-- replacement is created under that name, the record is cleared for its first bind to
		-- take, and QuestManager keeps hero one's. ApplyDogChange never yields, so pcall is safe.
		if OnCarriedActionUseBase ~= nil and OnCarriedActionUseBase.ApplyDogChange ~= nil
			and OnCarriedActionUseBase.ApplyDogChange ~= C.ApplyDogChange then
			local shipped = OnCarriedActionUseBase.ApplyDogChange
			C.ApplyDogChange = C.Permanent(function(self, dog, breed, ...)
				local partner = C.PartnerDog()
				if partner == nil or dog ~= partner or Refii._set_partner_dog == nil then
					return shipped(self, dog, breed, ...)
				end
				local quest_dog = QuestManager and QuestManager.DogEntity
				local create = CreateEntityFromIDAtPosition
				CreateEntityFromIDAtPosition = function(id, name, ...)
					return create(id, "RefiiPartnerDog", ...)
				end
				Refii._set_partner_dog()
				local ok, failure = pcall(shipped, self, dog, breed, ...)
				CreateEntityFromIDAtPosition = create
				if QuestManager ~= nil then
					QuestManager.DogEntity = quest_dog
				end
				if not ok then
					error(failure, 0)
				end
			end, "ApplyDogChange")
			OnCarriedActionUseBase.ApplyDogChange = C.ApplyDogChange
		end

		-- THE DOG'S OWN NEEDS. BehaviourDogVariables.Reload (run by every dog's init) sets the
		-- desire rates on GetDog() only; player two's dog gets the same rates.
		if BehaviourDogVariables ~= nil and BehaviourDogVariables.Reload ~= nil
			and BehaviourDogVariables.Reload ~= C.ReloadDogVariables then
			local shipped = BehaviourDogVariables.Reload
			C.ReloadDogVariables = C.Permanent(function(...)
				local result = shipped(...)
				local dog = C.PartnerDog()
				local v = BehaviourDogVariables
				if dog ~= nil and dog:IsAlive() then
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_SNIFF, 1 / v.Desire_TimeToFillMeterInSeconds_Sniff)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_BARK, 1 / v.Desire_TimeToFillMeterInSeconds_Bark)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_PEE, 1 / v.Desire_TimeToFillMeterInSeconds_Pee)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_VILLAGER_INTERACTION, 1 / v.Desire_TimeToFillMeterInSeconds_VillagerInteraction)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_SLEEP, 1 / v.Desire_TimeToFillMeterInSeconds_Sleep)
				end
				return result
			end, "ReloadDogVariables")
			BehaviourDogVariables.Reload = C.ReloadDogVariables
		end

		-- LEADING ITS OWN PLAYER (dig spots, chests, quest objectives). The lead behaviour waits,
		-- barking, until "the player can see the dog/destination": IsEntityVisibleOnScreen /
		-- IsPositionVisibleOnScreen, which ask player one's camera. For player two's dog they ask
		-- player two's view (Refii._partner_screen; the host's copy of player two's camera).
		for _, method in ipairs({ "GetPlayerCanSeeDestination", "GetPlayerCanSeeDog" }) do
			local class = BehaviourDogLeadHeroBase
			if type(class) == "table" and type(class[method]) == "function"
				and rawget(class, "RefiiShippedScreen" .. method) == nil then
				local shipped = class[method]
				rawset(class, "RefiiShippedScreen" .. method, shipped)
				class[method] = C.Permanent(function(self, ...)
					local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
					if partner == nil or self == nil or Refii == nil or Refii._partner_screen == nil
						or Dog.GetMaster(self.Entity) ~= partner then
						return shipped(self, ...)
					end
					Refii._partner_screen(true)
					local ok, result = pcall(shipped, self, ...)
					Refii._partner_screen(false)
					if not ok then
						error(result, 0)
					end
					return result
				end, "DogLead." .. method)
			end
		end

		-- INJURED is this dog's injury (shipped: GetDog()'s).
		if BehaviourDogInjured ~= nil and BehaviourDogInjured.IsStillRunnable ~= C.DogInjuredRunnable then
			C.DogInjuredRunnable = C.Permanent(function(self)
				return BehaviourDogTrackHeroBase.IsStillRunnable(self) and Dog.IsInjured(self.Entity) and true or false
			end, "DogInjuredRunnable")
			BehaviourDogInjured.IsStillRunnable = C.DogInjuredRunnable
			C.DogInjuredSlowExit = C.Permanent(function(self, mode)
				BehaviourDogTrackHeroBase.SlowExit(self, mode)
				if not Dog.IsInjured(self.Entity) and not AIManager:IsModeMaintained(mode, AIManager.Modes.DOG_INJURED) then
					self:PerformActionEx({ Type = EScriptableAction.DOG_ANIMATION, Anim = "Healed", OverrideLooking = true,
						Priority = EActionPriority.PRIORITY_COMBAT_RESPONSE, BlendInTime = 0.5 })
				end
			end, "DogInjuredSlowExit")
			BehaviourDogInjured.SlowExit = C.DogInjuredSlowExit
		end

		-- TRICKS answer the dog's own master. Shipped, hero one's dog also answered player two's
		-- expressions (the GetPlayerHenchman() branch); player two's dog answers player two.
		if BehaviourDogPerformTrick ~= nil and BehaviourDogPerformTrick.IsStillRunnable ~= C.DogTrickRunnable then
			C.DogTrickRunnable = C.Permanent(function(self)
				if Dog.GetMoodType(self.Entity) == EDogMoodType.DOG_MOOD_TYPE_SCARED then
					return false
				end
				return ExpressionPerformer.IsPerformingExpression(Dog.GetMaster(self.Entity)) or Dog.IsDogCommandAvailable(self.Entity)
			end, "DogTrickRunnable")
			BehaviourDogPerformTrick.IsStillRunnable = C.DogTrickRunnable
			C.DogTrickPrepare = C.Permanent(function(self)
				if not self:IsStillRunnable() then
					return false
				end
				local master = Dog.GetMaster(self.Entity)
				self.Target = Targeting.GetTarget(master)
				if not self.Target:IsAlive() or self.Target == self.Entity
					or (self.Target:IsAlive() and not OpinionReaction.IsAvailable(self.Target)) then
					self.Target = master
				end
				return true
			end, "DogTrickPrepare")
			BehaviourDogPerformTrick.PrepareToRun = C.DogTrickPrepare
		end

		-- THE DOG WAITING OUTSIDE BUILDINGS. Shipped, when the hero enters a building with a
		-- Type 45 "Dog Outside" marker (e.g. the Cow & Corset Tavern in Bowerstone Market),
		-- BehaviourDogLyingDownBase:IsStillRunnable checked `spot:GetZ() < landZ + waterDepth`.
		-- In towns and paved streets, the entryway static mesh is below the natural terrain
		-- heightfield (landZ), so the check rejected the outside marker as "submerged" and
		-- the dog followed the hero inside where cramped quarters caused rapid animation cycling.
		-- Furthermore, IsAtDestination hardcoded a 10cm tolerance which caused the dog to circle
		-- endlessly and stand right back up, and the engine's offscreen auto-teleport (sub_822AEAC0)
		-- teleports the dog inside unless AIManager:GetMustStayType returns a stay mode (3 = LIE).
		-- Dog.IsMasterInsideBuilding (sub_82812E88) reads the master's flags with NO null guard: a
		-- dog whose master is not bound yet — every dog in the frames between its creation and
		-- the first CECDog_Update that finds the hero flagged, and the partner dog while hero one
		-- is still loading — faults at 0x34 (three player reports, 2026-10-25). Dog.GetMaster
		-- (sub_82174410) is the title's guarded reader and answers nil for that window.
		C.DogMasterInsideBuilding = C.DogMasterInsideBuilding or function(entity)
			if Dog == nil or Dog.IsMasterInsideBuilding == nil or Dog.GetMaster == nil then
				return false
			end
			if Dog.GetMaster(entity) == nil then
				return false
			end
			return Dog.IsMasterInsideBuilding(entity) == true
		end
		if BehaviourDogLyingDownBase ~= nil and BehaviourDogLyingDownBase.IsStillRunnable ~= C.DogLyingDownBaseRunnable then
			C.DogLyingDownBaseRunnable = C.Permanent(function(self)
				local spot = self:GetSleepSpot()
				if spot == nil then
					return false
				end
				if C.DogMasterInsideBuilding(self.Entity) then
					return true
				end
				local waterDepth = GetWaterDepthAt(spot)
				if waterDepth and waterDepth > 0.1 then
					local landZ = GetLandscapeHeightAt(spot)
					if spot:GetZ() < landZ + waterDepth then
						return false
					end
				end
				return true
			end, "DogLyingDownBaseRunnable")
			BehaviourDogLyingDownBase.IsStillRunnable = C.DogLyingDownBaseRunnable

			C.DogLyingDownBaseAtDestination = C.Permanent(function(self)
				local spot = self:GetSleepSpot()
				if spot == nil then
					return false
				end
				local isLying = self.IsLying or (ModeManager ~= nil and ModeManager.HasMode(self.Entity, EEntityModeType.EM_DOG_LYING))
				local tolerance = isLying and 2.5 or 2.0
				return self:IsCloseToPosition(spot, tolerance)
			end, "DogLyingDownBaseAtDestination")
			BehaviourDogLyingDownBase.IsAtDestination = C.DogLyingDownBaseAtDestination
		end

		if AIManager ~= nil and AIManager.GetMustStayType ~= nil and AIManager.GetMustStayType ~= C.DogGetMustStayType then
			local original_GetMustStayType = AIManager.GetMustStayType
			C.DogGetMustStayType = C.Permanent(function(self, entity)
				local stay = original_GetMustStayType(self, entity)
				if stay and stay ~= EDogStayType.DOG_STAY_TYPE_NONE then
					return stay
				end
				if entity and C.DogMasterInsideBuilding(entity) then
					return EDogStayType.DOG_STAY_TYPE_LIE
				end
				return EDogStayType.DOG_STAY_TYPE_NONE
			end, "DogGetMustStayType")
			AIManager.GetMustStayType = C.DogGetMustStayType

			local original_GetMustStayPosition = AIManager.GetMustStayPosition
			C.DogGetMustStayPosition = C.Permanent(function(self, entity)
				local pos = original_GetMustStayPosition(self, entity)
				if pos ~= nil then
					return pos
				end
				if entity and C.DogMasterInsideBuilding(entity) then
					return Dog.GetWaitOutsideBuildingPosition(entity)
				end
				return nil
			end, "DogGetMustStayPosition")
			AIManager.GetMustStayPosition = C.DogGetMustStayPosition
		end

		-- ITEMS AND PLACES PLAYER TWO USES AS THEMSELVES. Each script reads "the hero" from
		-- GetPlayerHero(); run as the user, it is the user's.
		--   beds (onactionusebed.lua:39 refused player two outright; sleep, sex, rent, trespass);
		--   potions, food and books (bonus effects; books apply them too);
		--   property: the sale sign's rules, knocking on an owned door, a bedroll's trespass;
		--   the DLC travel items' return point; the photo; the costume suits (QuestManager.HeroEntity
		--   throughout, served per acting player above).
		for _, use in ipairs({ "OnActionUseBed", "OnCarriedActionUseBonusEffects", "OnCarriedActionUseBook",
			"OnActionUseSaleSign", "OnActionUseKnockDoor", "OnCarriedActionUseBedRoll", "OnCarriedActionUseSkull",
			"OnCarriedActionUseSnowglobe", "OnCarriedActionUseSpireStatue", "OnCarriedActionUseArenaMiniature",
			"OnCarriedActionUsePhoto", "OnCarriedActionUseHobbeSuit", "OnCarriedActionUseBalverineSuit",
			"OnCarriedActionUseHollowmanSuit" }) do
			C.RunMethodAsUser(use, "Update", "EntityUsingItem")
		end
		UsesOwnDog("OnCarriedActionUseBook", "Update")

		-- A WILL POTION'S MORPH: ApplyBonusEffects sets the will stat with Debug.SetHeroWill(v),
		-- which has no entity and is hero one's. For player two it is player two's: the same
		-- value through Stats.ModifyWill on them.
		if Debug ~= nil and Debug.SetHeroWill ~= nil and Debug.SetHeroWill ~= C.SetHeroWill then
			local shipped = Debug.SetHeroWill
			C.SetHeroWill = C.Permanent(function(value, ...)
				local acting = C.Acting()
				if acting == nil or Stats == nil or Stats.ModifyWill == nil then
					return shipped(value, ...)
				end
				return Stats.ModifyWill(acting, value - Stats.GetWill(acting))
			end, "SetHeroWill")
			Debug.SetHeroWill = C.SetHeroWill
		end

		-- PROPERTY: the real-estate screen resolves its buyer natively, as hero one; opened from a
		-- sale sign player two used (OnActionUseSaleSign runs as its user, above), it buys for
		-- player two (coop_ui_hooks.cpp, RefiiCoopEstateBuyer).
		if GUI ~= nil and GUI.DisplayRealEstateScreen ~= nil and GUI.DisplayRealEstateScreen ~= C.DisplayRealEstateScreen then
			local shipped = GUI.DisplayRealEstateScreen
			C.DisplayRealEstateScreen = C.Permanent(function(...)
				if Refii ~= nil and Refii._estate_buyer ~= nil then
					Refii._estate_buyer(Refii.acting_player ~= nil and Refii.acting_player() or (C.Acting() ~= nil and 2 or 1))
				end
				return shipped(...)
			end, "DisplayRealEstateScreen")
			GUI.DisplayRealEstateScreen = C.DisplayRealEstateScreen
		end

		-- GUARDS fine and arrest the criminal they chase; shipped, the fine and the sleep tidy-up
		-- went to GetPlayerHero() whoever the criminal was.
		if BehaviourGuardChaseCriminal ~= nil then
			for _, method in ipairs({ "PrepareToRun", "Update", "TidyUpJustInCaseHeroWasSleeping", "SlowExit", "FastExit" }) do
				C.RunMethodAsUser("BehaviourGuardChaseCriminal", method, "Criminal")
			end
		end

		-- A HERO'S FAMILY ANSWERS THAT HERO. The spouse, child and family behaviours say
		-- GetPlayerHero() for "my husband/wife", "my mum/dad". The family natives answer for the
		-- hero they are given: PlayerFamily.IsMarriedTo / IsParentOf / IsFamilyMember / GetChildren
		-- take the family component (136) of their first argument (`sub_827BC100`) and walk its
		-- lists (`sub_821C6DB0`, `sub_82285B08`, `sub_827AFC90`, `sub_827B3E98`), and both heroes'
		-- bodies carry one. A villager in exactly one hero's family runs as that hero; in both or
		-- neither, as the hero the AI runs for.
		C.FamilyHeroOf = C.Permanent(function(entity)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if entity == nil or two == nil or PlayerFamily == nil or not entity:IsAlive() then
				return nil
			end
			local found = nil
			for _, hero in ipairs({ C.ShippedGetPlayerHero(), two }) do
				local children = hero ~= nil and hero:IsAlive() and PlayerFamily.GetChildren(hero, entity) or nil
				if hero ~= nil and hero:IsAlive() and (PlayerFamily.IsMarriedTo(hero, entity)
					or PlayerFamily.IsParentOf(hero, entity) or PlayerFamily.IsFamilyMember(hero, entity)
					or (type(children) == "table" and #children > 0)) then
					if found ~= nil then
						return nil
					end
					found = hero
				end
			end
			return found
		end, "FamilyHeroOf")

		-- The hero a villager is married to (player two first), or nil.
		function C.SpouseOf(villager)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if villager == nil or two == nil or PlayerFamily == nil then
				return nil
			end
			for _, hero in ipairs({ two, C.ShippedGetPlayerHero() }) do
				if hero ~= nil and hero:IsAlive() and PlayerFamily.IsMarriedTo(hero, villager) then
					return hero
				end
			end
			return nil
		end

		local function FamilyOfSelf(self)
			return C.FamilyHeroOf(type(self) == "table" and self.Entity or nil)
		end
		for _, name in ipairs({ "BehaviourSpouseComment", "BehaviourSpouseMissedHero", "BehaviourSpousePostChildBirth",
			"BehaviourSpouseHoneymoonPeriod", "BehaviourSpouseJealous", "BehaviourSpouseLeaveHero",
			"BehaviourSpouseCatchBigamist", "BehaviourFamilyIdleUntilSeeHero", "BehaviourThreatenDivorce",
			"BehaviourInteractWithCot", "BehaviourChildLeaveHero", "BehaviourGreetHeroParent",
			"BehaviourWitnessParentDeath", "BehaviourHerosChildComment", "BehaviourHerosChildCommentWhenPrompted" }) do
			C.WrapAs(name, nil, "Spouse", FamilyOfSelf)
		end
		-- "Is this a hero's child / spouse / family" asked by one method of a wider behaviour.
		C.WrapAs("BehaviourChildTalkToParent", { "PickLine" }, "Family", FamilyOfSelf)
		C.WrapAs("BehaviourParentTalkToChild", { "PickLine" }, "Family", FamilyOfSelf)
		C.WrapAs("BehaviourSleepAtHome", { "PrepareToRun" }, "Family", FamilyOfSelf)
		-- A divorcing parent's group mind is matched by the child's own PrepareToRun
		-- (GroupMindManager:GetValidGroupMind): the children are the parent's hero's.
		C.WrapAs("FamilyDivorceGroupMind", { "GetMetricForComparison" }, "Family", function(self)
			local parent = type(self) == "table" and type(self.GetParent) == "function" and self:GetParent() or nil
			return C.FamilyHeroOf(parent)
		end)
		-- A child following its parent: chosen as its family's hero, then that parent throughout.
		C.LatchAs("BehaviourFollowHeroParent", { "PrepareToRun", "IsStillRunnable", "Update", "MakeSuggestionToParent" },
			"Parent", FamilyOfSelf, false)

		-- A FIANCE'S HERO. OpinionReaction.IsEngagedToHero reads the villager's engaged byte
		-- (`sub_82427750`: comp+771), part of the per-hero opinion block the opinion scope swaps
		-- (mod_events.cpp kOpinionRegions {770, 3}), so asked as each hero it answers for each.
		C.FianceOf = C.Permanent(function(villager)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if villager == nil or two == nil or OpinionReaction == nil or not OpinionReaction.IsAvailable(villager) then
				return nil
			end
			if two:IsAlive() and C.RunAs(two, OpinionReaction.IsEngagedToHero, villager) then
				return two
			end
			local one = C.ShippedGetPlayerHero()
			if one ~= nil and C.RunAs(one, OpinionReaction.IsEngagedToHero, villager) then
				return one
			end
			return nil
		end, "FianceOf")
		local function FianceOfSelf(self)
			return C.FianceOf(type(self) == "table" and self.Entity or nil)
		end
		for _, name in ipairs({ "BehaviourBreakEngagement", "BehaviourFianceJealous" }) do
			C.LatchAs(name, { "PrepareToRun", "Update", "FastExit" }, "RefiiHero", FianceOfSelf, true)
		end

		-- A FOLLOWER'S COMMENTS ON A HOUSE are about the house the hero they follow targets
		-- (BehaviourHouseOpinion.WantToCommentAboutHouse / MakeCommentAboutHouse, called with the
		-- running behaviour as self from the micro-reactions).
		function C.FollowedHero(entity)
			if entity == nil or Follow == nil or not Follow.IsAvailable(entity) or not Follow.IsFollowing(entity) then
				return nil
			end
			local target = Follow.GetFollowTarget(entity)
			return C.IsHero(target) and target or nil
		end
		C.WrapAs("BehaviourHouseOpinion", { "WantToCommentAboutHouse", "MakeCommentAboutHouse" }, "House", function(self)
			return C.FollowedHero(type(self) == "table" and self.Entity or nil)
		end)

		-- FLIRTING, COURTING, LOVE: the hero chosen when the behaviour was picked stays its hero
		-- (it walks to them, reads their opinion, puts suggestions on their HUD). MakeSuggestion and
		-- DoReaction are left to their callers' hero (micro-reactions call them too).
		local function ChosenHero()
			return C.Acting() or C.ShippedGetPlayerHero()
		end
		for _, name in ipairs({ "BehaviourFlirt", "BehaviourCourtHero", "BehaviourLoveHero" }) do
			C.LatchAs(name, { "PrepareToRun", "IsStillRunnable", "Update", "SlowExit", "FastExit" }, "RefiiHero", ChosenHero, true)
		end

		-- WHO A VILLAGER FOLLOWS AND ANSWERS. The expression behaviour answers the hero who
		-- performed the expression it is handling (the message's sender), else the hero the AI runs
		-- for; the follow behaviours the hero they follow; shipped, both say GetPlayerHero(), so
		-- player two's "follow me" or seduction made the villager follow player one
		-- (FollowEntityFromExpression with GetPlayerHero()). A follower invited for sex follows the
		-- hero whose seduction it accepted (OpinionReaction.SetFollowForSex, below) before its
		-- follow target is set.
		function C.WrapClassAs(name, resolver)
			C.WrapAs(name, nil, "Hero", resolver)
		end
		C.SexInviter = C.SexInviter or {}
		local MessageArg = { FollowHero = 1, ReactToSeductionAttempt = 1, DoDeedReaction = 1, RespondToBegging = 1,
			IsFollowExpression = 2 }
		C.NoticedPartner = C.Permanent(function(behaviour, method, ...)
			local index = method ~= nil and MessageArg[method] or nil
			if index ~= nil and select("#", ...) >= index then
				local sender = C.SenderOf((select(index, ...)))
				if C.IsHero(sender) then
					return sender
				end
			end
			local entity = type(behaviour) == "table" and behaviour.Entity or nil
			return C.AIHeroFor(entity) or C.ShippedGetPlayerHero()
		end, "NoticedPartner")
		C.FollowLeader = C.Permanent(function(behaviour)
			local entity = type(behaviour) == "table" and behaviour.Entity or nil
			if entity == nil then
				return nil
			end
			if Follow ~= nil and type(Follow.GetFollowTarget) == "function" then
				local target = Follow.GetFollowTarget(entity)
				if target ~= nil and target:IsAlive() and C.IsHero(target) then
					return target
				end
			end
			local inviter = Refii ~= nil and Refii.handle ~= nil and C.SexInviter[Refii.handle(entity)] or nil
			if inviter ~= nil and inviter:IsAlive() and C.IsHero(inviter) then
				return inviter
			end
			return C.AIHeroFor(entity) or C.ShippedGetPlayerHero()
		end, "FollowLeader")
		C.WrapClassAs("BehaviourNoticeExpression", C.NoticedPartner)
		-- The deed's doer is the hero noticed (its title credit, its amused/scared stats).
		C.Wrap("BehaviourNoticeExpression", "DoDeedReaction", "Deed", function(shipped)
			return function(self, message, just_talk, entity_to_notice, ...)
				local sender = C.SenderOf(message)
				if C.IsHero(sender) then
					entity_to_notice = sender
				end
				return shipped(self, message, just_talk, entity_to_notice, ...)
			end
		end)
		for _, name in ipairs({ "BehaviourFollow", "BehaviourFollowForSex", "BehaviourFollowCatchUp", "BehaviourFollowWaitOutside" }) do
			C.WrapClassAs(name, C.FollowLeader)
		end

		-- ONLINE, PLAYER TWO'S FOLLOWERS ARE THE HOST'S. The guest never sends who follows whom and
		-- the villagers think on the host, so the joiner's hero two lists nobody: its bed never
		-- found a partner. The host publishes them (household_persist.cpp, Refii._followers_two:
		-- handle and flags, 1 engaged, 2 following for sex, 4 married), and on the joiner the follow
		-- queries for its own hero two answer from that list.
		function C.JoinerTwo(hero)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return hero ~= nil and partner ~= nil and hero == partner and Refii ~= nil and Refii.coop_mode ~= nil
				and Refii._followers_two ~= nil and Refii.coop_mode() == 3
		end
		function C.PublishedFollowers()
			local list = {}
			for i = 1, 32 do
				local handle, flags = Refii._followers_two(i)
				local entity = handle ~= nil and Refii.entity(handle) or nil
				if entity ~= nil then
					list[#list + 1] = { entity = entity, flags = flags or 0 }
				end
			end
			return list
		end
		if Follow ~= nil and Follow.GetListOfFollowers ~= nil and not rawget(Follow, "RefiiJoinerFollowers") then
			rawset(Follow, "RefiiJoinerFollowers", true)
			local shipped_list = Follow.GetListOfFollowers
			Follow.GetListOfFollowers = C.Permanent(function(hero, ...)
				if not C.JoinerTwo(hero) then
					return shipped_list(hero, ...)
				end
				local list = {}
				for _, follower in ipairs(C.PublishedFollowers()) do
					list[#list + 1] = follower.entity
				end
				return list
			end, "Follow.GetListOfFollowers")
			local shipped_sex = Follow.IsBeingFollowedBySexPartner
			if shipped_sex ~= nil then
				Follow.IsBeingFollowedBySexPartner = C.Permanent(function(hero, ...)
					if not C.JoinerTwo(hero) then
						return shipped_sex(hero, ...)
					end
					for _, follower in ipairs(C.PublishedFollowers()) do
						if follower.flags % 4 >= 2 then
							return true
						end
					end
					return false
				end, "Follow.IsBeingFollowedBySexPartner")
			end
		end

		-- ONLINE, PLAYER TWO'S BED IS PARTLY THE HOST'S. On the joiner the bed runs as shipped for its
		-- player (the menus, the fade, their own stats), but the partners, the pregnancy and the baby,
		-- the partner's opinion, the deeds and the gold are the host's world: SexFunction.Begin there
		-- would find no partner (SexFunction.SexPartners is filled by the host's follow behaviours) and
		-- a client's money changes nothing. So the joiner's Begin for its hero two also goes to the
		-- host (`sex:<bed uid>:<condom>`), which runs the bed's own payment (the prostitutes' and the
		-- inn's, as OnActionUseBed:Update/Sex reckon them) and Begin for its hero two in a thread of
		-- its own, silent: no fade, movie, sound, tutorial, message box or achievement on the host.
		C.HostThreads = C.HostThreads or {}
		function C.Silence(class, name, answer)
			if type(class) ~= "table" or type(class[name]) ~= "function" or rawget(class, "RefiiSilent" .. name) then
				return
			end
			local shipped = class[name]
			rawset(class, "RefiiSilent" .. name, shipped)
			class[name] = C.Permanent(function(...)
				if C.CoGet(coroutine.running(), "silent") then
					return answer
				end
				return shipped(...)
			end, "Silent." .. name)
		end
		C.Silence(GUI, "FadeScreenOut")
		C.Silence(GUI, "FadeScreenIn")
		C.Silence(GUI, "IsScreenFading", false)
		C.Silence(GUI, "SetDPadEnabled")
		C.Silence(GUI, "PlayLocalisedMovie")
		C.Silence(GUI, "DisplayMessageBox")
		C.Silence(Debug, "SetShouldDrawWorldIcons")
		C.Silence(Sound, "SetSoundEffectVolume")
		C.Silence(TutorialManager, "DisplayTutorial")

		-- THE JOINER'S OWN BED SCENE IS ITS PRESENTATION. Its Begin finds no partner (they are the
		-- host's), so what it would count is wrong and what it would show is incomplete: its tracked
		-- stats and achievements are left to the host's (sent here, mod_events.cpp `stat`/`ach`), and
		-- its fade-in waits for the host's verdict (RefiiCoop.SexVerdict: the partner, a child born,
		-- the first) to play what a birth plays — the PREGNANT line with the sound down for five
		-- seconds, Childbirth.bik for the first child, the baby tutorial after the fade.
		for _, name in ipairs({ "ModifyTrackedStat", "UnlockAchievement" }) do
			if Stats ~= nil and type(Stats[name]) == "function" and not rawget(Stats, "RefiiPresenting" .. name) then
				local shipped = Stats[name]
				rawset(Stats, "RefiiPresenting" .. name, shipped)
				Stats[name] = C.Permanent(function(...)
					if C.CoGet(coroutine.running(), "presenting") then
						return nil
					end
					return shipped(...)
				end, "Presenting." .. name)
			end
		end
		if GUI ~= nil and GUI.FadeScreenIn ~= nil and not rawget(GUI, "RefiiAwaitVerdict") then
			local shipped = GUI.FadeScreenIn
			rawset(GUI, "RefiiAwaitVerdict", shipped)
			GUI.FadeScreenIn = C.Permanent(function(...)
				local co = coroutine.running()
				if not C.CoGet(co, "presenting") then
					return shipped(...)
				end
				C.CoSet(co, "presenting", nil)
				local limit = Timing.GetWorldFrame() + Timing.GetTickRate() * 45.0
				return C.Call1(C.VerdictAfter, { n = select("#", ...), ... }, C.Wait1, C.VerdictPoll, { limit = limit },
					coroutine.yield)
			end, "AwaitVerdict.FadeScreenIn")
		end
		-- The host's verdict, or none once the limit passes.
		C.VerdictPoll = C.Permanent(function(state)
			if C.SexVerdict == nil and type(state) == "table" and Timing.GetWorldFrame() < (state.limit or 0) then
				return false
			end
			local verdict = C.SexVerdict
			C.SexVerdict = nil
			return true, verdict
		end, "VerdictPoll")
		-- A birth: the PREGNANT line (the host's partner speaking, it arrives with the scene's chatter)
		-- under five seconds of quiet, then Childbirth.bik for the first child; then the fade itself
		-- (the function the wait was wrapped over), then the baby tutorial.
		C.VerdictAfter = C.Permanent(function(args, verdict)
			if type(verdict) == "table" and verdict.born then
				Sound.SetSoundEffectVolume(0.0)
				local until_frame = Timing.GetWorldFrame() + Timing.GetTickRate() * 5.0
				return C.Call1(C.BirthAfter, { args = args, verdict = verdict }, C.Wait1, C.FramePoll, until_frame, coroutine.yield)
			end
			return C.Call1(C.TutorialAfter, verdict or false, rawget(GUI, "RefiiAwaitVerdict"), unpack(args, 1, args.n))
		end, "VerdictAfter")
		C.FramePoll = C.Permanent(function(until_frame)
			return Timing.GetWorldFrame() >= (until_frame or 0)
		end, "FramePoll")
		C.BirthAfter = C.Permanent(function(state)
			Sound.SetSoundEffectVolume(1.0)
			if state.verdict.first then
				GUI.PlayLocalisedMovie("Childbirth.bik")
			end
			return C.Call1(C.TutorialAfter, state.verdict, rawget(GUI, "RefiiAwaitVerdict"), unpack(state.args, 1, state.args.n))
		end, "BirthAfter")
		C.TutorialAfter = C.Permanent(function(verdict, ...)
			if type(verdict) == "table" and verdict.born and TutorialManager ~= nil
				and not TutorialManager.HasDisplayedTutorial(ETutorialType.TUTORIAL_BABY) then
				TutorialManager.DisplayTutorial(ETutorialType.TUTORIAL_BABY)
			end
			return ...
		end, "TutorialAfter")

		if SexFunction ~= nil and SexFunction.Begin ~= nil and SexFunction.Begin ~= C.SexBegin then
			local shipped = SexFunction.Begin
			-- C.SexHero is the hero having sex while Begin runs (BehaviourAvoidSex's hero).
			C.SexBegin = C.Permanent(function(hero, bed, ...)
				if bed ~= nil and C.JoinerTwo(hero) and Refii._household ~= nil and Refii.uid(bed) ~= nil then
					local condom = Player ~= nil and Player.IsWearingCondom ~= nil and Player.IsWearingCondom(hero)
					Refii._household("sex", Refii.uid(bed), condom and 1 or 0)
					if coroutine.running() ~= nil then
						C.SexVerdict = nil
						return C.WithFlag("presenting", true, shipped, hero, bed, ...)
					end
				end
				local before = C.SexHero
				C.SexHero = hero
				return C.Call1(C.SexHeroLeave, before, shipped, hero, bed, ...)
			end, "SexFunction.Begin")
			SexFunction.Begin = C.SexBegin
		end
		C.SexHeroLeave = C.Permanent(function(before, ...)
			C.SexHero = before
			return ...
		end, "SexHeroLeave")

		C.HostBedSexThread = C.Permanent(function(hero, bed, condom)
			return C.RunAs(hero, function()
				local owner = OwnedEntity.IsAvailable(bed) and OwnedEntity.GetOwner(bed) or nil
				local trespass, cost = false, 0
				if owner ~= nil and owner:IsAlive() then
					local bar = Building.IsBuildingOfType(owner, EBuildingType.BUILDING_BAR)
					trespass = not PlayerProperties.IsOwnedByPlayer(hero, owner) and not bar
					if bar then
						cost = Building.GetCostToSleepHere(owner) or 0
					end
				end
				local whores = 0
				for _, follower in ipairs(Follow.GetListOfFollowers(hero) or {}) do
					if Villager.GetOccupation(follower) == EJobType.JOB_WHORE and not PlayerFamily.IsMarriedTo(hero, follower) then
						whores = whores + 1
					end
				end
				local total = cost + whores * GDB.GetRecord("WhoreMultipliers"):GetS32("Cost")
				if total > 0 then
					Money.Modify(hero, -total)
				end
				if condom == 1 then
					Player.SetWearingCondom(hero, true)
				end
				-- What the bed's own Sex charges a hero after Begin (onactionusebed main.3 pc 303–362):
				-- the prostitutes' morality and purity, +10 morality for protection. Morality is the
				-- host's (a client's change applies only when received), so it is charged here.
				local multipliers = GDB.GetRecord("WhoreMultipliers")
				SexFunction.HenchmanHavingSex = false
				local before = SexFunction.FrameLastHadChild
				C.BirthSpouse = nil
				SexFunction.Begin(hero, bed, trespass, owner)
				if Stats.IsAvailable(hero) then
					Stats.ModifyMorality(hero, multipliers:GetS32("MoralityModifier") * whores)
					Stats.ModifyPurity(hero, multipliers:GetFloat("PurityModifier") * whores)
					if condom == 1 then
						Stats.ModifyMorality(hero, 10.0)
					end
				end
				-- The verdict for the joiner's own presentation (mod_events.cpp `sexdone`).
				local born = SexFunction.FrameLastHadChild ~= before
				local first = born and Refii._children_two ~= nil and Refii._children_two() == 1
				local partner = born and C.BirthSpouse ~= nil and Refii.uid(C.BirthSpouse) or "0x1"
				Refii._household("sexdone", partner, (born and 1 or 0) + (first and 2 or 0))
			end)
		end, "HostBedSexThread")

		-- THE CHATTER GOES TO THE JOINER. Every line a partner is told to say in the silent thread
		-- (the comments, the PREGNANT line) is played later by the villager's AI; its speaker is marked
		-- so those lines reach the joiner's screen too (mod_events.cpp ForwardSpeech).
		if ScriptFunction ~= nil and ScriptFunction.SaySimLine ~= nil and not rawget(ScriptFunction, "RefiiForwardSpeech") then
			local shipped = ScriptFunction.SaySimLine
			rawset(ScriptFunction, "RefiiForwardSpeech", shipped)
			ScriptFunction.SaySimLine = C.Permanent(function(speaker, ...)
				if C.CoGet(coroutine.running(), "silent") and speaker ~= nil and Refii._forward_speech ~= nil then
					Refii._forward_speech(Refii.handle(speaker), 15.0)
				end
				return shipped(speaker, ...)
			end, "Forward.SaySimLine")
		end

		-- Which spouse a birth in the silent thread was with (the joiner's verdict).
		if PlayerFamily ~= nil and PlayerFamily.AdoptWithSpouse ~= nil and not rawget(PlayerFamily, "RefiiBirthSpouse") then
			local shipped = PlayerFamily.AdoptWithSpouse
			rawset(PlayerFamily, "RefiiBirthSpouse", shipped)
			PlayerFamily.AdoptWithSpouse = C.Permanent(function(hero, spouse, ...)
				if C.CoGet(coroutine.running(), "silent") then
					C.BirthSpouse = spouse
				end
				return shipped(hero, spouse, ...)
			end, "BirthSpouse.AdoptWithSpouse")
		end

		function C.HostBedSex(bed, condom)
			local hero = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if bed == nil or hero == nil or not hero:IsAlive() then
				return
			end
			local co = coroutine.create(C.HostBedSexThread)
			C.CoSet(co, "silent", true)
			C.HostThreads[#C.HostThreads + 1] = { co = co, args = { hero, bed, condom } }
		end

		-- The host's threads run once a world frame (from the per-hero FrameUpdate, below).
		function C.HouseholdTick()
			local frame = GetWorldFrame ~= nil and GetWorldFrame() or 0
			if C.HouseholdFrame == frame or #C.HostThreads == 0 then
				return
			end
			C.HouseholdFrame = frame
			for i = #C.HostThreads, 1, -1 do
				local thread = C.HostThreads[i]
				if coroutine.status(thread.co) == "dead" then
					table.remove(C.HostThreads, i)
				else
					-- Player two's stats counted while it runs go to the joiner (mod_events.cpp).
					if Refii._household_context ~= nil then
						Refii._household_context(true)
					end
					local ok, err = coroutine.resume(thread.co, unpack(thread.args))
					if Refii._household_context ~= nil then
						Refii._household_context(false)
					end
					thread.args = {}
					if not ok then
						print("[refii] player two's bed on the host: " .. tostring(err))
						table.remove(C.HostThreads, i)
					end
				end
			end
		end

		-- QUEST PROGRESS IS THE WORLD'S. Code acting for player two still records quests on player
		-- one's tracker (quests belong to the host's world, as in Fable 3): a QuestTracker call
		-- whose hero argument is the acting partner is made for the stored hero instead.
		if type(QuestTracker) == "table" and not rawget(QuestTracker, "RefiiWorldTracker") then
			rawset(QuestTracker, "RefiiWorldTracker", true)
			local shipped = {}
			for key, value in pairs(QuestTracker) do
				if type(value) == "function" then
					shipped[key] = value
				end
			end
			for key, fn in pairs(shipped) do
				QuestTracker[key] = C.Permanent(function(hero, ...)
					local acting = C.Acting()
					if acting ~= nil and hero == acting and QuestManager ~= nil then
						hero = rawget(QuestManager, "RefiiHeroEntity") or (C.ShippedGetPlayerHero and C.ShippedGetPlayerHero())
					end
					return fn(hero, ...)
				end, "QuestTracker." .. key)
			end
		end

		-- QUESTS LOAD ON DEMAND (QuestManager:LoadQuestModule → require), after this chunk runs,
		-- so their classes are patched as each module loads (and those already loaded, now).
		-- A class wrapper is a permanent (see C.Permanent); the module table is one already
		-- (QuestManager:AddQuestToPermanentsTables).
		function C.RunMethodNear(class, method, field, name)
			if type(class) ~= "table" or type(class[method]) ~= "function" then
				return
			end
			local key = "RefiiShippedNear" .. method
			if rawget(class, key) == nil then
				rawset(class, key, class[method])
			end
			local shipped = rawget(class, key)
			class[method] = C.Permanent(function(self, ...)
				return C.RunNear(self and self[field], shipped, self, ...)
			end, "Near." .. name .. "." .. method)
		end

		-- JOBS FOR EITHER PLAYER (the woodsman, blacksmith, bartender and bard signs). While a sign
		-- waits, the worker is player two when player two alone targets it; once the job starts
		-- (CurrentState leaves 0) the worker is whoever it was then. Inside the sign's thread
		-- "the hero" is that worker: the gate, the pay (Money.Modify(QuestManager.HeroEntity)),
		-- the camera (SetCameraOverride is per hero), the stats. The partner is neither ghosted
		-- nor hidden (Fable 3 has no henchman to put away).
		C.JobWorker = C.Permanent(function(sign)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			if sign == nil or partner == nil or not partner:IsAlive() then
				return hero
			end
			if sign.CurrentState == 0 then
				local theirs = Targeting.GetTarget(partner) == sign.Entity
				local mine = hero ~= nil and Targeting.GetTarget(hero) == sign.Entity
				sign.RefiiWorker = theirs and not mine and partner or nil
				-- The job this sign ran is over: its meter goes back to player one's pad.
				if C.ActiveJobSign == sign then
					C.ActiveJobSign = nil
					if Refii ~= nil and Refii._job_worker ~= nil then
						Refii._job_worker(0)
					end
				end
			elseif Refii ~= nil and Refii._job_worker ~= nil then
				-- The running job's meter takes the worker's pad (coop_ui_hooks.cpp).
				C.ActiveJobSign = sign
				Refii._job_worker(sign.RefiiWorker ~= nil and 2 or 1)
			end
			return sign.RefiiWorker or hero
		end, "JobWorker")

		function C.RunJob(sign, fn, ...)
			local co = coroutine.running()
			if co == nil or sign == nil then
				return fn(...)
			end
			local before = C.CoGet(co, "job")
			C.CoSet(co, "job", true)
			return C.Call1(C.JobLeave, before, C.RunResolved, C.JobWorker, sign, fn, ...)
		end
		C.Permanent(C.RunJob, "RunJob")
		C.JobLeave = C.Permanent(function(before, ...)
			C.CoSet(coroutine.running(), "job", before)
			return ...
		end, "JobLeave")

		if Multiplayer ~= nil and not rawget(Multiplayer, "RefiiJobGhost") then
			rawset(Multiplayer, "RefiiJobGhost", true)
			for _, key in ipairs({ "SetHenchmanGhostMode", "SetHenchmanHiddenMode" }) do
				local shipped = Multiplayer[key]
				if type(shipped) == "function" then
					Multiplayer[key] = C.Permanent(function(...)
						if C.CoGet(coroutine.running(), "job") then
							return
						end
						return shipped(...)
					end, "Multiplayer." .. key)
				end
			end
		end

		-- DEMON DOORS answer whichever hero stands nearer: each door judges "the hero" — purity,
		-- items, expressions, followers, distance (qo900..qo990) — off QuestManager.HeroEntity,
		-- which inside a door's thread is player two while player two is the nearer.
		function C.PatchQuestModule(module)
			if type(module) ~= "table" or rawget(module, "RefiiPatched") then
				return
			end
			rawset(module, "RefiiPatched", true)
			for key, class in pairs(module) do
				if type(key) == "string" and type(class) == "table" then
					if key:match("^QO9%d%d_DemonDoor$") or key:match("^QO9%d%d_DemonDoorMouthTrigger$") then
						C.RunMethodNear(class, "CustomUpdate", "Entity", key)
					elseif key:match("^QO9%d%d_DemonDoor%w+$") then
						C.RunMethodNear(class, "Update", "Door", key)
					elseif key:match("^QJ00%d_Job%w+Sign$") and type(class.CustomUpdate) == "function"
						and rawget(class, "RefiiShippedJobCustomUpdate") == nil then
						local shipped = class.CustomUpdate
						rawset(class, "RefiiShippedJobCustomUpdate", shipped)
						class.CustomUpdate = C.Permanent(function(self, ...)
							return C.RunJob(self, shipped, self, ...)
						end, "Job." .. key .. ".CustomUpdate")
					end
				end
			end
		end

		if QuestManager ~= nil and QuestManager.LoadQuestModule ~= nil and QuestManager.LoadQuestModule ~= C.LoadQuestModule then
			local shipped = QuestManager.LoadQuestModule
			C.LoadQuestModule = C.Permanent(function(name, ...)
				local module = shipped(name, ...)
				C.PatchQuestModule(module)
				return module
			end, "LoadQuestModule")
			QuestManager.LoadQuestModule = C.LoadQuestModule
			for name in pairs(QuestManager.LoadedQuestModules or {}) do
				C.PatchQuestModule(_G[name] or (package ~= nil and package.loaded[name]) or nil)
			end
		end

		-- ============================================================================
		-- EACH NPC'S HERO, CASE BY CASE. The AI rule above runs an NPC for the hero nearer it; the
		-- cases below are the ones where the shipped script means a particular hero instead — the
		-- one who sent the message being answered, the one a behaviour latched when it started,
		-- the one a family, a crime, a cutscene or a building belongs to — or both heroes at once.
		-- ============================================================================
		local MSG = EMessageEventType or {}

		-- The two heroes, the one the AI runs `entity` for first (player one alone without a
		-- partner standing).
		function C.HeroesFor(entity)
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if two == nil or not two:IsAlive() then
				return { one }
			end
			if C.AIHeroFor(entity) == two then
				return { two, one }
			end
			return { one, two }
		end

		-- A behaviour whose PrepareToRun tests "the hero" with no message naming one: it is tried
		-- as each hero, the AI's first, and runs as the one it accepted (stored in `field` when
		-- `store`). Only for a PrepareToRun whose refusals change nothing — each one listed here
		-- returns false before any write that matters.
		function C.PrepareAsEither(name, field, store)
			C.Wrap(name, "PrepareToRun", "Either", function(shipped)
				return function(self, ...)
					local heroes = C.HeroesFor(type(self) == "table" and self.Entity or nil)
					for i, hero in ipairs(heroes) do
						local results = { C.RunAs(hero, shipped, self, ...) }
						if results[1] or i == #heroes then
							if store and type(self) == "table" then
								self[field] = results[1] and hero or nil
							end
							return unpack(results, 1, table.maxn(results))
						end
					end
					return shipped(self, ...)
				end
			end)
		end

		-- Runs fn (which never yields) once as each standing hero, player one first; answers
		-- player one's results.
		function C.RunAsBoth(fn, ...)
			local results = { C.RunAs(C.ShippedGetPlayerHero(), fn, ...) }
			local two = C.ShippedGetPlayerHenchman()
			if two ~= nil and two:IsAlive() then
				C.RunAs(two, fn, ...)
			end
			return unpack(results, 1, table.maxn(results))
		end

		-- "NOBODY NEAR". While C.AllHeroesFar runs fn (which never yields), the distance tests
		-- IsDistanceBetweenThingAndPositionOver / IsDistanceBetweenThingsOver asked about a hero
		-- answer for both heroes: "over" only when every standing hero is over.
		C.FarDepth = C.FarDepth or 0
		function C.AllHeroesFar(fn, ...)
			C.FarDepth = C.FarDepth + 1
			local results = { pcall(fn, ...) }
			C.FarDepth = C.FarDepth - 1
			if not results[1] then
				error(results[2], 0)
			end
			return unpack(results, 2, table.maxn(results))
		end
		for _, name in ipairs({ "IsDistanceBetweenThingAndPositionOver", "IsDistanceBetweenThingsOver" }) do
			if type(_G[name]) == "function" and C["Shipped" .. name] == nil then
				local shipped = _G[name]
				C["Shipped" .. name] = shipped
				_G[name] = C.Permanent(function(a, b, ...)
					if C.FarDepth == 0 then
						return shipped(a, b, ...)
					end
					local one = C.ShippedGetPlayerHero()
					local two = C.ShippedGetPlayerHenchman()
					if two == nil or not two:IsAlive() then
						return shipped(a, b, ...)
					end
					if C.IsHero(a) then
						return shipped(one, b, ...) and shipped(two, b, ...)
					end
					if C.IsHero(b) then
						return shipped(a, one, ...) and shipped(a, two, ...)
					end
					return shipped(a, b, ...)
				end, name)
			end
		end

		-- A VILLAGER'S CRIMES AND RENOWN ARE PER HERO. Villager.ForgiveHerosCrimes (`sub_827DE260`)
		-- forgives every crime the villager knows of, whoever committed it (each id at comp+0xC0, else
		-- comp+0xD0, through `sub_827D0770`, then the list cleared); HasHeroCommittedCrimeIHaveDiscovered
		-- (`sub_827DDD40`) compares each discovered crime's criminal with players+108 and
		-- GetPerceivedRenownOfHero (`sub_827DEBE8`) reads players+108's renown — hero one. The natives
		-- that take the hero (Refii.forgive_crimes / discovered_crime / perceived_renown, mod_events.cpp)
		-- answer for the hero the code acts for; with no partner, the shipped ones.
		C.VillagerPerHero = { ForgiveHerosCrimes = "forgive_crimes", HasHeroCommittedCrimeIHaveDiscovered = "discovered_crime",
			GetPerceivedRenownOfHero = "perceived_renown" }
		for method, native in pairs(C.VillagerPerHero) do
			if Villager ~= nil then
				C.Wrap("Villager", method, "Hero", function(shipped)
					return function(villager, ...)
						local two = C.ShippedGetPlayerHenchman()
						local api = Refii ~= nil and Refii[native] or nil
						if villager == nil or two == nil or api == nil then
							return shipped(villager, ...)
						end
						local player = Refii.acting_player ~= nil and Refii.acting_player() or (C.Acting() ~= nil and 2 or 1)
						local answer = api(Refii.handle(villager), player)
						if method == "ForgiveHerosCrimes" then
							return
						end
						if answer == nil then
							return shipped(villager, ...)
						end
						return answer
					end
				end, Villager)
			end
		end

		-- ---------------------------------------------------------------- interactive cutscenes
		-- ONE HERO PER INTERACTIVE CUTSCENE. Each actor's Update reaches the group mind
		-- (GetMyAction, SetAsActive, MoveToNextElement) under the AI rule — the hero nearer that
		-- actor — while the group mind's own Update runs from GroupMindManager:Update for player one,
		-- so one scene could grab one hero (Player.StartScriptControlMode, the cutscene rules, speed
		-- caps, cameras, microphone, HUD choices) and release or check the other. The scene holds
		-- one hero from SetAsActive (RefiiHero, saved with the group mind) until SetAsInactive has
		-- released everything it gave them; before that it is the hero in range (the scene's own
		-- IsPlayerInRange, asked as each hero), else the one nearer its main character. Every
		-- method of the group mind and of the actors' BehaviourCutscenePerformAction runs as it.
		local ICS = "InteractiveCutsceneGroupMind"
		C.CutsceneHero = C.Permanent(function(gm)
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if type(gm) ~= "table" or two == nil then
				return one
			end
			if gm.RefiiHeroLatched then
				local held = gm.RefiiHero
				return (held ~= nil and held == two and two:IsAlive()) and two or one
			end
			local frame = Timing.GetWorldFrame()
			if gm.RefiiGuessFrame == frame and gm.RefiiGuess ~= nil then
				return gm.RefiiGuess
			end
			if C.CutsceneGuessing then
				return one
			end
			local class = _G[ICS]
			local in_range = class ~= nil and rawget(class, "RefiiShippedCutsceneIsPlayerInRange") or nil
			local main = class ~= nil and rawget(class, "RefiiShippedCutsceneGetMainCharacter") or nil
			C.CutsceneGuessing = true
			local ok, guess = pcall(function()
				if in_range ~= nil and C.RunAs(one, in_range, gm) then
					return one
				end
				if in_range ~= nil and two:IsAlive() and C.RunAs(two, in_range, gm) then
					return two
				end
				return main ~= nil and C.NearerPartner(main(gm)) or one
			end)
			C.CutsceneGuessing = false
			guess = (ok and guess ~= nil) and guess or one
			gm.RefiiGuess = guess
			gm.RefiiGuessFrame = frame
			return guess
		end, "CutsceneHero")
		C.CutsceneHeroOfAction = C.Permanent(function(behaviour)
			local gm = type(behaviour) == "table" and behaviour.MyGroupMind or nil
			return C.CutsceneHero(gm)
		end, "CutsceneHeroOfAction")

		if type(_G[ICS]) == "table" then
			local special = { SetAsActive = true, SetAsInactive = true, PauseAnyCrimeAI = true, RestartAnyCrimeAI = true }
			for _, method in ipairs(C.OwnMethods(_G[ICS])) do
				if not special[method] then
					C.Wrap(ICS, method, "Cutscene", function(shipped)
						return function(self, ...)
							return C.RunResolved(C.CutsceneHero, self, shipped, self, ...)
						end
					end)
				end
			end
			C.Wrap(ICS, "SetAsActive", "Cutscene", function(shipped)
				return function(self, ...)
					if type(self) == "table" and not self.Active and not self.RefiiHeroLatched then
						self.RefiiHero = C.CutsceneHero(self)
						self.RefiiHeroLatched = true
					end
					return C.RunResolved(C.CutsceneHero, self, shipped, self, ...)
				end
			end)
			-- Released once nothing given to the held hero is still out (script control, a custom
			-- pose, a point of interest): a looping background scene, or one paused because its hero
			-- walked off, passes to the hero who walks up next.
			C.Wrap(ICS, "SetAsInactive", "Cutscene", function(shipped)
				return function(self, ...)
					return C.Call1(C.CutsceneReleased, self, C.RunResolved, C.CutsceneHero, self, shipped, self, ...)
				end
			end)
			-- A cutscene pauses and restarts the crime pursuit of both heroes.
			for _, method in ipairs({ "PauseAnyCrimeAI", "RestartAnyCrimeAI" }) do
				C.Wrap(ICS, method, "Cutscene", function(shipped)
					return function(self, ...)
						return C.RunAsBoth(shipped, self, ...)
					end
				end)
			end
		end
		C.CutsceneReleased = C.Permanent(function(self, ...)
			if type(self) == "table" and not self.Active and not self.GrabbedHero and not self.HeroInMode
				and not self.POITargetSet then
				self.RefiiHeroLatched = nil
				self.RefiiHero = nil
			end
			return ...
		end, "CutsceneReleased")
		if type(BehaviourCutscenePerformAction) == "table" then
			for _, method in ipairs(C.OwnMethods(BehaviourCutscenePerformAction)) do
				C.Wrap("BehaviourCutscenePerformAction", method, "Cutscene", function(shipped)
					return function(self, ...)
						if type(self) ~= "table" or type(self.MyGroupMind) ~= "table" then
							return shipped(self, ...)
						end
						return C.RunResolved(C.CutsceneHeroOfAction, self, shipped, self, ...)
					end
				end)
			end
		end

		-- A CUTSCENE SPEAKER answers the hero who hit it or performed at it
		-- (BehaviourCutsceneSpeakerIdle:Update compares each message's sender with the hero).
		C.SpeakerHero = C.Permanent(function(self)
			if type(self) ~= "table" or self.Entity == nil then
				return nil
			end
			return C.HeroSentTo(MSG.MESSAGE_EVENT_HIT, self.Entity, self.LastReactionMessage)
				or C.HeroSentTo(MSG.MESSAGE_EVENT_EXPRESSION_PERFORMED, self.Entity, self.LastReactionMessage)
				or C.AIHeroFor(self.Entity) or C.ShippedGetPlayerHero()
		end, "SpeakerHero")
		C.Wrap("BehaviourCutsceneSpeakerIdle", "Update", "Cutscene", function(shipped)
			return function(self, ...)
				return C.RunResolved(C.SpeakerHero, self, shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- allies and fights
		-- An ally follows the hero whose follow group it joined (PrepareToRun attached it to that
		-- hero's FollowGroupMind; Update asks the hero again for its formation slot).
		C.AllyLeader = C.Permanent(function(self)
			local gm = type(self) == "table" and self.MyGroupMind or nil
			local leader = type(gm) == "table" and gm.EntityAttachedTo or nil
			return C.IsHero(leader) and leader or nil
		end, "AllyLeader")
		C.Wrap("BehaviourAllyFollowHero", "Update", "Ally", function(shipped)
			return function(self, ...)
				if C.AllyLeader(self) == nil then
					return shipped(self, ...)
				end
				return C.RunResolved(C.AllyLeader, self, shipped, self, ...)
			end
		end)
		-- Whether a villager keeps fighting is about the hero it fights.
		C.WrapAs("BehaviourOpinionBasedAttack", { "IsStillRunnable" }, "Target", function(self)
			local target = type(self) == "table" and self.Target or nil
			return C.IsHero(target) and target or nil
		end)

		-- ---------------------------------------------------------------- homes
		-- ASKED TO LEAVE: either hero inside counts (the trespasser), and the vigilante, the fine
		-- and the crime are that hero's (self.Hero, which the shipped PrepareToRun stores).
		C.PrepareAsEither("BehaviourHomeAskHeroToLeave", "Hero", false)
		C.PrepareAsEither("BehaviourShopKeeperAskHeroToLeave", "Hero", false)
		C.LatchAs("BehaviourHomeAskHeroToLeave", { "Update", "IsStillRunnable", "FastExit" }, "Hero", nil, false)
		C.LatchAs("BehaviourShopKeeperAskHeroToLeave", { "IsStillRunnable" }, "Hero", nil, false)

		-- A HOUSE'S HERO is its owner (PlayerProperties.IsOwnedByPlayer reads the properties
		-- component of the hero it is given, `sub_82197CE0`), else the hero the AI runs the house
		-- for: its sims do not lock a hero out of their own home.
		function C.HouseHero(gm)
			local house = type(gm) == "table" and gm.EntityAttachedTo or nil
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if house == nil or two == nil or PlayerProperties == nil then
				return nil
			end
			if two:IsAlive() and PlayerProperties.IsOwnedByPlayer(two, house) then
				return two
			end
			if one ~= nil and PlayerProperties.IsOwnedByPlayer(one, house) then
				return one
			end
			return C.AIHeroFor(house) or one
		end
		C.WrapAs("HomeGroupMind", { "Update", "IncrementNumberOfPeopleToCloseAndLockDoor", "DecrementNumberOfPeopleToCloseAndLockDoor" },
			"House", C.HouseHero)

		-- ---------------------------------------------------------------- reactions
		-- AN INTERROGATION IS ANSWERED TO THE INTERROGATOR: the micro-reaction comment
		-- (BehaviourBase:CommentBasedOnOpinionCheck) and a baby's reaction checks face, greet and
		-- judge the hero whose interaction they are handling, else the hero the AI runs for — one
		-- hero for the whole comment, which turns to them over several seconds.
		if MSG.MESSAGE_EVENT_INTERACTED_WITH ~= nil then
			C.Wrap("BehaviourBase", "CommentBasedOnOpinionCheck", "Micro", function(shipped)
				return function(self, ...)
					if type(self) ~= "table" or self.Entity == nil or C.ShippedGetPlayerHenchman() == nil then
						return shipped(self, ...)
					end
					local hero = C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, self.LastInterrogationMessage)
						or C.AIHeroFor(self.Entity) or C.ShippedGetPlayerHero()
					return C.RunAs(hero, shipped, self, ...)
				end
			end)
			C.Wrap("BehaviourBaby", "BasicReactionChecks", "Micro", function(shipped)
				return function(self, ...)
					local hero = type(self) == "table"
						and C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, self.BabyLastInterrogationMessage) or nil
					return C.RunAs(hero, shipped, self, ...)
				end
			end)
			-- Interacting with a villager: the whole reaction is for the interactor (who courts,
			-- gossips, is told about the rent), latched when it is picked.
			C.LatchAs("BehaviourReactOnInteraction", { "PrepareToRun", "Update", "FastExit" }, "RefiiActor", function(self)
				return type(self) == "table" and C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, nil) or nil
			end, true)
			-- A barman's job subgame is served by the hero who interacted (the jug, the pour, the tip).
			C.Wrap("BehaviourBarmanJobBase", "CheckForInteractionMessages", "Server", function(shipped)
				return function(self, ...)
					local server = type(self) == "table"
						and C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, self.LastMessageID_InteractedWith) or nil
					return C.RunAs(server, shipped, self, ...)
				end
			end)
		end

		-- The crowd an expression drew is the performer's: it is counted down for the hero it was
		-- counted up for (ExpressionPerformer.IncrementCrowdSize / DecrementCrowdSize).
		C.Wrap("BehaviourBase", "SetMicroReactionLabelAndPriorities", "Crowd", function(shipped)
			return function(self, label, ...)
				if type(self) ~= "table" or C.ShippedGetPlayerHenchman() == nil then
					return shipped(self, label, ...)
				end
				if label == "Notice Expression" then
					self.RefiiCrowdHero = GetPlayerHero()
					return shipped(self, label, ...)
				end
				if self.MicroReactLabel == "Notice Expression" and C.IsHero(self.RefiiCrowdHero) then
					local hero = self.RefiiCrowdHero
					self.RefiiCrowdHero = nil
					return C.RunAs(hero, shipped, self, label, ...)
				end
				return shipped(self, label, ...)
			end
		end)
		C.Wrap("BehaviourBase", "GenericFastExit", "Crowd", function(shipped)
			return function(self, ...)
				if type(self) == "table" and self.MicroReactLabel == "Notice Expression" and C.IsHero(self.RefiiCrowdHero) then
					local hero = self.RefiiCrowdHero
					self.RefiiCrowdHero = nil
					return C.RunAs(hero, shipped, self, ...)
				end
				return shipped(self, ...)
			end
		end)

		-- Extortion is the extorter's: their money, their crime.
		C.WrapAs("BehaviourRespondToExtortion", nil, "Extort", function(self, method)
			if type(self) ~= "table" then
				return nil
			end
			if method == "PrepareToRun" then
				return C.HeroSentTo(MSG.MESSAGE_EVENT_EXPRESSION_PERFORMED, self.Entity, nil)
			end
			local sender = C.SenderOf(self.Message)
			return C.IsHero(sender) and sender or nil
		end)

		-- A hit is the hitter's: from either hero it is noticed (and consumed) as theirs, and the
		-- reaction that follows is to them.
		C.Wrap("BehaviourReactToAbuse", "IsHitByPlayer", "Abuse", function(shipped)
			return function(self, ...)
				local hero = type(self) == "table" and C.HeroSentTo(MSG.MESSAGE_EVENT_HIT, self.Entity, self.LastMessageID_Hit) or nil
				if hero ~= nil then
					self.RefiiHitter = hero
				end
				return C.RunAs(hero, shipped, self, ...)
			end
		end)
		C.LatchAs("BehaviourReactToAbuse", { "Update", "ReactToAbuse" }, "RefiiHitter", nil, false)

		-- A deed is judged as its doer's.
		C.WrapAs("BehaviourReactToDeed", { "PerformDeedReaction" }, "Deed", function(self, method, message)
			local sender = C.SenderOf(message)
			return C.IsHero(sender) and sender or nil
		end)
		-- A reaction faces the hero it is to (InteractedWithHero is that hero's record).
		C.WrapAs("ReactionHelpers", { "PerformReactionNoWait" }, "Face", function(self, method, behaviour, record, can_speak,
			just_talk, entity_to_face)
			return C.IsHero(entity_to_face) and entity_to_face or nil
		end)
		-- A reaction to "the henchman" is a reaction to the other hero, judged by that hero's opinion.
		C.Wrap("BehaviourOpinionReaction", "GetReaction", "Henchman", function(shipped)
			return function(self, entity, target, ...)
				if EOpinionTarget ~= nil and target == EOpinionTarget.EOT_HENCHMAN
					and not (Multiplayer ~= nil and Multiplayer.IsHenchmanInGhostMode()) then
					local other = GetPlayerHenchman()
					if other ~= nil and other:IsAlive() then
						return C.RunAs(other, shipped, self, entity, EOpinionTarget.EOT_PLAYER, ...)
					end
				end
				return shipped(self, entity, target, ...)
			end
		end)
		-- A dog's trick is noticed as its master's dog's (GetDog() is the acting hero's dog).
		C.LatchAs("BehaviourNoticeDogTricks", { "PrepareToRun", "Update", "SlowExit" }, "RefiiHero", function(self)
			if type(self) ~= "table" or self.Entity == nil then
				return nil
			end
			local posted, message = MessageEvents.IsMessageSentTo(MSG.MESSAGE_EVENT_EXPRESSION_PERFORMED, self.Entity,
				self.LastMessageID_ExpressionPerformed)
			local dog = posted and C.SenderOf(message) or nil
			if dog == nil or not IsDog(dog) then
				return nil
			end
			local master = Dog.GetMaster(dog)
			return C.IsHero(master) and master or nil
		end, true)

		-- Tried as each hero: a known person's reaction to what a hero did nearby, an apology,
		-- running away from a frightening hero.
		for _, name in ipairs({ "BehaviourReactToKnownPersonInteraction", "BehaviourAcceptApology", "BehaviourAcceptApologyVigilante" }) do
			C.PrepareAsEither(name, "RefiiHero", true)
			C.LatchAs(name, { "Update" }, "RefiiHero", nil, false)
		end
		C.PrepareAsEither("BehaviourRunAway", "FleeFrom", false)
		C.LatchAs("BehaviourRunAway", { "IsStillRunnable", "Update" }, "FleeFrom", nil, false)
		-- A villager killed by a hero is not a reason to flee "from an enemy" for either hero.
		C.WrapAs("BehaviourScriptControlFlee", { "PrepareToRun" }, "Flee", function(self)
			local village = type(self) == "table" and Villager.IsAvailable(self.Entity) and Villager.GetVillage(self.Entity) or nil
			return village ~= nil and C.HeroSentTo(MSG.MESSAGE_EVENT_VILLAGER_KILLED_BY_ENEMY, village, nil) or nil
		end)
		-- Sex is avoided around the hero having it.
		C.WrapAs("BehaviourAvoidSex", nil, "Sex", function()
			if C.SexHero ~= nil and C.SexHero:IsAlive() then
				return C.SexHero
			end
			local two = C.ShippedGetPlayerHenchman()
			if two ~= nil and two:IsAlive() and Follow ~= nil and Follow.IsBeingFollowedBySexPartner(two) then
				return two
			end
			return C.ShippedGetPlayerHero()
		end)

		-- A title bought from the town crier is announced for its buyer (the crier's last
		-- customer, kept by BehaviourTownCrierOpenGUI above).
		C.WrapAs("BehaviourAnnounceHeroTitle", { "PrepareToRun", "Update" }, "Title", function(self)
			local customer = type(self) == "table" and self.Entity ~= nil and Refii ~= nil and C.CrierCustomer[Refii.handle(self.Entity)] or nil
			return C.IsHero(customer) and customer or nil
		end)

		-- ---------------------------------------------------------------- crime
		-- THE CRIMINAL. A victim, a witness or a vigilante responds to the hero who is the
		-- village's criminal (VillageCrimeManager.IsCriminal answers for the hero it is given); both
		-- criminals, the one the AI runs for.
		function C.CriminalFor(village, entity)
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if village == nil or two == nil or VillageCrimeManager == nil then
				return nil
			end
			local first = one ~= nil and VillageCrimeManager.IsCriminal(village, one)
			local second = two:IsAlive() and VillageCrimeManager.IsCriminal(village, two)
			if first and second then
				return C.AIHeroFor(entity) or one
			end
			return (first and one) or (second and two) or nil
		end
		for _, name in ipairs({ "BehaviourRespondToCrime", "BehaviourRespondToCrimeVictim", "BehaviourRespondToCrimeWitness",
			"BehaviourRespondToCrimeVigilante" }) do
			C.LatchAs(name, C.OwnMethods(_G[name]), "RefiiCriminal", function(self, ptr_data)
				if type(self) ~= "table" or self.Entity == nil then
					return nil
				end
				local village = type(ptr_data) == "table" and ptr_data.Village or nil
				if village == nil and Villager.IsAvailable(self.Entity) then
					village = Villager.GetVillage(self.Entity)
				end
				return C.CriminalFor(village, self.Entity)
			end, true)
		end

		-- GUARDS AND VILLAGES. GuardGroupMind:Update attacks "the hero" when resisting arrest and
		-- nobody is being attacked yet: each resisting hero is attacked. Attacking or letting off a
		-- hero takes that hero's own dog and leaves the other hero alone (the crime is the
		-- criminal's; shipped, the henchman and the one dog were accomplices).
		C.Wrap("GuardGroupMind", "Update", "Crime", function(shipped)
			return function(self, ...)
				return C.Call1(C.GuardsAttack, self, C.RunAs, C.ShippedGetPlayerHero(), shipped, self, ...)
			end
		end)
		C.GuardsAttack = C.Permanent(function(self, ...)
			local village = type(self) == "table" and self.EntityAttachedTo or nil
			local two = C.ShippedGetPlayerHenchman()
			if village ~= nil and two ~= nil and type(self.CriminalsAttacking) == "table" and C.ShippedIsResistingArrest ~= nil then
				for _, hero in ipairs({ C.ShippedGetPlayerHero(), two }) do
					local listed = false
					for _, criminal in pairs(self.CriminalsAttacking) do
						if criminal == hero then
							listed = true
						end
					end
					if not listed and hero:IsAlive() and C.ShippedIsResistingArrest(village, hero) then
						self:AttackCriminal(hero)
					end
				end
			end
			return ...
		end, "GuardsAttack")
		for _, method in ipairs({ "AttackCriminal", "StopAttackingCriminal" }) do
			C.Wrap("GuardGroupMind", method, "Crime", function(shipped)
				return function(self, criminal, ...)
					if C.ShippedGetPlayerHenchman() == nil or not C.IsHero(criminal) then
						return shipped(self, criminal, ...)
					end
					return C.HideHenchman(C.WithDogOf, criminal, shipped, self, criminal, ...)
				end
			end)
		end
		-- A village spawns its transients and guards out of sight of both heroes, and a guard
		-- spawned while a hero resists arrest goes for that hero.
		C.Wrap("VillageGroupMind", "Update", "Crime", function(shipped)
			return function(self, ...)
				local two = C.ShippedGetPlayerHenchman()
				local village = type(self) == "table" and self.EntityAttachedTo or nil
				if two == nil or village == nil or C.ShippedIsResistingArrest == nil then
					return shipped(self, ...)
				end
				local one = C.ShippedGetPlayerHero()
				local first = one ~= nil and C.ShippedIsResistingArrest(village, one)
				local second = two:IsAlive() and C.ShippedIsResistingArrest(village, two)
				local hero = (second and not first) and two or one
				if first and second then
					return C.RunAs(hero, C.AllHeroesFar, shipped, self, ...)
				end
				return C.RunAs(hero, C.HideHenchman, C.AllHeroesFar, shipped, self, ...)
			end
		end)
		-- A farmer's crate goes home unseen only when both heroes are far from it.
		C.Wrap("FarmGroupMind", "TidyCrates", "Far", function(shipped)
			return function(self, ...)
				return C.AllHeroesFar(shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- despawning
		-- AN EVICTED VILLAGER OR A SPOUSE LEAVING is removed only where neither hero sees it
		-- (`not IsEntityVisibleOnScreen(e) and IsDistanceBetweenThingsOver(e, hero, 30)`): under
		-- a coroutine's `despawn` scope, IsEntityVisibleOnScreen also answers for player two's view
		-- (Refii._partner_screen) and for either hero within 30 m.
		if IsEntityVisibleOnScreen ~= nil and C.ShippedIsEntityVisibleOnScreen == nil then
			C.ShippedIsEntityVisibleOnScreen = IsEntityVisibleOnScreen
			IsEntityVisibleOnScreen = C.Permanent(function(entity, ...)
				local shipped = C.ShippedIsEntityVisibleOnScreen
				local co = coroutine.running()
				local two = C.ShippedGetPlayerHenchman()
				if not C.CoGet(co, "despawn") or entity == nil or two == nil then
					return shipped(entity, ...)
				end
				if shipped(entity, ...) then
					return true
				end
				if Refii ~= nil and Refii._partner_screen ~= nil then
					Refii._partner_screen(true)
					local ok, seen = pcall(shipped, entity, ...)
					Refii._partner_screen(false)
					if ok and seen then
						return true
					end
				end
				for _, hero in ipairs({ C.ShippedGetPlayerHero(), two }) do
					if hero:IsAlive() and GetDistanceBetweenEntities(hero, entity) < 30 then
						return true
					end
				end
				return false
			end, "IsEntityVisibleOnScreen")
		end
		for _, name in ipairs({ "BehaviourEvicted", "BehaviourSpouseLeaveHero" }) do
			C.Wrap(name, "Update", "Despawn", function(shipped)
				return function(self, ...)
					return C.WithFlag("despawn", true, shipped, self, ...)
				end
			end)
		end

		-- A PROSTITUTE'S THUMBS-UP/DOWN SUGGESTIONS went to whichever hero was nearer when added;
		-- they are taken off both heroes' HUDs.
		if Player ~= nil then
			C.Wrap("Player", "RemoveHUDSuggestionOfType", "Both", function(shipped)
				return function(hero, ...)
					local results = { shipped(hero, ...) }
					local co = coroutine.running()
					if C.CoGet(co, "hud_both") and C.IsHero(hero) then
						local other = C.Other(hero)
						if other ~= nil and other:IsAlive() then
							shipped(other, ...)
						end
					end
					return unpack(results, 1, table.maxn(results))
				end
			end, Player)
		end
		C.Wrap("BehaviourWhoreIdle", "Update", "Both", function(shipped)
			return function(self, ...)
				return C.WithFlag("hud_both", true, shipped, self, ...)
			end
		end)
		C.Wrap("BehaviourWhoreIdle", "FastExit", "Both", function(shipped)
			return function(self, ...)
				return C.RunAsBoth(shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- jobs, shops, followers
		-- A follower invited for sex follows the hero whose seduction it accepted.
		if OpinionReaction ~= nil and type(OpinionReaction.SetFollowForSex) == "function" then
			C.Wrap("OpinionReaction", "SetFollowForSex", "Inviter", function(shipped)
				return function(villager, on, ...)
					if villager ~= nil and Refii ~= nil and Refii.handle ~= nil then
						C.SexInviter[Refii.handle(villager)] = on and (C.Acting() or C.ShippedGetPlayerHero()) or nil
					end
					return shipped(villager, on, ...)
				end
			end, OpinionReaction)
		end

		-- Behaviour groups only a hero's spouse or child runs: either hero's. The home emergency
		-- (calling the guards on an intruder) spares either hero's family.
		local groups = AIManager ~= nil and AIManager.BehaviourGroupPrepareToRuns or nil
		if type(groups) == "table" then
			for _, name in ipairs({ "SpouseBehaviourGroup", "SpouseMiscBehaviourGroup", "HerosChildBehaviourGroup" }) do
				C.Wrap("BehaviourGroupPrepareToRuns", name, "Either", function(shipped)
					return function(entity, ...)
						local two = C.ShippedGetPlayerHenchman()
						local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, entity, ...) }
						if results[1] or two == nil or not two:IsAlive() then
							return unpack(results, 1, table.maxn(results))
						end
						return C.RunAs(two, shipped, entity, ...)
					end
				end, groups)
			end
			C.Wrap("BehaviourGroupPrepareToRuns", "HomeEmergencyBehaviourGroup", "Both", function(shipped)
				return function(entity, ...)
					local two = C.ShippedGetPlayerHenchman()
					if two == nil or not two:IsAlive() then
						return shipped(entity, ...)
					end
					local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, entity, ...) }
					if not results[1] or not C.RunAs(two, shipped, entity, ...) then
						return false
					end
					return unpack(results, 1, table.maxn(results))
				end
			end, groups)
		end

		-- A bigamy chat: both chatters are asked about the same hero, each hero in turn.
		C.Wrap("BehaviourVillagerChat", "IsThisADivorceConversation", "Either", function(shipped)
			return function(self, partner, ...)
				local two = C.ShippedGetPlayerHenchman()
				local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, self, partner, ...) }
				if results[1] or two == nil or not two:IsAlive() then
					return unpack(results, 1, table.maxn(results))
				end
				return C.RunAs(two, shipped, self, partner, ...)
			end
		end)

		-- A hero-owned business: its keeper's and patrons' lines about the owner are for its owner.
		function C.HeroOwner(building)
			local owner = building ~= nil and Building ~= nil and Building.GetBuildingOwner(building) or nil
			return C.IsHero(owner) and owner or nil
		end
		local function OwnerOfGroup(self)
			local gm = type(self) == "table" and self.MyGroupMind or nil
			return C.HeroOwner(type(gm) == "table" and gm.EntityAttachedTo or nil)
		end
		for _, name in ipairs({ "BehaviourShopKeeperHawk", "BehaviourStallVendorAttractMode", "BehaviourStallPatronGotoStall" }) do
			C.WrapAs(name, { "Update" }, "Owner", OwnerOfGroup)
		end
		C.WrapAs("BehaviourShopPatronCommentOnPrice", { "PrepareToRun" }, "Owner", function(self)
			if type(self) ~= "table" or GroupMind == nil or GroupMind.Types == nil then
				return nil
			end
			local gm = GroupMindManager:GetGroupMind(GroupMind.Types.ShopGroupMind, self.Entity)
			return C.HeroOwner(type(gm) == "table" and gm.EntityAttachedTo or nil)
		end)

		-- A villager leading a hero watches the hero it leads (self.Player, latched at PrepareToRun).
		C.LatchAs("BehaviourLeadHero", { "Update" }, "Player", nil, false)

		-- A TRAVELLING TRADER answers either hero targeting it. Targeted.IsTargetLockedByHero
		-- tests player one's targeting only (`sub_822950B0`: the hero at players+108); player two's
		-- target is their Targeting.GetTarget.
		function C.TraderTargeted(entity)
			if entity == nil or Targeted == nil then
				return false
			end
			if Targeted.IsTargetLockedByHero(entity) or Targeted.IsTargetedByHero(entity) then
				return true
			end
			local two = C.ShippedGetPlayerHenchman()
			return two ~= nil and two:IsAlive() and Targeting.GetTarget(two) == entity
		end
		C.TraderTargeter = C.Permanent(function(self)
			local entity = type(self) == "table" and self.Entity or nil
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if entity ~= nil and two ~= nil and two:IsAlive() and Targeting.GetTarget(two) == entity
				and Targeting.GetTarget(one) ~= entity then
				return two
			end
			return one
		end, "TraderTargeter")
		-- The behaviour's update: comment while targeted, a frame at a time (a loop as tail steps
		-- through C.Call1, so no frame of ours is under the yield).
		C.TraderUpdate = C.Permanent(function(self)
			self:LookAtEntity(GetPlayerHero(), ELookAtPriority.NORMAL_PRIORITY, ELookAt.WITH_ALL)
			return C.TraderStep(self)
		end, "TraderUpdate")
		C.TraderStep = C.Permanent(function(self)
			if not C.TraderTargeted(self.Entity) then
				return
			end
			return C.Call1(C.TraderYield, self, self.CommentBasedOnOpinionCheck, self)
		end, "TraderStep")
		C.TraderYield = C.Permanent(function(self, ...)
			return C.Call1(C.TraderStep, self, coroutine.yield)
		end, "TraderYield")
		C.Wrap("BehaviourTravellingTraderTargeted", "PrepareToRun", "Trader", function()
			return function(self)
				return C.TraderTargeted(type(self) == "table" and self.Entity or nil)
			end
		end)
		C.Wrap("BehaviourTravellingTraderTargeted", "IsStillRunnable", "Trader", function()
			return function(self)
				return C.TraderTargeted(type(self) == "table" and self.Entity or nil)
			end
		end)
		C.Wrap("BehaviourTravellingTraderTargeted", "Update", "Trader", function()
			return function(self)
				return C.RunResolved(C.TraderTargeter, self, C.TraderUpdate, self)
			end
		end)

		-- A closed shop's doors: the hero inside is the one asked whether they are in or out.
		C.Wrap("ShopGroupMind", "Update", "Inside", function(shipped)
			return function(self, ...)
				local shop = type(self) == "table" and self.EntityAttachedTo or nil
				local one = C.ShippedGetPlayerHero()
				local two = C.ShippedGetPlayerHenchman()
				local inside = shop ~= nil and two ~= nil and two:IsAlive() and Whereabouts.GetBuildingWeAreInsideOf(two) == shop
					and Whereabouts.GetBuildingWeAreInsideOf(one) ~= shop
				return C.RunAs(inside and two or one, shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- player two's player state
		-- PLAYER TWO IS A PLAYER ON THE HOST. Online, the host's copy of player two's body has no
		-- Player component (Player.IsAvailable false, IsPlayer true), so shipped fights treated
		-- player two as a non-player: a beetle kept attacking a hero in interaction mode
		-- (CombatSequences BeetleCloseAttack/BeetleAttack, CombatStates:BeetleSpit), a beetle's hug
		-- could not be shaken off (CombatStates:BeetleHug), a hobbe caster in chaos would not heal
		-- them (CombatStates:SpellCasterHeal), and the child rule applied to them
		-- (CombatRegister:IsValidTarget). The joiner, which plays player two, states that hero's
		-- player modes in the player store (coop.q.mode.<EPlayerMode>, with the other stated queries
		-- of THE ROUTE below) and its button presses while in interaction mode (coop.mode.bash); the
		-- host's Start/StopInteractionMode for them travel on the route, and until the joiner has applied
		-- the last one (coop.route.done) the commanded mode is the answer. Those functions read Player
		-- and MessageEvents through views that answer for player two from the store (their
		-- environment, setfenv: the rest of the game sees the real tables).
		function C.StatedPlayer(entity)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return entity ~= nil and two ~= nil and entity == two and Refii ~= nil and Refii.player_value ~= nil
				and not Player.IsAvailable(entity) and entity:IsAlive()
		end
		function C.StatedMode(entity, mode)
			if EPlayerMode == nil or mode == nil then
				return false
			end
			local pending = C.ModePending
			if pending ~= nil and pending.mode == mode and Refii.player_value(2, "coop.route.done", 0) < pending.id then
				return pending.on
			end
			return Refii.player_value(2, "coop.q.mode." .. tostring(mode), 0) ~= 0
		end

		C.BashKinds = { "MESSAGE_EVENT_INTERACTED_A_BUTTON", "MESSAGE_EVENT_INTERACTED_B_BUTTON",
			"MESSAGE_EVENT_INTERACTED_X_BUTTON", "MESSAGE_EVENT_INTERACTED_Y_BUTTON" }
		C.BashKind = {}
		for _, kind in ipairs(C.BashKinds) do
			if MSG[kind] ~= nil then
				C.BashKind[MSG[kind]] = true
			end
		end
		C.BashMessage = C.BashMessage or { GetID = C.Permanent(function() return 0 end, "BashMessage.GetID") }

		C.PlayerView = C.PlayerView or setmetatable({}, { __index = Player })
		C.PlayerView.IsAvailable = C.Permanent(function(entity, ...)
			return Player.IsAvailable(entity, ...) or C.StatedPlayer(entity)
		end, "PlayerView.IsAvailable")
		C.PlayerView.HasPlayerMode = C.Permanent(function(entity, mode, ...)
			if C.StatedPlayer(entity) then
				return C.StatedMode(entity, mode)
			end
			return Player.HasPlayerMode(entity, mode, ...)
		end, "PlayerView.HasPlayerMode")
		-- Player.Start/StopInteractionMode for a stated player travel on the route (Route.Player.*).
		C.PlayerView.StartInteractionMode = C.Permanent(function(entity, ...)
			if C.StatedPlayer(entity) then
				C.BashSeen = Refii.player_value(2, "coop.mode.bash", 0)
			end
			return Player.StartInteractionMode(entity, ...)
		end, "PlayerView.StartInteractionMode")
		C.PlayerView.StopInteractionMode = C.Permanent(function(entity, ...)
			return Player.StopInteractionMode(entity, ...)
		end, "PlayerView.StopInteractionMode")
		C.MessageView = C.MessageView or setmetatable({}, { __index = MessageEvents })
		C.MessageView.IsMessageSentBy = C.Permanent(function(kind, entity, ...)
			if kind ~= nil and C.BashKind[kind] and C.StatedPlayer(entity) then
				local bashes = Refii.player_value(2, "coop.mode.bash", 0)
				C.BashSeen = C.BashSeen or bashes
				if bashes > C.BashSeen then
					C.BashSeen = C.BashSeen + 1
					return true, C.BashMessage
				end
				return false
			end
			return MessageEvents.IsMessageSentBy(kind, entity, ...)
		end, "MessageView.IsMessageSentBy")
		C.PlayerEnv = C.PlayerEnv or setmetatable({}, { __index = _G, __newindex = _G })
		rawset(C.PlayerEnv, "Player", C.PlayerView)
		rawset(C.PlayerEnv, "MessageEvents", C.MessageView)
		for _, owner in ipairs({ { CombatStates, "BeetleSpit" }, { CombatStates, "BeetleHug" }, { CombatStates, "SpellCasterHeal" },
			{ CombatRegister, "IsValidTarget" } }) do
			local fn = type(owner[1]) == "table" and owner[1][owner[2]] or nil
			if type(fn) == "function" and setfenv ~= nil then
				pcall(setfenv, fn, C.PlayerEnv)
			end
		end
		-- The beetles' melee sequences hold their "target in interaction mode" test inside their
		-- IsValid (a file-local predicate): a stated player in interaction mode refuses them too.
		if type(CombatSequences) == "table" and EPlayerMode ~= nil then
			for _, name in ipairs({ "BeetleCloseAttack", "BeetleAttack" }) do
				local sequence = CombatSequences[name]
				if type(sequence) == "table" and type(sequence.IsValid) == "function" then
					if rawget(sequence, "RefiiShippedIsValid") == nil then
						rawset(sequence, "RefiiShippedIsValid", sequence.IsValid)
					end
					local shipped = rawget(sequence, "RefiiShippedIsValid")
					sequence.IsValid = C.Permanent(function(combat_info)
						if not shipped(combat_info) then
							return false
						end
						local target = type(combat_info) == "table" and combat_info.Target or nil
						return not (C.StatedPlayer(target) and C.StatedMode(target, EPlayerMode.PLAYER_MODE_INTERACTION))
					end, "Sequence." .. name .. ".IsValid")
				end
			end
		end

		-- This machine plays player two online and the host runs the world (Refii.world_here).
		function C.IsJoiner()
			if Refii == nil then
				return false
			end
			if Refii.world_here ~= nil then
				return not Refii.world_here()
			end
			return Refii.coop_mode ~= nil and Refii.coop_mode() == 3
		end

		-- The joiner's side, once a world frame (from the camera's FrameUpdate).
		C.ModeSent = C.ModeSent or {}
		C.ButtonLast = C.ButtonLast or {}
		function C.StatePlayerModes()
			if not C.IsJoiner() or Refii.player_set == nil or EPlayerMode == nil then
				return
			end
			local frame = Timing.GetWorldFrame()
			if C.ModeFrame == frame then
				return
			end
			C.ModeFrame = frame
			local hero = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if hero == nil or not hero:IsAlive() or not Player.IsAvailable(hero) then
				return
			end
			local function State(key, value)
				if C.ModeSent[key] ~= value then
					C.ModeSent[key] = value
					Refii.player_set(2, key, value)
				end
			end
			local interacting = Player.HasPlayerMode(hero, EPlayerMode.PLAYER_MODE_INTERACTION) == true
			-- The stated queries (THE ROUTE): the host answers its Player / GUI questions about this hero
			-- from these.
			C.StateQueries(hero, State)
			if interacting then
				if not C.WasInteracting then
					local newest = MessageEvents.GetMostRecentMessageID()
					for _, kind in ipairs(C.BashKinds) do
						C.ButtonLast[kind] = newest
					end
				end
				for _, kind in ipairs(C.BashKinds) do
					if MSG[kind] ~= nil then
						local posted, message = MessageEvents.IsMessageSentBy(MSG[kind], hero, C.ButtonLast[kind])
						if posted then
							C.ButtonLast[kind] = message:GetID()
							C.Bashes = (C.Bashes or 0) + 1
						end
					end
				end
				State("coop.mode.bash", C.Bashes or 0)
			end
			C.WasInteracting = interacting
		end

		-- ============================================================================
		-- THE ROUTE: PLAYER TWO'S BUSINESS ON PLAYER TWO'S SCREEN. Online, the host runs the world —
		-- the AI, the cutscenes, the quests — for both heroes, so code acting for player two calls
		-- the GUI, camera and Player natives on the HOST, and those act on what the host shows:
		--   * the boxes (`GUI.DisplayMessageBox` `sub_823027B0`, `GUI.DisplayYesNoQuestion`
		--     `sub_823044A8`) take no player: each builds its box and pushes it on the one box queue
		--     of this machine's GUI (`sub_826B99D8(*(*(GUI+28)+52) + 4, box)` at 0x82302824 /
		--     0x82304520). A question's answer is a MESSAGE posted on this machine's bus when the box
		--     is answered (`sub_823CF428`: type 36, extra data 1 yes / 0 no, `sub_82279B80` at
		--     0x823CF4E4), which GUI.AskYesNoQuestion and its kin (miscfunctions.lua) poll each
		--     frame with MessageEvents.IsMessagePosted. Shown on the host, player one sees and
		--     answers player two's box, and the waiting script takes player one's answer.
		--   * TutorialManager.DisplayTutorial (`sub_821B5260`) queues on this machine's tutorial
		--     manager; FadeScreenOut/In (`sub_82305AA0` / `sub_82305C08`) fade this machine's screen
		--     (and the host's is sent on to the joiner as well, `sub_82348678` at 0x82305B30);
		--     SetDPadEnabled / SetDPadCenter / SetHUDSuggestionMode write this machine's HUD
		--     (`*(GUI+32)+56`, `sub_82305FC8`, `sub_82306180`, `sub_82306508`).
		--   * ScriptFunction.SetScriptedHeroCamera / ClearScriptedHeroCamera act on
		--     CameraManager.GetCurrentCamera(), the LOCAL player's camera (`sub_8245DB40` →
		--     `sub_821F87C8`: record 2 on a joiner, else 1); SetICSCam / ClearICSCam set
		--     SACCamera.IsICSActive and SACCamera.AddInterestingEntity this VM's camera list (Lua);
		--     SetHeroLookTarget makes GetPlayerHero() look, and player two's body is played on the
		--     joiner.
		--   * Player.* on the host's copy of player two do nothing: the binding calls the method only
		--     when the entity carries component 33 (`sub_823FB4D8`: `(entity+40 >> 1) & 1` at
		--     0x823FB520), and that copy has none.
		-- Each such call made while the code acts for player two (C.Acting), or a Player.* call
		-- naming player two's copy, is sent to the joiner on the ROUTE (core/event_relay.h ps.r:
		-- ordered, acknowledged, exactly once) and run there through the same function, as player
		-- two, on player two's own hero, GUI and camera: the box looks and behaves exactly as player
		-- one's. A question waits for the answer the joiner's box gave and returns it as the shipped
		-- function would have. Player queries about player two are answered from what the joiner
		-- states each frame in the player store (coop.q.*). Local split screen: the box is built
		-- here and seated in player two's half, on player two's pad (Refii._ui_seat,
		-- display_box_hooks.cpp). Mods get all of it by acting as a player (Refii.run_as_player), or
		-- ask outright: Refii.show_to_player(p, "GUI.DisplayMessageBox", ...).
		--
		-- A LINE (the Lua's own text; no ';', '|' or newline):
		--   c<id>,<player>,<mode>,<name>,<arg>...   a call for <player>; mode c command, q question
		--                                           (answered), h a handle the host keeps a token for
		--   a<id>,<value>...                        a question's answer
		-- A value: _ nil · b0/b1 · n<number> · s<text, %XX-escaped> · p1/p2 a hero · u<uid> an entity
		-- (Refii.uid, the same on both machines) · t<id> a handle token · c the question's caller (a
		-- fresh one on the machine that asks).
		-- ============================================================================
		-- A question waits through the frozen trampolines (C.Call1 / C.Wait1, at the top).
		C.RouteNext = C.RouteNext or 0
		C.RouteOpen = C.RouteOpen or {}
		C.RouteAnswers = C.RouteAnswers or {}
		C.RouteHandles = C.RouteHandles or {}
		C.RouteThreads = C.RouteThreads or {}
		C.RouteShipped = C.RouteShipped or {}
		C.RouteSaid = C.RouteSaid or {}
		C.RouteDone = C.RouteDone or 0

		-- What travels, by function. seat: a box built here is seated for its player on split screen.
		-- answer: what a routed command returns on the sending machine (the shipped function's own
		-- constant answer). query: a stated query, answered from the store.
		C.RouteSpecs = C.RouteSpecs or {
			["GUI.DisplayMessageBox"] = { mode = "c", seat = true },
			["GUI.DisplayReceivedMoney"] = { mode = "c" },
			["GUI.DisplayReceivedItem"] = { mode = "c" },
			["TutorialManager.DisplayTutorial"] = { mode = "c", answer = { true } },
			["GUI.AskYesNoQuestion"] = { mode = "q", caller = 2, default = { false } },
			["GUI.AskSetQuestPrimaryQuestion"] = { mode = "q", caller = 2, default = { false } },
			["GUI.AskForAmount"] = { mode = "q", caller = 1, default = { 0, false } },
			["GUI.ShowMenuBox"] = { mode = "q", caller = 1, default = { 0 } },
			["GUI.ShowMenuBoxNoCancel"] = { mode = "q", caller = 1, default = { 0 } },
			["GUI.DisplayYesNoQuestion"] = { seat = true },
			["GUI.DisplayQuestPrimaryQuestion"] = { seat = true },
			["GUI.DisplayNumericAdjusterParams"] = { seat = true },
			["GUI.DisplayMenuBox"] = { seat = true },
			["GUI.DisplayUncancelableMenuBox"] = { seat = true },
			["GUI.FadeScreenOut"] = { mode = "c" },
			["GUI.FadeScreenIn"] = { mode = "c" },
			["GUI.SetDPadEnabled"] = { mode = "c" },
			["GUI.SetDPadCenter"] = { mode = "c" },
			["GUI.SetHUDSuggestionMode"] = { mode = "c" },
			["GUI.AddWorldIcon"] = { mode = "c" },
			["GUI.RemoveWorldIcon"] = { mode = "c" },
			["GUI.IsQuickMenuOpen"] = { query = "coop.q.GUI.IsQuickMenuOpen" },
			["GUI.IsScreenFading"] = { query = "coop.q.GUI.IsScreenFading" },
			["ScriptFunction.SetScriptedHeroCamera"] = { mode = "c" },
			["ScriptFunction.ClearScriptedHeroCamera"] = { mode = "c" },
			["ScriptFunction.SetICSCam"] = { mode = "c", answer = { true } },
			["ScriptFunction.ClearICSCam"] = { mode = "c" },
			["ScriptFunction.SetHeroLookTarget"] = { mode = "h" },
			["SACCamera.AddInterestingEntity"] = { mode = "c" },
			["SACCamera.RemoveInterestingEntity"] = { mode = "c" },
		}

		-- Player queries the joiner states for its hero (StateQueries): by an enum argument, or none.
		C.RouteQueries = C.RouteQueries or { HasPlayerMode = "mode", IsInteractiveCutsceneRuleSet = "rule",
			GetOverrideCameraMode = "cam", IsSafetyModeEnabled = true, IsCutsceneInEffect = true, CanSleep = true,
			IsWearingCondom = true, IsInFirstPerson = true }
		-- Player functions that never travel: the availability test, and the combat state the store
		-- already carries (C.CombatApply).
		C.RouteKeep = C.RouteKeep or { IsAvailable = true, SetMidstOfCombatCounter = true, SetCloseToCombat = true,
			SetInAnyCombat = true }

		function C.WorldHere()
			if Refii == nil then
				return true
			end
			if Refii.world_here ~= nil then
				return Refii.world_here() == true
			end
			return Refii.coop_mode == nil or Refii.coop_mode() ~= 3
		end

		-- An online partner stands (either side).
		function C.RoutePeerStands()
			local mode = Refii ~= nil and Refii.coop_mode ~= nil and Refii.coop_mode() or 0
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return (mode == 2 or mode == 3) and two ~= nil and two:IsAlive()
		end

		-- Whether player p is played on the other machine (and the route can reach it).
		function C.RouteRemote(p)
			return Refii ~= nil and Refii._route_post ~= nil and Refii.player_here ~= nil and C.RoutePeerStands()
				and not Refii.player_here(p)
		end

		-- The player a call made now is for, when this machine runs the world and that player is
		-- played on the other one: player two while the code acts for them; nil otherwise. The host's
		-- silent threads (player two's bed) show nothing anywhere.
		function C.RouteActing()
			local co = coroutine.running()
			if C.CoGet(co, "silent") then
				return nil
			end
			local two = C.Acting()
			if two == nil or not two:IsAlive() or not C.WorldHere() or not C.RouteRemote(2) then
				return nil
			end
			return 2
		end

		-- Local co-op on split screen, acting for player two: their boxes stand in their half.
		function C.SeatActing()
			if Refii == nil or Refii._ui_seat == nil or Refii.split_screen == nil or Refii.coop_mode == nil
				or Refii.coop_mode() ~= 1 or not Refii.split_screen() then
				return nil
			end
			return C.Acting() ~= nil and 2 or nil
		end

		local function Escape(text)
			return (string.gsub(text, "[%%,;|\r\n]", function(c) return string.format("%%%02X", string.byte(c)) end))
		end
		local function Unescape(text)
			return (string.gsub(text, "%%(%x%x)", function(h) return string.char(tonumber(h, 16)) end))
		end
		local function Split(text)
			local fields = {}
			local at = 1
			while true do
				local comma = string.find(text, ",", at, true)
				if comma == nil then
					fields[#fields + 1] = string.sub(text, at)
					return fields
				end
				fields[#fields + 1] = string.sub(text, at, comma - 1)
				at = comma + 1
			end
		end

		-- A value as a token, or nil when it cannot travel.
		function C.RouteEncode(value)
			local kind = type(value)
			if value == nil then
				return "_"
			elseif kind == "boolean" then
				return value and "b1" or "b0"
			elseif kind == "number" then
				return "n" .. string.format("%.9g", value)
			elseif kind == "string" then
				return "s" .. Escape(value)
			elseif kind == "table" and rawget(value, "RefiiRouteToken") ~= nil then
				return "t" .. tostring(rawget(value, "RefiiRouteToken"))
			elseif kind == "userdata" and Refii ~= nil and Refii.handle ~= nil then
				local handle = Refii.handle(value)
				if handle == nil then
					return nil
				end
				if value == C.ShippedGetPlayerHero() then
					return "p1"
				end
				local two = C.ShippedGetPlayerHenchman()
				if two ~= nil and value == two then
					return "p2"
				end
				local uid = Refii.uid(handle)
				return uid ~= nil and ("u" .. uid) or nil
			end
			return nil
		end

		-- A token's value here; false second when it names something this machine does not have.
		function C.RouteDecode(token, caller)
			local tag, rest = token:sub(1, 1), token:sub(2)
			if token == "_" or token == "" then
				return nil, true
			elseif tag == "b" then
				return rest == "1", true
			elseif tag == "n" then
				return tonumber(rest), true
			elseif tag == "s" then
				return Unescape(rest), true
			elseif tag == "p" then
				local hero = rest == "2" and C.ShippedGetPlayerHenchman() or C.ShippedGetPlayerHero()
				return hero, hero ~= nil
			elseif tag == "u" then
				local handle = Refii._by_uid ~= nil and Refii._by_uid(rest) or nil
				local entity = handle ~= nil and Refii.entity(handle) or nil
				return entity, entity ~= nil
			elseif tag == "t" then
				return C.RouteHandles[tonumber(rest)], true
			elseif tag == "c" then
				return caller, true
			end
			return nil, false
		end

		-- Sends a call for player p; its id, or nil when an argument cannot travel (the call then
		-- runs here, as shipped).
		function C.RouteSend(p, mode, name, caller, n, ...)
			local parts = { "c" .. tostring(C.RouteNext + 1), tostring(p), mode, name }
			for i = 1, n do
				local token = i == caller and "c" or C.RouteEncode((select(i, ...)))
				if token == nil then
					if not C.RouteSaid[name] then
						C.RouteSaid[name] = true
						print("[refii] route: " .. name .. " has an argument that cannot travel; shown here")
					end
					return nil
				end
				parts[#parts + 1] = token
			end
			local line = table.concat(parts, ",")
			if #line > 700 then
				return nil
			end
			C.RouteNext = C.RouteNext + 1
			Refii._route_post(line)
			return C.RouteNext
		end

		-- A question's wait (C.Wait1): the answer the other machine sent, else — the partner gone, or
		-- a ticket this session never opened (a save loaded mid-question) — the shipped refusal.
		C.RouteAnswerPoll = C.Permanent(function(state)
			if type(state) ~= "table" or C.RouteOpen[state.id] == nil or C.RouteOpen[state.id] ~= state.ticket then
				return true, state ~= nil and state.default or {}
			end
			local answer = C.RouteAnswers[state.id]
			if answer ~= nil then
				C.RouteAnswers[state.id] = nil
				C.RouteOpen[state.id] = nil
				return true, answer
			end
			if not C.RoutePeerStands() then
				C.RouteOpen[state.id] = nil
				return true, state.default
			end
			return false
		end, "RouteAnswerPoll")
		C.RouteUnpack = C.Permanent(function(state, values)
			if type(values) ~= "table" then
				return nil
			end
			return unpack(values, 1, values.n or table.maxn(values))
		end, "RouteUnpack")

		-- A handle the other machine holds (a look): its Clear goes there.
		C.RouteHandleClear = C.Permanent(function(self)
			if type(self) == "table" and rawget(self, "RefiiRouteToken") ~= nil and Refii ~= nil and Refii._route_post ~= nil then
				Refii._route_post("c0," .. tostring(rawget(self, "RefiiRoutePlayer") or 2) .. ",c,@clear,t"
					.. tostring(rawget(self, "RefiiRouteToken")))
			end
		end, "RouteHandleClear")

		-- A routed command's or handle's result here, or the question's state (sent, not yet waited
		-- for). nil when nothing was sent.
		function C.RouteOut(p, name, spec, ...)
			local mode = spec.mode or "c"
			if mode == "q" and coroutine.running() == nil then
				return nil
			end
			local id = C.RouteSend(p, mode, name, spec.caller, select("#", ...), ...)
			if id == nil then
				return nil
			end
			if mode == "q" then
				local ticket = {}
				C.RouteOpen[id] = ticket
				return { id = id, ticket = ticket, default = spec.default or {} }
			elseif mode == "h" then
				return { RefiiRouteToken = id, RefiiRoutePlayer = p, Clear = C.RouteHandleClear }
			end
			return spec.answer or {}
		end

		-- Wraps one routed function (by its path; idempotent).
		function C.RouteWrap(path, spec)
			local ns, method = string.match(path, "^(%w+)%.(%w+)$")
			local class = ns ~= nil and _G[ns] or nil
			if type(class) ~= "table" or type(class[method]) ~= "function" then
				return
			end
			C.Wrap(ns, method, "Route", function(shipped)
				C.RouteShipped[path] = shipped
				return function(...)
					if spec.query ~= nil and C.RouteActing() ~= nil then
						local stated = Refii.player_value(2, spec.query, -1)
						if stated >= 0 then
							return stated ~= 0
						end
					elseif spec.mode ~= nil and C.RouteActing() ~= nil then
						local out = C.RouteOut(2, path, spec, ...)
						if out ~= nil and spec.mode == "q" then
							return C.Call1(C.RouteUnpack, nil, C.Wait1, C.RouteAnswerPoll, out, coroutine.yield)
						elseif out ~= nil and spec.mode == "h" then
							return out
						elseif out ~= nil then
							return unpack(out)
						end
					end
					local seat = spec.seat and C.SeatActing() or nil
					if seat == nil then
						return shipped(...)
					end
					-- A box-building native (never yields): its boxes are the seat's.
					Refii._ui_seat(seat)
					local results = { pcall(shipped, ...) }
					Refii._ui_seat(0)
					if not results[1] then
						error(results[2], 0)
					end
					return unpack(results, 2, table.maxn(results))
				end
			end, class)
		end

		-- Player two's copy on the host: a stated player whose machine is the joiner.
		function C.RemotePlayer(entity)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if entity == nil or two == nil or entity ~= two or not C.WorldHere() or not C.RouteRemote(2) then
				return false
			end
			local co = coroutine.running()
			return not C.CoGet(co, "silent")
		end

		-- A stated Player query about the remote player two (nil when not yet stated: as shipped).
		function C.StatedQuery(name, kind, arg)
			if name == "HasPlayerMode" then
				return C.StatedMode(nil, arg)
			end
			local key = kind == true and ("coop.q." .. name) or ("coop.q." .. kind .. "." .. tostring(arg))
			local value = Refii.player_value(2, key, -1)
			if value < 0 then
				return nil
			end
			if name == "GetOverrideCameraMode" then
				return value
			end
			return value ~= 0
		end

		function C.RouteWrapPlayer()
			if type(Player) ~= "table" then
				return
			end
			for _, method in ipairs(C.OwnMethods(Player)) do
				if not C.RouteKeep[method] then
					local query = C.RouteQueries[method]
					local asks = string.match(method, "^Is") or string.match(method, "^Get") or string.match(method, "^Has")
						or string.match(method, "^Can")
					C.Wrap("Player", method, "Route", function(shipped)
						C.RouteShipped["Player." .. method] = shipped
						return function(entity, ...)
							if not C.RemotePlayer(entity) then
								return shipped(entity, ...)
							end
							if query ~= nil then
								return C.StatedQuery(method, query, (...))
							end
							if not asks then
								local id = C.RouteSend(2, "c", "Player." .. method, nil, select("#", ...) + 1, entity, ...)
								if id ~= nil then
									if (method == "StartInteractionMode" or method == "StopInteractionMode") and EPlayerMode ~= nil then
										C.ModePending = { mode = EPlayerMode.PLAYER_MODE_INTERACTION,
											on = method == "StartInteractionMode", id = id }
									end
									return
								end
							end
							return shipped(entity, ...)
						end
					end, Player)
				end
			end
		end

		-- Every routed function, wrapped now; the camera chunk wraps SACCamera's (`only`), a table the
		-- camera scripts rebuild each time they run.
		function C.RouteWrapAll(only)
			for path, spec in pairs(C.RouteSpecs) do
				if only == nil or string.sub(path, 1, #only + 1) == only .. "." then
					C.RouteWrap(path, spec)
				end
			end
			if only == nil then
				C.RouteWrapPlayer()
			end
		end

		-- THE RECEIVING SIDE. A call runs in a thread of its own, as its player, through the function
		-- as the route found it (not re-routed); threads resume once a world frame, oldest first, so
		-- the calls start in the order they were made and a question waits without holding the rest.
		C.RouteRun = C.Permanent(function(hero, fn, args)
			return C.RunAs(hero, fn, unpack(args, 1, args.n))
		end, "RouteRun")

		-- The function a path names: as the route found it (unwrapped), or as it stands when `live`.
		function C.RouteFunction(name, live)
			local shipped = not live and C.RouteShipped[name] or nil
			if shipped ~= nil then
				return shipped
			end
			local value = _G
			for part in string.gmatch(name, "[^%.]+") do
				if type(value) ~= "table" then
					return nil
				end
				value = value[part]
			end
			return type(value) == "function" and value or nil
		end

		function C.RouteAnswer(id, results)
			local parts = { "a" .. tostring(id) }
			for i = 1, results.n or 0 do
				parts[#parts + 1] = C.RouteEncode(results[i]) or "_"
			end
			Refii._route_post(table.concat(parts, ","))
		end

		function C.RouteStep(thread, ...)
			local results = { n = select("#", ...) - 1, select(2, ...) }
			if not (...) then
				print("[refii] route: " .. tostring(thread.name) .. " failed here: " .. tostring(results[1]))
				if thread.mode == "q" then
					C.RouteAnswer(thread.id, { n = 0 })
				end
				return true
			end
			if coroutine.status(thread.co) ~= "dead" then
				return false
			end
			if thread.mode == "q" then
				C.RouteAnswer(thread.id, results)
			elseif thread.mode == "h" then
				C.RouteHandles[thread.id] = results[1]
			end
			return true
		end

		function C.RouteArrive(line)
			local kind = line:sub(1, 1)
			local fields = Split(line:sub(2))
			local id = tonumber(fields[1])
			if id == nil then
				return
			end
			if kind == "a" then
				if C.RouteOpen[id] ~= nil then
					local values = { n = #fields - 1 }
					for i = 2, #fields do
						values[i - 1] = (C.RouteDecode(fields[i]))
					end
					C.RouteAnswers[id] = values
				end
				return
			end
			if kind ~= "c" or #fields < 4 then
				return
			end
			local player, mode, name = tonumber(fields[2]), fields[3], fields[4]
			if id > C.RouteDone then
				C.RouteDone = id
			end
			if name == "@clear" then
				local token = tonumber((fields[5] or ""):sub(2))
				local handle = token ~= nil and C.RouteHandles[token] or nil
				C.RouteHandles[token or -1] = nil
				if handle ~= nil and type(handle.Clear) == "function" then
					pcall(handle.Clear, handle)
				end
				return
			end
			local fn = C.RouteFunction(name)
			local hero = player == 2 and C.ShippedGetPlayerHenchman() or C.ShippedGetPlayerHero()
			local recent = MessageEvents.GetMostRecentMessageID()
			local caller = { LastMessageID_YesNoQuestionReply = recent, LastPosted = recent, LastMessageID_MenuBox = recent,
				LastMessageID_ButtonBox = recent }
			local args = { n = #fields - 4 }
			for i = 5, #fields do
				local value, ok = C.RouteDecode(fields[i], caller)
				if not ok then
					fn = nil
				end
				args[i - 4] = value
			end
			if fn == nil or hero == nil then
				if not C.RouteSaid[name] then
					C.RouteSaid[name] = true
					print("[refii] route: " .. tostring(name) .. " cannot run here (no such function, hero or entity)")
				end
				if mode == "q" then
					C.RouteAnswer(id, { n = 0 })
				end
				return
			end
			local thread = { co = coroutine.create(C.RouteRun), id = id, mode = mode, name = name }
			if not C.RouteStep(thread, coroutine.resume(thread.co, hero, fn, args)) then
				C.RouteThreads[#C.RouteThreads + 1] = thread
			end
		end

		-- Once a world frame on either machine (the camera's FrameUpdate).
		function C.RouteTick()
			if Refii == nil or Refii._route_take == nil then
				return
			end
			local frame = Timing.GetWorldFrame()
			if C.RouteFrame == frame then
				return
			end
			C.RouteFrame = frame
			local waiting = C.RouteThreads
			C.RouteThreads = {}
			for _, thread in ipairs(waiting) do
				if not C.RouteStep(thread, coroutine.resume(thread.co)) then
					C.RouteThreads[#C.RouteThreads + 1] = thread
				end
			end
			for n = 1, 32 do
				local line = Refii._route_take()
				if line == nil then
					break
				end
				C.RouteArrive(line)
			end
			if C.IsJoiner() and Refii.player_set ~= nil and C.RouteDoneSaid ~= C.RouteDone then
				C.RouteDoneSaid = C.RouteDone
				Refii.player_set(2, "coop.route.done", C.RouteDone)
			end
		end

		-- The joiner states what the host asks about its hero (from StatePlayerModes, once a world
		-- frame): every player mode, cutscene rule and camera-override scope, the plain queries, and
		-- the GUI's quick menu and fade.
		function C.StateQueries(hero, State)
			local function Flag(value)
				return value and 1 or 0
			end
			for _, v in pairs(EPlayerMode or {}) do
				State("coop.q.mode." .. tostring(v), Flag(Player.HasPlayerMode(hero, v)))
			end
			for _, v in pairs(EInteractiveCutsceneRule or {}) do
				if v ~= 0 then
					State("coop.q.rule." .. tostring(v), Flag(Player.IsInteractiveCutsceneRuleSet(hero, v)))
				end
			end
			for _, v in pairs(EInteractiveCutsceneRuleScope or {}) do
				State("coop.q.cam." .. tostring(v), Player.GetOverrideCameraMode(hero, v) or 0)
			end
			for name, kind in pairs(C.RouteQueries) do
				if kind == true and type(Player[name]) == "function" then
					State("coop.q." .. name, Flag(Player[name](hero)))
				end
			end
			State("coop.q.GUI.IsQuickMenuOpen", Flag(GUI.IsQuickMenuOpen()))
			State("coop.q.GUI.IsScreenFading", Flag(GUI.IsScreenFading()))
		end

		C.RouteWrapAll()

		-- ============================================================================
		-- THE PLAYER-AGNOSTIC PRIMITIVES, the mod API's core: the co-op layer above and the mods
		-- use the same calls (the Godot and native API state the same names). Refii.player_for is
		-- native; these are defined here only where no native of the name exists.
		--   Refii.acting_player()            1 or 2: who GetPlayerHero() means right now.
		--   Refii.run_as_player(p, fn, ...)  fn with GetPlayerHero()/GetPlayerHenchman()/
		--                                    QuestManager.HeroEntity as player p.
		--   Refii.combat_state(p)            midst, close_to_combat, in_any_combat for player p.
		--   Refii.show_to_player(p, name, ...) the function `name` ("GUI.DisplayMessageBox", any global
		--                                    path) run for player p, on player p's screen: through THE
		--                                    ROUTE when p is played on the other machine (a question
		--                                    returns the answer p gave), else here as p (split screen:
		--                                    p's half).
		-- ============================================================================
		if Refii ~= nil then
			if Refii.acting_player == nil then
				Refii.acting_player = C.Permanent(function()
					return C.Acting() ~= nil and 2 or 1
				end, "Api.acting_player")
			end
			if Refii.run_as_player == nil then
				Refii.run_as_player = C.Permanent(function(player, fn, ...)
					local hero = player == 2 and C.ShippedGetPlayerHenchman() or C.ShippedGetPlayerHero()
					return C.RunAs(hero, fn, ...)
				end, "Api.run_as_player")
			end
			if Refii.combat_state == nil then
				Refii.combat_state = C.Permanent(function(player)
					local p = player == 2 and 2 or 1
					if p == 2 and C.IsJoiner() then
						return Refii.player_value(2, "coop.combat.midst", 0), Refii.player_value(2, "coop.combat.close", 0) ~= 0,
							Refii.player_value(2, "coop.combat.any", 0) ~= 0
					end
					local last = C.CombatLast ~= nil and C.CombatLast[p] or nil
					if last == nil then
						return 0, false, false
					end
					return last.midst or 0, last.close == true, last.any == true
				end, "Api.combat_state")
			end
			if Refii.show_to_player == nil then
				Refii.show_to_player = C.Permanent(function(player, name, ...)
					local p = player == 2 and 2 or 1
					local hero = p == 2 and C.ShippedGetPlayerHenchman() or C.ShippedGetPlayerHero()
					if hero == nil or type(name) ~= "string" then
						return nil
					end
					local spec = C.RouteSpecs[name] or {}
					if C.RouteRemote(p) then
						if spec.query ~= nil then
							local stated = Refii.player_value(p, spec.query, -1)
							return stated >= 0 and stated ~= 0 or nil
						end
						if spec.mode == nil then
							spec = { mode = "c" }
						end
						local out = C.RouteOut(p, name, spec, ...)
						if out == nil then
							return nil
						elseif spec.mode == "q" then
							return C.Call1(C.RouteUnpack, nil, C.Wait1, C.RouteAnswerPoll, out, coroutine.yield)
						elseif spec.mode == "h" then
							return out
						end
						return unpack(out)
					end
					local fn = C.RouteFunction(name, true)
					if fn == nil then
						return nil
					end
					return C.RunAs(hero, fn, ...)
				end, "Api.show_to_player")
			end
		end

		-- THE HENCHMAN TUTORIALS are retired: the teleport hint (free roam replaces the leash) and
		-- the multiplayer introduction (it explains the henchman and the share sliders). The
		-- orb, join, camera-snap and joint-expression tutorials stay.
		if TutorialManager ~= nil and TutorialManager.CompleteTutorial ~= nil and ETutorialType ~= nil then
			TutorialManager.CompleteTutorial(ETutorialType.TUTORIAL_MULTIPLAYER_TELEPORT)
			TutorialManager.CompleteTutorial(ETutorialType.TUTORIAL_MULTIPLAYER)
		end
		""";

	/// <summary>
	/// The camera half, run after the camera scripts each time they run (a session's first level,
	/// a save load, a co-op join: each rebuilds SACCamera and CameraFunctions, so every wrap is
	/// re-applied when the table no longer holds it).
	/// </summary>
	/// <remarks>
	/// <para>EACH PLAYER THEIR OWN CAMERA, ONLINE. Shipped, the joiner's camera ran no Lua and
	/// followed the host's (net message 0xBD); now each machine runs its own (coop_hero_hooks.cpp)
	/// and frames only its own hero: <c>CameraFunctions.IsMultiplayer</c> — "henchman alive", the
	/// test behind the two-player cage (SACCamera:CalculatePrimaryAndSecondary) and the
	/// multiplayer camera set (CameraManager.CameraSetList, which captured the function, so its
	/// entry is repointed) — answers false in an online game.</para>
	/// <para>FREE ROAM. The leash is the movement filter sub_821A0298, skipped while the game's own
	/// <c>Debug.SetMultiPlayerHeroFree</c> byte is set (0x821a0334); the camera re-imposes its
	/// distance every frame in <c>SACCamera:SetMaxCoopDistance</c>. That method now states the
	/// switch every frame: free (and no distance) where players roam freely — online — else the
	/// shipped leash, which a shared screen needs. Local co-op on split screen counts as "own
	/// cameras" too: player two's manager runs the same scripts with player two as its player.
	/// After free roam ends with the heroes beyond the leash's distance they stay free until they
	/// are within it (<c>C.Reuniting</c>): the leash holds every step that ends beyond it.</para>
	/// </remarks>
	public const string CameraKey = "refii/core_camera.lua";

	private const string CameraSource = """
		-- The payload also runs this right after the startup scripts (the disc build loads its
		-- camera files there); on the Title Update the camera scripts have not run yet.
		if SACCamera == nil or CameraFunctions == nil then
			return
		end

		RefiiCoop = RefiiCoop or {}
		local C = RefiiCoop

		-- The core chunk's permanent registration, in case this runs first (the disc build).
		C.Permanent = C.Permanent or function(fn, name)
			if AddToPermanentsTables ~= nil then
				AddToPermanentsTables(fn, "Refii." .. name)
			end
			return fn
		end

		-- Every player follows their own hero: an online game, or local co-op on split screen
		-- (player two's camera manager is built by coop_hero_hooks.cpp; Refii.split_screen() is
		-- split IN EFFECT: the host's coop_split_screen switch AND player two's Player built with
		-- that camera — a split chosen after the drop-in keeps the shared rules until it is).
		-- Online is "a partner and not local co-op" (game+284 1 vs 2, `sub_82340300`) on BOTH machines:
		-- the shipped IsMultiplayerGame (`sub_8245B410`) answers only on the session HOST
		-- (CM+52 && CM+53), so a joiner read it false and framed both heroes in one camera.
		function C.OwnCameras()
			if Multiplayer ~= nil and Multiplayer.IsLocalCoopGame ~= nil and Multiplayer.IsLocalCoopGame() == true then
				return Refii ~= nil and Refii.split_screen ~= nil and Refii.split_screen() == true
			end
			local partner = GetPlayerHenchman ~= nil and GetPlayerHenchman() or nil
			return partner ~= nil and partner:IsAlive() == true
		end

		function C.FreeRoam()
			return C.OwnCameras()
		end

		-- A CAMERA'S LOOK-AT IS ITS OWN PLAYER'S. CameraFunctions.GetSafeLookAtButtonDown(player)
		-- ignores its player: it answers from hero one (gated on Player.IsAvailable(GetPlayerHero()))
		-- or'd with the henchman — one shared camera. On a joiner hero one is the host's copy, with
		-- no Player component, so player two's held trigger never reached their camera (no target
		-- lock, no look-at). With a camera each, it is the camera's own player's button.
		if CameraFunctions.GetSafeLookAtButtonDown ~= C.SafeLookAtButtonDown then
			local shipped = CameraFunctions.GetSafeLookAtButtonDown
			C.SafeLookAtButtonDown = C.Permanent(function(player, ...)
				if C.OwnCameras() then
					return player ~= nil and Player.IsAvailable(player) and Player.GetLookAtButtonDown(player) == true
				end
				return shipped(player, ...)
			end, "SafeLookAtButtonDown")
			CameraFunctions.GetSafeLookAtButtonDown = C.SafeLookAtButtonDown
		end

		if CameraFunctions.IsMultiplayer ~= C.IsMultiplayer then
			local shipped = CameraFunctions.IsMultiplayer
			C.IsMultiplayer = C.Permanent(function(...)
				if C.OwnCameras() then
					return false
				end
				return shipped(...)
			end, "IsMultiplayer")
			CameraFunctions.IsMultiplayer = C.IsMultiplayer
			if CameraManager ~= nil and type(CameraManager.CameraSetList) == "table" then
				for _, set in ipairs(CameraManager.CameraSetList) do
					if type(set) == "table" and set.IsValid == shipped then
						set.IsValid = C.IsMultiplayer
					end
				end
			end
		end

		-- EACH CAMERA FRAMES ITS OWN PLAYER'S FIGHT. With UseNewNearbyFoeSearch (set on both
		-- machines) the cage takes its foes from CameraManager.GetFoes (`sub_82484428`), which names
		-- no player: with hero two alive it searches around the MIDPOINT of the two heroes, widened by
		-- half their separation, for hero one's foes — so either player's fight pulled both cameras
		-- into combat framing. The script's other branch searches around the camera's own player
		-- (SearchTools.FilterFoesForCombatCamera(..., self.Player, ...)); with a camera each, that
		-- is the one taken.
		if SACCamera.CalculateNearbyFoes ~= nil and SACCamera.CalculateNearbyFoes ~= C.CalculateNearbyFoes then
			local shipped = SACCamera.CalculateNearbyFoes
			C.CalculateNearbyFoes = C.Permanent(function(self, ...)
				if not C.OwnCameras() or not SACCamera.UseNewNearbyFoeSearch then
					return shipped(self, ...)
				end
				SACCamera.UseNewNearbyFoeSearch = false
				local results = { pcall(shipped, self, ...) }
				SACCamera.UseNewNearbyFoeSearch = true
				if not results[1] then
					error(results[2], 0)
				end
				return unpack(results, 2, table.maxn(results))
			end, "CalculateNearbyFoes")
			SACCamera.CalculateNearbyFoes = C.CalculateNearbyFoes
		end

		-- THE MOD API'S CAMERA (refii_mod.h refii_mod_camera_*), stated around every
		-- CameraManager:FrameUpdate(hero) below.
		--   THE MODE. FrameUpdate activates the first valid set's best camera through
		--   self.ActivateCamera(camera) (rex_lhCameraManager_ActivateCamera: a Lua table, no mode) and keeps
		--   it as self.CurrentCamera. Its mode is the key the override set (OverridesCameraValidity) lists
		--   it under in CameraModes, which names every camera the manager activates; -1 for one it does
		--   not list. Refii._camera_stated raises camera_changed where it differs.
		--   THE HOLD. CameraManager.SetCameraOverride(hero, mode, scope, data) is the game's per-player
		--   hold (the quests' first-person shots). Each half of the hero's record (Normal; LookAt, what a
		--   held LT frames) that holds nobody else's override (CameraEnum DEFAULT) is claimed with the
		--   hold's data; a half someone else overrode is theirs, and is claimed again once it is DEFAULT
		--   (their release, OnLevelLoad's NewOverrideDataForPlayer, LoadFromSave). Released while a canned
		--   action's camera is switched on (the normal set lists CannedActionCamera and
		--   CombatCannedActionCamera before FirstPersonCamera; IsValid answers their Active flags) and, with
		--   the aim flag, while RangedCombatCamera.IsValid(hero); the host answers no hold during a FULL
		--   cutscene. hold.Written[half] is the data table written there, while the native mode word
		--   (Player.SetOverrideCameraMode's +0xC4 / +0xC8) is still ours; Refii._camera_held states those
		--   halves, so the cutscene event leaves our LookAt out.
		C.CameraModes = C.CameraModes or {}
		C.Holds = C.Holds or {}
		C.CameraHalves = { { "Normal", 1 }, { "LookAt", 2 } }

		function C.CameraModeOf(camera)
			if camera ~= nil and type(CameraManager.CameraSetList) == "table" then
				for _, set in ipairs(CameraManager.CameraSetList) do
					if type(set) == "table" and set.OverridesCameraValidity and type(set.CameraModes) == "table" then
						for mode, listed in pairs(set.CameraModes) do
							if listed == camera then
								return mode
							end
						end
					end
				end
			end
			return -1
		end

		function C.CameraStated(hero, camera)
			local key = GetIDFromEntity(hero)
			local mode = C.CameraModeOf(camera)
			if key ~= nil and C.CameraModes[key] ~= mode and Refii ~= nil and Refii._camera_stated ~= nil then
				C.CameraModes[key] = mode
				Refii._camera_stated(hero, mode)
			end
		end

		-- The scope covering the halves flagged in bits (1 Normal, 2 LookAt).
		function C.CameraScope(bits)
			if bits == 3 then
				return EInteractiveCutsceneRuleScope.CUTSCENE_RULE_SCOPE_ALWAYS
			elseif bits == 1 then
				return EInteractiveCutsceneRuleScope.CUTSCENE_RULE_SCOPE_NORMAL
			end
			return EInteractiveCutsceneRuleScope.CUTSCENE_RULE_SCOPE_LOOK_AT
		end

		function C.CameraHold(hero)
			if Refii == nil or Refii._camera_hold == nil then
				return
			end
			local key = GetIDFromEntity(hero)
			if key == nil then
				return
			end
			local mode, fov, pitch, blend, flags, revision = Refii._camera_hold(hero)
			local hold = C.Holds[key]
			if mode == nil and hold == nil then
				return
			end
			if hold == nil then
				hold = { Written = {}, Bits = 0 }
				C.Holds[key] = hold
			end
			local wanted = mode ~= nil
			if wanted and ((CannedActionCamera ~= nil and CannedActionCamera.Active) or
					(CombatCannedActionCamera ~= nil and CombatCannedActionCamera.Active)) then
				wanted = false
			end
			if wanted and flags % 2 == 1 and RangedCombatCamera ~= nil and RangedCombatCamera.IsValid(hero) then
				wanted = false
			end
			if wanted and (hold.Data == nil or hold.Revision ~= revision) then
				local data = { SupercedesCombat = true }
				if fov > 0 then
					data.FOV = fov
				end
				if pitch > 0 then
					data.BoundAngleVert = 0
					data.BoundAngle = pitch
				end
				if blend >= 0 then
					data.BlendInSeconds = blend
					data.BlendOutSeconds = blend
				end
				hold.Data, hold.Revision, hold.Mode, hold.Blend = data, revision, mode, blend
			end
			local record = CameraManager.GetOverriddenData(hero)
			local default = ECameraMode.CAMERA_MODE_DEFAULT
			local claim, release = 0, 0
			for _, half in ipairs(C.CameraHalves) do
				local name, bit = half[1], half[2]
				local slot = record[name]
				local written = hold.Written[name]
				local free = slot.CameraEnum == default
				local ours = written ~= nil and not free and slot.OverrideData == written
				if not free and not ours then
					hold.Written[name] = nil
				elseif wanted then
					if free or written ~= hold.Data then
						claim = claim + bit
					end
				elseif written ~= nil then
					release = release + bit
				end
			end
			if claim ~= 0 then
				CameraManager.SetCameraOverride(hero, hold.Mode, C.CameraScope(claim), hold.Data)
				for _, half in ipairs(C.CameraHalves) do
					if math.floor(claim / half[2]) % 2 == 1 then
						hold.Written[half[1]] = hold.Data
					end
				end
			end
			if release ~= 0 then
				local data = nil
				if hold.Blend ~= nil and hold.Blend >= 0 then
					data = { BlendInSeconds = hold.Blend }
				end
				CameraManager.SetCameraOverride(hero, default, C.CameraScope(release), data)
				for _, half in ipairs(C.CameraHalves) do
					if math.floor(release / half[2]) % 2 == 1 then
						hold.Written[half[1]] = nil
					end
				end
			end
			local bits = (hold.Written.Normal ~= nil and 1 or 0) + (hold.Written.LookAt ~= nil and 2 or 0)
			if bits ~= hold.Bits then
				hold.Bits = bits
				Refii._camera_held(hero, bits)
			end
			if mode == nil and bits == 0 then
				C.Holds[key] = nil
			end
		end

		-- THE HOLD IS NEVER SAVED. The engine persists what the global GetSaveTable returns as soon as it
		-- returns (TU1 sub_8245CB10: the global looked up by name, called, its table handed to
		-- pluto_persist_entry), and the shipped table's CameraSave.Overrides is CameraManager.Overrides
		-- itself (CameraManager.GetSaveTable). The core save wrapper calls this on that table: the
		-- written copy has every half the hold stands on as the default record, the live one untouched.
		function C.CameraSaveTable(t)
			local save = type(t) == "table" and t.CameraSave or nil
			local overrides = type(save) == "table" and save.Overrides or nil
			if type(overrides) ~= "table" or next(C.Holds) == nil then
				return
			end
			local copy = {}
			for id, record in pairs(overrides) do
				local hold = C.Holds[id]
				if hold ~= nil and type(record) == "table" then
					local entry = {}
					for field, value in pairs(record) do
						entry[field] = value
					end
					for _, half in ipairs(C.CameraHalves) do
						local name = half[1]
						local written = hold.Written[name]
						if written ~= nil and type(record[name]) == "table" and record[name].OverrideData == written then
							entry[name] = { CameraEnum = ECameraMode.CAMERA_MODE_DEFAULT }
						end
					end
					copy[id] = entry
				else
					copy[id] = record
				end
			end
			save.Overrides = copy
		end

		-- EACH CAMERA MANAGER ITS OWN CURRENT CAMERA. Every native manager binds the ONE global
		-- `CameraManager` table as its self (`sub_82481328`: the global named at dword_83498450),
		-- and FrameUpdate activates a camera only when the best one differs from
		-- self.CurrentCamera. A second manager — an online joiner's own, local player two's on
		-- split screen — was therefore told "already active" and never activated: no mode
		-- wrapper, so `sub_82265F90` wrote the default pose (origin, 70 degrees). The current
		-- set, camera and priority are kept per hero here, keyed as the shipped script keys its
		-- per-player overrides (GetIDFromEntity).
		if CameraManager ~= nil and CameraManager.FrameUpdate ~= nil and CameraManager.FrameUpdate ~= C.FrameUpdate then
			local shipped = CameraManager.FrameUpdate
			C.CameraState = C.CameraState or {}
			C.FrameUpdate = C.Permanent(function(self, hero, ...)
				-- Called for each hero every frame: the chest watch rides it (throttled inside).
				if C.ChestWatch ~= nil then
					pcall(C.ChestWatch)
				end
				if C.HouseholdTick ~= nil then
					pcall(C.HouseholdTick)
				end
				if C.CombatApply ~= nil then
					pcall(C.CombatApply)
				end
				if C.StatePlayerModes ~= nil then
					pcall(C.StatePlayerModes)
				end
				if C.RouteTick ~= nil then
					pcall(C.RouteTick)
				end
				if hero ~= nil then
					pcall(C.CameraHold, hero)
				end
				if hero == nil then
					return shipped(self, hero, ...)
				end
				local key = GetIDFromEntity(hero)
				if key == nil or not C.OwnCameras() then
					local result = shipped(self, hero, ...)
					pcall(C.CameraStated, hero, self.CurrentCamera)
					return result
				end
				local state = C.CameraState[key]
				if state == nil then
					state = {}
					C.CameraState[key] = state
				end
				local set, camera, priority = self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority
				self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority = state.Set, state.Camera, state.Priority
				local result = shipped(self, hero, ...)
				state.Set, state.Camera, state.Priority = self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority
				pcall(C.CameraStated, hero, self.CurrentCamera)
				self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority = set, camera, priority
				return result
			end, "FrameUpdate")
			CameraManager.FrameUpdate = C.FrameUpdate
		end

		-- EACH PLAYER TURNS THEIR OWN CAMERA. SACCamera:ProcessGameAction rotates only while the
		-- global GetCanRotateCamera() (the network manager's +222, `sub_8233C280`) is true. Fable 2
		-- clears it for co-op because one camera framed both players: the host's join handler
		-- (`stb r22,0xDE(r31)` at 0x8232BD98) and the local shared-screen toggle (0x8233C350).
		if GetCanRotateCamera ~= nil and GetCanRotateCamera ~= C.GetCanRotateCamera then
			local shipped = GetCanRotateCamera
			C.GetCanRotateCamera = C.Permanent(function(...)
				if C.OwnCameras() then
					return true
				end
				return shipped(...)
			end, "GetCanRotateCamera")
			GetCanRotateCamera = C.GetCanRotateCamera
		end

		-- THE MOUSE'S CAMERA KEEPS ITS HEADING. SACCamera hangs on a string: CalculateXY (FrameUpdate
		-- pc 113) puts the camera between the cage and the string from the NEW focus, along the line to
		-- where it was, so a hero moving across the view swings the camera after him — the only turn
		-- the free camera makes on its own while walking. The script's own counter-term is the zoom
		-- level's TrackAmount, `pos + CalculateMovement(centre) * TrackAmount` (CalculateXY pc 6..16),
		-- skipped while PlayerIsManuallyRotatingCamera. The camera the mouse drives is published as
		-- RefiiMouseLookCamera for its update (Fable2/src/vfs/src/hooks/mouse_look.cpp, cleared after it):
		-- that camera is carried by its focus's whole movement every update, so it keeps its heading
		-- and turns only for the mouse, a lock's AlignToDirection and the scripts' cuts. The shipped
		-- term is held off for the call (the flag it tests) so the movement is added once.
		if SACCamera.CalculateXY ~= nil and SACCamera.CalculateXY ~= C.CalculateXY then
			local shipped = SACCamera.CalculateXY
			C.CalculateXY = C.Permanent(function(self, pos, cage, length, centre, ...)
				if self ~= RefiiMouseLookCamera or pos == nil or centre == nil then
					return shipped(self, pos, cage, length, centre, ...)
				end
				local moved = self:CalculateMovement(centre)
				local manual = self.PlayerIsManuallyRotatingCamera
				self.PlayerIsManuallyRotatingCamera = true
				local ok, result = pcall(shipped, self, pos + moved, cage, length, centre, ...)
				self.PlayerIsManuallyRotatingCamera = manual
				if not ok then
					error(result, 0)
				end
				return result
			end, "CalculateXY")
			SACCamera.CalculateXY = C.CalculateXY
		end

		-- THE MOUSE TURNS THE RANGED AIM WHOLE (Fable2/src/vfs/src/hooks/mouse_look.cpp). For the update in
		-- flight the payload publishes the aim camera as RefiiMouseLookCamera with the mouse's turn in
		-- RefiiAimYaw (degrees, rightward) and RefiiAimTilt (degrees, upward). The camera is turned
		-- rigidly about the hero's collision head, which GetDesiredCameraPos hangs it from: the yaw
		-- about the vertical through it, then the tilt about its level right axis, bounded so
		-- CurrentDirection stays inside the camera's own AngleLimit. Turned: CurrentDirection,
		-- CurrentDesired, CurrentPosition, and the aim point GetDesiredDirection reads (from the turned
		-- position to the point turned with it). The shipped easing then runs on geometry that is the
		-- old geometry turned, so the camera turns by exactly the mouse plus its own motion. What was
		-- applied is stated back for the payload, which turns the aim's own points to match.
		if RangedCombatCamera ~= nil and RangedCombatCamera.UpdateCurrentDirection ~= nil and
				RangedCombatCamera.UpdateCurrentDirection ~= C.AimUpdateCurrentDirection then
			local shipped = RangedCombatCamera.UpdateCurrentDirection
			C.AimUpdateCurrentDirection = C.Permanent(function(self, ...)
				local hx, hy, hz = self.RefiiHostAimX, self.RefiiHostAimY, self.RefiiHostAimZ
				self.RefiiHostAimX, self.RefiiHostAimY, self.RefiiHostAimZ = nil, nil, nil
				local yaw, tilt = self.RefiiAimYaw, self.RefiiAimTilt
				self.RefiiAimYaw, self.RefiiAimTilt = nil, nil
				if self == RefiiMouseLookCamera and hx ~= nil and hy ~= nil and hz ~= nil and
						self.Player ~= nil and Creature ~= nil and Creature.GetCollisionHeadPosition ~= nil then
					if self.GetTightness ~= nil then
						self:GetTightness()
					end
					local len = math.sqrt(hx * hx + hy * hy + hz * hz)
					local pivot = Creature.GetCollisionHeadPosition(self.Player)
					if len >= 0.0001 and pivot ~= nil then
						local nx, ny, nz = hx / len, hy / len, hz / len
						local limit = self.AngleLimit or (math.pi / 6)
						local elev = math.asin(math.max(-1, math.min(1, nz)))
						if elev > limit or elev < -limit then
							local clamped = math.max(-limit, math.min(limit, elev))
							local hlen = math.sqrt(nx * nx + ny * ny)
							local c = math.cos(clamped)
							if hlen > 0.0001 then
								nx, ny = (nx / hlen) * c, (ny / hlen) * c
							end
							nz = math.sin(clamped)
						end
						local dir = CVector3(nx, ny, nz)
						local oldD = self.CurrentDirection
						if oldD ~= nil and self.CurrentPosition ~= nil then
							local ox, oy, oz = oldD:GetX(), oldD:GetY(), oldD:GetZ()
							local olen = math.sqrt(ox * ox + oy * oy + oz * oz)
							local nhlen = math.sqrt(nx * nx + ny * ny)
							if olen >= 0.0001 and nhlen >= 0.0001 then
								ox, oy, oz = ox / olen, oy / olen, oz / olen
								local ohlen = math.sqrt(ox * ox + oy * oy)
								if ohlen >= 0.0001 then
									local px, py, pz = pivot:GetX(), pivot:GetY(), pivot:GetZ()
									local cx = self.CurrentPosition:GetX() - px
									local cy = self.CurrentPosition:GetY() - py
									local cz = self.CurrentPosition:GetZ() - pz
									if cx * cx + cy * cy + cz * cz < 625 then
										local ofx, ofy = ox / ohlen, oy / ohlen
										local orx, ory = ofy, -ofx
										local side = cx * orx + cy * ory
										local behind = -(cx * ofx + cy * ofy) / math.max(ohlen, 0.1)
										local height = cz + oz * behind
										local nfx, nfy = nx / nhlen, ny / nhlen
										local nrx, nry = nfy, -nfx
										self.CurrentPosition = CVector3(
											px - nx * behind + nrx * side,
											py - ny * behind + nry * side,
											pz - nz * behind + height)
									end
								end
							end
						end
						self.CurrentDirection = dir
						self.CurrentDesired = dir
						self.RefiiAimPivot = pivot
						self.RefiiHostAimApplied = true
						return
					end
				end
				local d = self.CurrentDirection
				if self ~= RefiiMouseLookCamera or yaw == nil or tilt == nil or d == nil or
						self.Player == nil or Creature == nil or Creature.GetCollisionHeadPosition == nil or
						Targeting == nil or Targeting.GetCameraSafeTargetedPosition == nil then
					return shipped(self, ...)
				end
				if self.GetTightness ~= nil then
					self:GetTightness()
				end
				local pivot = Creature.GetCollisionHeadPosition(self.Player)
				local dx, dy, dz = d:GetX(), d:GetY(), d:GetZ()
				local length = math.sqrt(dx * dx + dy * dy + dz * dz)
				if pivot == nil or length < 0.0001 then
					return shipped(self, ...)
				end
				local radians = math.pi / 180
				local limit = self.AngleLimit or (math.pi / 6)
				local elevation = math.asin(math.max(-1, math.min(1, dz / length)))
				local t = math.max(math.min(-limit - elevation, 0),
					math.min(math.max(limit - elevation, 0), tilt * radians))
				local y = -yaw * radians
				local cy, sy = math.cos(y), math.sin(y)
				local hx, hy = dx * cy - dy * sy, dx * sy + dy * cy
				local level = math.sqrt(hx * hx + hy * hy)
				if level < 0.0001 then
					t, level = 0, 1
				end
				local rx, ry = hy / level, -hx / level
				local ct, st = math.cos(t), math.sin(t)
				local px, py, pz = pivot:GetX(), pivot:GetY(), pivot:GetZ()
				local function spin(vx, vy, vz)
					local ax, ay, az = vx * cy - vy * sy, vx * sy + vy * cy, vz
					local dot = ax * rx + ay * ry
					return ax * ct + ry * az * st + rx * dot * (1 - ct),
						ay * ct - rx * az * st + ry * dot * (1 - ct),
						az * ct + (rx * ay - ry * ax) * st
				end
				local function direction(v)
					return CVector3(spin(v:GetX(), v:GetY(), v:GetZ()))
				end
				local function point(v)
					local x, y2, z = spin(v:GetX() - px, v:GetY() - py, v:GetZ() - pz)
					return CVector3(x + px, y2 + py, z + pz)
				end
				self.CurrentDirection = direction(d)
				if self.CurrentDesired ~= nil then
					self.CurrentDesired = direction(self.CurrentDesired)
				end
				if self.CurrentPosition ~= nil then
					self.CurrentPosition = point(self.CurrentPosition)
					local target = Targeting.GetCameraSafeTargetedPosition(self.Player)
					if target ~= nil then
						self.RefiiAimFrom = target - (point(target) - self.CurrentPosition)
					end
				end
				local ok, result = pcall(shipped, self, ...)
				self.RefiiAimFrom = nil
				if not ok then
					error(result, 0)
				end
				self.RefiiAimPivot = pivot
				self.RefiiAimTurned, self.RefiiAimTilted = yaw, t / radians
				self.RefiiAimRightX, self.RefiiAimRightY = rx, ry
				return result
			end, "AimUpdateCurrentDirection")
			RangedCombatCamera.UpdateCurrentDirection = C.AimUpdateCurrentDirection
		end

		-- RangedCombatCamera:GetDesiredDirection(from) reads `from or self.CurrentPosition`; while the
		-- mouse's turn is applied the line starts where the turn put it (above).
		if RangedCombatCamera ~= nil and RangedCombatCamera.GetDesiredDirection ~= nil and
				RangedCombatCamera.GetDesiredDirection ~= C.AimGetDesiredDirection then
			local shipped = RangedCombatCamera.GetDesiredDirection
			C.AimGetDesiredDirection = C.Permanent(function(self, from, ...)
				if from == nil and self.RefiiAimFrom ~= nil then
					from = self.RefiiAimFrom
				end
				return shipped(self, from, ...)
			end, "AimGetDesiredDirection")
			RangedCombatCamera.GetDesiredDirection = C.AimGetDesiredDirection
		end

		-- THE ROUTE's camera calls (SACCamera.AddInterestingEntity, ...): this table is new each time
		-- the camera scripts run.
		if C.RouteWrapAll ~= nil then
			C.RouteWrapAll("SACCamera")
		end

		-- THE PAIR WALKS BACK INTO THE LEASH. The leash (sub_821A0298, disc sub_821A0340) zeroes every
		-- step that ends farther from the partner than the stated distance (flt_8331914C, disc
		-- flt_833190F8; 30 m as the binary starts, written by Debug.SetLimitCharacterDistance):
		-- `fcmpu cr6, f31, f0` 0x821A0AEC, then `stvx128 v0` (zero) into the move 0x821A0B40 (disc
		-- 0x821A0B94, 0x821A0BE8). Free roam has no such limit, so a change from free roam to the
		-- shared screen with the heroes farther apart than that held both where they stood; they roam
		-- free, under the shared camera, until they are within it again.
		C.LeashMetres = C.LeashMetres or 30
		if Debug ~= nil and Debug.SetLimitCharacterDistance ~= nil and
				Debug.SetLimitCharacterDistance ~= C.SetLimitCharacterDistance then
			local shipped = Debug.SetLimitCharacterDistance
			C.SetLimitCharacterDistance = C.Permanent(function(distance, ...)
				if type(distance) == "number" then
					C.LeashMetres = distance
				end
				return shipped(distance, ...)
			end, "SetLimitCharacterDistance")
			Debug.SetLimitCharacterDistance = C.SetLimitCharacterDistance
		end

		function C.Reuniting(free)
			if free then
				C.WasFree = true
				return false
			end
			if not C.WasFree then
				return false
			end
			local hero = GetPlayerHero ~= nil and GetPlayerHero() or nil
			local henchman = C.ShippedGetPlayerHenchman or GetPlayerHenchman
			local partner = henchman ~= nil and henchman() or nil
			if hero == nil or partner == nil or GetDistanceBetweenEntities == nil or
					GetDistanceBetweenEntities(hero, partner) <= C.LeashMetres then
				C.WasFree = false
				return false
			end
			return true
		end

		if SACCamera.SetMaxCoopDistance ~= C.SetMaxCoopDistance then
			local shipped = SACCamera.SetMaxCoopDistance
			C.SetMaxCoopDistance = C.Permanent(function(self, ...)
				local free = C.FreeRoam()
				local reuniting = C.Reuniting(free)
				if Debug ~= nil and Debug.SetMultiPlayerHeroFree ~= nil then
					Debug.SetMultiPlayerHeroFree(free or reuniting)
				end
				if free then
					return
				end
				return shipped(self, ...)
			end, "SetMaxCoopDistance")
			SACCamera.SetMaxCoopDistance = C.SetMaxCoopDistance
		end
		""";

	/// <summary>Mounts both chunks the payload runs. Called on every mount.</summary>
	public static void Attach(BnkFileSystem mounted)
	{
		Mount(mounted, Key, Source);
		Mount(mounted, CameraKey, CameraSource);
	}

	private static void Mount(BnkFileSystem mounted, string key, string source)
	{
		if (Refii.Lua.Engine_LuaDecompiler.Available &&
			!Refii.Lua.Engine_LuaDecompiler.TryCheckSource(source, "@" + key, out string error))
		{
			GD.PushError($"[coop] {key} does not compile and is NOT run: {error}");
			return;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(source);
		mounted.AddVirtualFile(key, key, bytes.Length, () => bytes, "Refii");
	}
}
