-- Souls Combat: the gameplay camera, Elden Ring style, stated through the camera API.
--
-- Runs after the camera scripts each time they run (a session's first level, a save load, a
-- co-op join) and again when a camera setting changes; every statement is idempotent.
--
-- What the shipped camera does (camera/stringandcagecamera.lua, SACCamera):
--   * the camera hangs on a "string" between CageRadius and StringLength from a focal point, so
--     its distance drifts between 3.4 and 5 m as the hero moves; the focal point is a slot the
--     hero may wander in, merged with circles round nearby foes and villagers, so the frame
--     slides toward whoever is close;
--   * with a foe within 10 m it blends into a high combat camera (HeightOffset 2.8, FocalOffset 1);
--   * right stick X orbits at RotateSpeed 120 deg/s; stick Y pushes ManualHeightOffset, which
--     GetHeight decays back to 0 every update — the pitch springs back to level;
--   * a lock frames the target through AlignToDirection, inside a cone of cos 0.9 at 250 deg/s.
--
-- The souls camera, for every camera that frames one hero (single player, online co-op, split
-- screen): a fixed orbit distance round a hero-centred focus (camera_frame), no combat camera
-- (camera_combat_blend 0), no foe or villager pull (camera_interest), a pitch that stays where the
-- stick puts it (camera_pitch_hold, also on a shared screen) and a tighter lock (camera_tune). A
-- shared screen keeps the shipped framing, which has to hold two heroes. The orbit speed and its
-- damping stay the shipped ones for the stick, the player's camera sensitivity scales them, and
-- the mouse's turn lands whole outside them.

if Refii == nil or Refii.camera_frame == nil then
	return
end

-- Named so other mods can tell the souls camera is in force (Portal Gun leaves the frame to it).
SoulsCamera = SoulsCamera or {}
local C = SoulsCamera

local function Setting(name, default)
	if Refii.setting then
		return Refii.setting("souls_combat." .. name, default)
	end
	return default
end

function C.State()
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
			levels = "first",
		})
		Refii.camera_combat_blend(p, 0)
		Refii.camera_interest(p, { foes = false, passers = false })
		Refii.camera_pitch_hold(p, true, { shared = true })
	end
end
C.State()

-- A live change of a camera setting (setting_changed) reaches the camera at once. Subscribed once
-- per game VM; this chunk runs again with every camera build.
if not C.SettingsSubscribed and Refii.on then
	C.SettingsSubscribed = true
	Refii.on("setting_changed", function(e)
		if string.sub(e.key, 1, 20) == "souls_combat.camera_" then
			C.State()
		end
	end)
end
