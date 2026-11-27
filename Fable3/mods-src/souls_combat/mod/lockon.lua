-- Souls Combat: the toggled lock-on.
--
-- LOCK-ON IS ITS OWN BUTTON: R3 on the pad, G on the keyboard (input.json `lock_on`, rebindable on the
-- controls page), as Dark Souls has it. Fable III's LT is several things at once — the game's own
-- hold-to-lock, Hold Hands with whoever is beside the hero, the ranged zoom — so the lock does not ride
-- it: LT stays entirely the game's. The action is CLAIMED for both players, so the game does not see
-- the press (R3 is Fable III's camera-mode toggle on the pad; V still toggles it on the keyboard).
--
-- THE LOCK IS OURS: one state per hero, flipped by the press, and the game's lock held to match it
-- through Refii.lock_target / release_lock (the targeting component's force byte; the game's targeting
-- pass adds and drops its TARGET_LOCK mode from it).
--   lock_lost — THE LOCK ENDS WHEN THERE IS NOTHING TO LOCK. When the game drops its TARGET_LOCK
--     mode (the target died or left reach) the targeting pass re-selects while the force byte
--     holds; a drop, and a toggle that finds nothing, gives the lock lock_drop_seconds
--     (Refii.after) to hold a living target (Refii.locked_target, Refii.living) and releases it
--     otherwise.
--   hero_gone — a hero that stopped existing holds no lock.
--
-- THE RETICLE: Fable III draws no mark on a forced lock's target, so the locked target carries the
-- mod's own reticle (art/lock_reticle.png) as a world marker for that player (Refii.mark), moved to
-- each new target the game picks and removed with the lock.
--
-- IN A CUTSCENE THE BUTTON DOES NOTHING: a full cutscene, a foreground interactive cutscene, or a
-- camera the game gave is what Refii.in_cutscene states for that hero, and the cutscene's rising
-- edge releases any lock.

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

-- The heroes currently locked, by handle.
S.Locked = {}

local function IsLocked(hero)
	return hero ~= nil and S.Locked[hero] ~= nil
end

local function InCutscene(p)
	return IsPlayer(p) and Refii.in_cutscene(p) == true
end

local function HasValidTarget(p)
	local target = Refii.locked_target(p)
	return target ~= nil and Refii.living(target)
end

local function DropKey(hero)
	return "souls_combat.lock_drop." .. hero
end

local function ApplyLock(p, on)
	if on then
		Refii.lock_target(p)  -- the game's own pick
	else
		Refii.release_lock(p)
	end
end

-- The camera frames the mod's lock while it stands (camera_lock_frame): stated when a lock is set or
-- cleared and held equal to S.Locked on every tick.
S.Framed = {}
local function FrameLocks()
	for p = 1, 2 do
		local hero = Refii.player_hero(p)
		local want = hero ~= nil and S.Locked[hero] ~= nil
		if S.Framed[p] ~= want then
			S.Framed[p] = want
			Refii.camera_lock_frame(p, want)
		end
	end
end

local function Unlock(hero, p, why)
	S.Locked[hero] = nil
	FrameLocks()
	if S.Reticles ~= nil and S.Reticles[hero] ~= nil then
		Refii.unmark(S.Reticles[hero].id)
		S.Reticles[hero] = nil
	end
	Refii.cancel(DropKey(hero))
	if IsPlayer(p) then
		ApplyLock(p, false)
	end
	Refii.log("Souls Combat: lock-on OFF (" .. why .. ")")
end

-- lock_drop_seconds from now, the lock ends unless it holds a valid target.
local function ReleaseIfNothingToLock(hero, p)
	Refii.after(Setting("lock_drop_seconds", 0.5), function()
		if IsLocked(hero) and not HasValidTarget(p) then
			Unlock(hero, p, "no target")
		end
	end, DropKey(hero))
end

local kAction = "souls_combat.lock_on"
local kReticle = "art/lock_reticle.png"

-- Each locked hero's reticle: the marker id and the target it stands on.
S.Reticles = {}

local function Unmark(hero)
	local r = S.Reticles[hero]
	if r ~= nil then
		Refii.unmark(r.id)
		S.Reticles[hero] = nil
	end
end

local function Toggle(p)
	local hero = Refii.player_hero(p)
	if hero == nil or InCutscene(p) then
		return
	end
	if IsLocked(hero) then
		Unlock(hero, p, "toggle")
		return
	end
	S.Locked[hero] = true
	FrameLocks()
	ApplyLock(p, true)
	ReleaseIfNothingToLock(hero, p)
	Refii.log("Souls Combat: lock-on ON")
end

Refii.on("input_action", function(e)
	if e.name == kAction and e.pressed and IsPlayer(e.player) then
		Toggle(e.player)
	end
end)

-- The lock-on button is the mod's, for both players, from the runtime's start.
Refii.on("lua_ready", function()
	for p = 1, 2 do Refii.input_claim(p, kAction, true) end
end)
for p = 1, 2 do Refii.input_claim(p, kAction, true) end

-- The reticle follows the game's pick each tick while the lock holds, for each of the two players.
-- (On Fable III a `pairs` loop must have exactly two variables: the VM walks kpairs inline only then,
-- and `for hero in pairs(t)` runs zero times.)
Refii.on("tick", function()
	FrameLocks()
	for p = 1, 2 do
		local hero = Refii.player_hero(p)
		if hero ~= nil then
			local target = IsLocked(hero) and Refii.locked_target(p) or nil
			if target ~= nil and not Refii.living(target) then
				target = nil
			end
			local r = S.Reticles[hero]
			if (r == nil and target ~= nil) or (r ~= nil and r.target ~= target) then
				Unmark(hero)
				if target ~= nil then
					local id = Refii.mark(target, kReticle, "", p)
					if id ~= nil then
						S.Reticles[hero] = { id = id, target = target }
					end
				end
			end
		end
	end
end)

Refii.on("lock_lost", function(e)
	if IsLocked(e.hero) then
		ReleaseIfNothingToLock(e.hero, e.player)
	end
end)

Refii.on("hero_gone", function(e)
	if IsLocked(e.hero) then
		S.Locked[e.hero] = nil
		FrameLocks()
		Refii.cancel(DropKey(e.hero))
		if S.Reticles[e.hero] ~= nil then
			Refii.unmark(S.Reticles[e.hero].id)
			S.Reticles[e.hero] = nil
		end
	end
end)

-- A cutscene starting ends the lock of the hero it holds (player 0, a full cutscene: both heroes).
Refii.on("cutscene", function(e)
	if not e.active then
		return
	end
	for p = 1, 2 do
		local hero = Refii.player_hero(p)
		if IsLocked(hero) and (e.player == 0 or p == e.player) then
			Unlock(hero, p, "cutscene")
		end
	end
end)
