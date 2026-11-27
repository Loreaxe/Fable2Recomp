-- Portal Gun: while portal mode holds, the camera stays where the mouse puts it — stated through the
-- camera API.
--
-- Runs after the camera scripts each time they run; PortalCamera.State is also called when portal mode
-- flips (init.lua's toggle) and on every tick, so a change of who frames one hero (a partner joining a
-- shared screen) reaches the camera. Every statement is idempotent and made only on a change.
--
-- The gun's aim (camera/rangedcombatcamera.lua) chases the aim point: outside a cone of FOV * 0.5 *
-- Tightness round its direction it is pulled toward that point, turned OffsetDegrees to the side, and its
-- elevation stops at AngleLimit (30 degrees). In portal mode the cone is narrow (ranged_tightness), so a
-- controller's stick turns the camera with the aim point; the offset is nothing (ranged_offset), so the
-- aim is the middle of the screen, where the portal goes; the elevation reaches further
-- (ranged_pitch_limit).
--
-- The everyday camera (camera/stringandcagecamera.lua, SACCamera) hangs on a string from a focal point
-- that slides about and takes in whoever is near, blends into a high combat camera near a foe, and decays
-- its pitch back to level on every update. In portal mode, and only for a camera framing one hero, the
-- distance is fixed (camera_frame; Souls Combat's frame stands when it is enabled), foes and passers-by
-- are hidden from the frame (camera_interest, camera_combat_blend), the pitch stays where it was put over
-- a wider range (camera_pitch_hold), and the focus slides over the hero's shoulder (camera_focus).

if Refii == nil or Refii.camera_frame == nil then
	return
end

PortalCamera = PortalCamera or {}
local C = PortalCamera

-- What was last stated per player; made new with every run, as the camera API's values are with every
-- load of the mods' Lua.
C.Stated = {}

local RANGED_ON = { ranged_tightness = 0.1, ranged_offset = 0, ranged_pitch_limit = 70 }
local RANGED_OFF = { ranged_tightness = false, ranged_offset = false, ranged_pitch_limit = false }

function C.State()
	local on = PortalGun ~= nil and PortalGun.On == true
	for p = 1, 2 do
		local hero = Refii.player_hero(p)
		local body = hero ~= nil and Refii.entity(hero) or nil
		local aiming = on and body ~= nil and CameraFunctions ~= nil and not CameraFunctions.IsMultiplayer(body)
		local framed = aiming and SoulsCamera == nil
		local key = (on and "r" or "-") .. (aiming and "a" or "-") .. (framed and "f" or "-")
		if C.Stated[p] ~= key then
			C.Stated[p] = key
			Refii.camera_tune(p, on and RANGED_ON or RANGED_OFF)
			Refii.camera_frame(p, framed and { distance = 3.2, interior_scale = 0.85, slot = 0, track = 0, levels = "first" } or nil)
			Refii.camera_combat_blend(p, aiming and 0 or nil)
			Refii.camera_interest(p, aiming and { foes = false, passers = false } or nil)
			Refii.camera_focus(p, aiming and { right = 0.95, up = 0.25, ease = 0.12 } or nil)
			Refii.camera_pitch_hold(p, aiming, { min = 1.2, max = 3.0 })
		end
	end
end
C.State()

if not C.Subscribed and Refii.on then
	C.Subscribed = true
	Refii.on("tick", function()
		PortalCamera.State()
	end)
end
