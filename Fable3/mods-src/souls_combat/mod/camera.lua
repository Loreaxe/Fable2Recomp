-- Souls Combat: the gameplay camera, Elden Ring style, stated through the camera API.
--
-- WHEN THIS RUNS. After the startup scripts and again after every camera-script run (a camera manager
-- being built), straight after the core chunk re-wraps the camera, and again when a camera setting
-- changes. Every statement is idempotent.
--
-- WHAT THE SHIPPED CAMERA DOES (camera/stringandcagecamera.lua and camera/targetlockcamera.lua, both
-- SACCamera, all Lua):
--   * the camera hangs on a "string" from a focal point: the zoom level's StringLength plus how far the
--     cage exceeds its CageRadius; the cage takes in circles round nearby foes and interesting entities,
--     and the focus drifts inside SlotWidth, SlotDepthNear and SlotDepthFar;
--   * the combat weight rises from 0 at 10 m to 1 at 4 m from the nearest foe, blending toward
--     CombatExteriorZoomLevel (StringLength 6.5, HeightOffset 2);
--   * stick Y adds to ManualHeightOffset (HeightAdjustSpeed 0.05), clamped to 0.7 either way and then
--     decayed toward 0: the pitch springs back;
--   * a lock frames its target only while the look-at button is down (DirectionStrengthHard 0.9,
--     InwardsRotateSpeed 250 deg/s);
--   * MagicSACCamera takes over while a spell fully charges or casts: a 6.5 m high view.
--
-- THE SOULS CAMERA, for every camera that frames one hero (single player and online co-op): a fixed
-- orbit distance round a hero-centred focus on every explore zoom level (camera_frame), no combat camera
-- (camera_combat_blend 0) and no spell camera (camera_mode_allow CAMERA_MAGIC_AIM false), no foe or
-- villager pull (camera_interest), a pitch that stays where the stick or mouse puts it
-- (camera_pitch_hold, also on a shared screen), a tighter lock (camera_tune), and the mod's lock framed
-- (camera_lock_frame, stated by lockon.lua while a lock stands). A shared screen keeps the shipped
-- framing, which holds two heroes. Sprint, drop, vault, fall, hand-holding, Assassin's Rush,
-- conversation and troll levels stay shipped, and so do the orbit speed and its damping.

if Refii == nil or Refii.camera_frame == nil then
	return
end

SoulsCamera = SoulsCamera or {}
local C = SoulsCamera

local function Setting(name, default)
	if Refii.setting then
		return Refii.setting("souls_combat." .. name, default)
	end
	return default
end

function C.State()
	local magic = ECameraMode ~= nil and ECameraMode.CAMERA_MODE_MAGIC_AIM or Refii.CAMERA_MAGIC_AIM
	for p = 1, 2 do
		Refii.camera_tune(p, {
			pitch_speed = Setting("camera_pitch_speed", 0.09),
			pitch_max = 1.2,
			pitch_min = 0.45,
			move_damping = 1,
			lock_strength = Setting("camera_lock_tightness", 0.97),
			lock_turn_speed = Setting("camera_lock_turn_speed", 420),
		})
		Refii.camera_frame(p, {
			distance = Setting("camera_distance", 4),
			interior_scale = 0.85,
			height = Setting("camera_height", 0.9),
			focus_height = Setting("camera_focus_height", 1.6),
			slot = 0,
			track = 0,
			levels = "all",
		})
		Refii.camera_combat_blend(p, 0)
		Refii.camera_interest(p, { foes = false, passers = false })
		Refii.camera_pitch_hold(p, true, { shared = true })
		Refii.camera_mode_allow(p, magic, false)
	end
end
C.State()

-- A live change of a camera setting (setting_changed) reaches the camera at once. The handler is
-- taken off and put back on each run, so a run never stacks a second one and a reload that dropped
-- the handlers gets it back.
if Refii.on then
	if C.OnSetting ~= nil and Refii.off then
		Refii.off("setting_changed", C.OnSetting)
	end
	C.OnSetting = function(e)
		if type(e.key) == "string" and string.sub(e.key, 1, 20) == "souls_combat.camera_" then
			C.State()
		end
	end
	Refii.on("setting_changed", C.OnSetting)
end
