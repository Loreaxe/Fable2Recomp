-- Portal Gun: where a shot lands, and moving the hero between the two portals.
--
-- G puts the hero's own gun into portal mode and back, so its stance, aiming and camera stay the game's
-- and nothing is equipped or unequipped. Portal mode serves one player at a time, the one whose G turned
-- it on (PortalGun.toggle(p)), published as portal_gun.player. In portal mode its shot is cancelled here (ranged_shot) and
-- the fire signal asks godot/portal_gun.gd for the aim, which arrives as PortalGun.fire(which, x,
-- y, z, dx, dy, dz), in world axes. portal_gun.mode says portal mode holds, and portal_gun.weapon names
-- the gun's entity, whose model godot/portal_gun.gd swaps. The game's physics answers one question, whether a line is clear, so a hit
-- is found by halving along the ray, and a surface's direction from two more rays beside it.
--
-- Published per portal i (1, 2): portal_gun.on<i>, the centre x<i> y<i> z<i>, the normal nx<i> ny<i>
-- nz<i> and the up ux<i> uy<i> uz<i>. Every shot also signals where it ended, as "which opened x y z":
-- opened is 1 when a portal opened there, and there is no place when it met nothing.
-- The hero moves on touching a portal; walls stay solid, there is no hole in the physics.

PortalGun = PortalGun or {}
local P = PortalGun

local RANGE = 120
local HALF_W, HALF_H = 0.55, 1.0
local GAP = 0.02            -- how far off the surface a portal stands
local REST = 0.4            -- seconds after a trip before another
local LOOK_EVERY = 0.5      -- seconds between looks for the hero's gun

-- The game's own sounds, by audio bank event name. Only the SE_ events start from a mod's call: the
-- FX ones (the teleports among them) belong to the particles. A sound that does not start is logged.
local SOUNDS = {
	launch = { "SE_SPELL_RELEASE_TARGETED_TIME_SLOT_2", "SE_SPELL_RELEASE_TARGETED_FORCE_PUSH_SLOT_1" },
	open = "SE_SPELL_RELEASE_AREA_VORTEX_SLOT_1",
	fail = "SE_SPELL_END_AREA_TIME",
	trip = "SE_SPELL_RELEASE_AREA_TIME_SLOT_2",
}

local said = {}
local function heard(cue, started)
	if not started and not said[cue] then
		said[cue] = true
		Refii.log("portal_gun: sound " .. cue .. " did not start")
	end
end

local function sound_on(cue, entity)
	if entity ~= nil then
		heard(cue, Refii.play_sound(cue, entity))
	end
end

local function sound_at(cue, at)
	if at ~= nil then
		heard(cue, Refii.play_sound_at(cue, at[1], at[2], at[3]))
	end
end

P.Portals = P.Portals or {}
P.Player = P.Player or 1
P.Armed = P.Armed == nil and true or P.Armed
P.Rest = P.Rest or 0

local function add(a, b, s) return { a[1] + b[1] * s, a[2] + b[2] * s, a[3] + b[3] * s } end
local function dot(a, b) return a[1] * b[1] + a[2] * b[2] + a[3] * b[3] end
local function cross(a, b)
	return { a[2] * b[3] - a[3] * b[2], a[3] * b[1] - a[1] * b[3], a[1] * b[2] - a[2] * b[1] }
end
local function unit(a)
	local n = math.sqrt(dot(a, a))
	if n < 0.0001 then
		return nil
	end
	return { a[1] / n, a[2] / n, a[3] / n }
end
local function clear(a, b)
	return Refii.line_of_sight(a[1], a[2], a[3], b[1], b[2], b[3])
end

-- How far along the ray the first hit is, or nil within range. A clear line stays clear however
-- far back its end comes, so halving finds the hit.
local function hit(from, dir, range)
	if clear(from, add(from, dir, range)) then
		return nil
	end
	local near, far = 0, range
	for _ = 1, 14 do
		local middle = (near + far) * 0.5
		if clear(from, add(from, dir, middle)) then
			near = middle
		else
			far = middle
		end
	end
	return near
end

local function surface(from, dir)
	local t = hit(from, dir, RANGE)
	if t == nil then
		return nil
	end
	local at = add(from, dir, t)
	local side = unit(cross(dir, math.abs(dir[3]) < 0.9 and { 0, 0, 1 } or { 1, 0, 0 }))
	local other = unit(cross(dir, side))
	local points = {}
	for i, beside in ipairs({ side, other }) do
		local t2 = hit(add(from, beside, 0.2), dir, RANGE)
		if t2 == nil then
			t2 = hit(add(from, beside, -0.2), dir, RANGE)
			if t2 == nil then
				return nil
			end
			points[i] = add(add(from, beside, -0.2), dir, t2)
		else
			points[i] = add(add(from, beside, 0.2), dir, t2)
		end
	end
	local a = { points[1][1] - at[1], points[1][2] - at[2], points[1][3] - at[3] }
	local b = { points[2][1] - at[1], points[2][2] - at[2], points[2][3] - at[3] }
	local normal = unit(cross(a, b))
	if normal == nil then
		return nil
	end
	if dot(normal, dir) > 0 then
		normal = { -normal[1], -normal[2], -normal[3] }
	end
	return at, normal
end

-- Slides the portal along the surface out of anything it would cut into, then checks it all lies
-- on the surface with room in front. nil when it does not fit.
local function fit(at, normal, up)
	local right = unit(cross(up, normal))
	local off = add(at, normal, 0.1)
	for _, along in ipairs({ { up, HALF_H }, { right, HALF_W } }) do
		local dir, half = along[1], along[2]
		for _, sign in ipairs({ 1, -1 }) do
			local look = { dir[1] * sign, dir[2] * sign, dir[3] * sign }
			local t = hit(off, look, half + 0.02)
			if t ~= nil then
				at = add(at, look, t - half - 0.02)
				off = add(at, normal, 0.1)
			end
		end
	end
	for _, c in ipairs({ { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 } }) do
		local corner = add(add(at, right, c[1] * HALF_W * 0.85), up, c[2] * HALF_H * 0.85)
		if clear(add(corner, normal, 0.15), add(corner, normal, -0.2)) then
			return nil
		end
		if not clear(off, add(corner, normal, 0.1)) then
			return nil
		end
	end
	if not clear(off, add(off, normal, 0.8)) then
		return nil
	end
	return at
end

local function publish(i)
	local p = P.Portals[i]
	Refii.publish("portal_gun.on" .. i, p and 1 or 0)
	if p then
		for k, v in pairs({ x = p.at[1], y = p.at[2], z = p.at[3], nx = p.normal[1], ny = p.normal[2],
			nz = p.normal[3], ux = p.up[1], uy = p.up[2], uz = p.up[3] }) do
			Refii.publish("portal_gun." .. k .. i, v)
		end
	end
end

local function report(i, at, opened)
	local said = i .. " " .. (opened and 1 or 0)
	if at ~= nil then
		said = said .. string.format(" %f %f %f", at[1], at[2], at[3])
	end
	Refii.signal("portal_gun.shot", said)
end

local function fail(i, at)
	report(i, at, false)
	sound_at(SOUNDS.fail, at)
end

-- Where a portal would open along this ray: the place, its normal and up, or nil and where the ray met
-- something, if it met anything. Both a shot and the aim's preview ask this.
local function placement(from, dir, moving)
	local at, normal = surface(from, dir)
	if at == nil then
		return nil
	end
	local up
	if math.abs(normal[3]) > 0.7 then
		up = unit(add(dir, normal, -dot(dir, normal)))   -- on a floor or ceiling, the way the shot went
	else
		up = unit(add({ 0, 0, 1 }, normal, -normal[3]))
	end
	local landed = at
	at = up and fit(at, normal, up)
	if at == nil then
		return nil, landed, normal
	end
	local other = P.Portals[3 - moving]
	if other ~= nil and math.sqrt(dot(add(at, other.at, -1), add(at, other.at, -1))) < 1.2 then
		return nil, landed, normal
	end
	return at, normal, up
end

function P.fire(i, x, y, z, dx, dy, dz)
	local dir = unit({ dx, dy, dz })
	if dir == nil or (i ~= 1 and i ~= 2) then
		return
	end
	sound_on(SOUNDS.launch[i], Refii.player_hero(P.Player))
	local at, normal, up = placement({ x, y, z }, dir, i)
	if at == nil then
		return fail(i, normal)
	end
	P.Portals[i] = { at = add(at, normal, GAP), normal = normal, up = up, right = unit(cross(up, normal)) }
	publish(i)
	report(i, at, true)
	sound_at(SOUNDS.open, at)
end

-- While aiming: where this shot would open, published as portal_gun.aim_x y z, aim_nx ny nz, aim_ux uy uz
-- and portal_gun.aim_state: 0 nothing in range, 1 no room to open, 2 it would open.
function P.preview(i, x, y, z, dx, dy, dz)
	local dir = unit({ dx, dy, dz })
	if dir == nil then
		return
	end
	local at, b, c = placement({ x, y, z }, dir, i)
	local state, normal, up = 2, b, c
	if at == nil then
		state, at, normal = (b ~= nil and 1 or 0), b, c
		if at == nil then
			Refii.publish("portal_gun.aim_state", 0)
			return
		end
		up = math.abs(normal[3]) > 0.7 and unit(add(dir, normal, -dot(dir, normal))) or unit(add({ 0, 0, 1 }, normal, -normal[3]))
		if up == nil then
			Refii.publish("portal_gun.aim_state", 0)
			return
		end
	end
	for k, v in pairs({ aim_x = at[1], aim_y = at[2], aim_z = at[3], aim_nx = normal[1], aim_ny = normal[2],
		aim_nz = normal[3], aim_ux = up[1], aim_uy = up[2], aim_uz = up[3], aim_state = state }) do
		Refii.publish("portal_gun." .. k, v)
	end
end

local function body_of(p, feet)
	-- The part of the hero that meets this surface: the head under a ceiling, the feet on a floor.
	local height = p.normal[3] > 0.7 and 0.1 or (p.normal[3] < -0.7 and 1.8 or 0.9)
	return { feet[1], feet[2], feet[3] + height }
end

local function touching(p, feet)
	local v = add(body_of(p, feet), p.at, -1)
	local depth = dot(v, p.normal)
	return depth > -0.5 and depth < 0.55
		and math.abs(dot(v, p.right)) < HALF_W + 0.1 and math.abs(dot(v, p.up)) < HALF_H + 0.15
end

local function way_out(p)
	if p.normal[3] > 0.7 then
		return add(p.at, p.normal, 0.25)
	elseif p.normal[3] < -0.7 then
		return add(p.at, p.normal, 2.0)
	end
	local out = add(p.at, p.normal, 0.8)
	out[3] = out[3] - HALF_H + 0.03
	return out
end

-- G for player p: portal mode on the gun that player's hero has, or off again. While it is on it is
-- p's alone, and the other player's G does nothing.
function P.toggle(p)
	if P.On and p ~= P.Player then
		return
	end
	local hero = Refii.player_hero(p)
	if hero == nil then
		return
	end
	local _, ranged = Refii.equipped(hero)
	if not P.On and ranged == nil then
		Refii.notice("{PORTAL_GUN_MOD_NAME}", Refii.text("PORTAL_GUN_EQUIP_A_GUN"), 0, p)
		return
	end
	P.Player = p
	P.On = not P.On
	Refii.publish("portal_gun.player", p)
	Refii.publish("portal_gun.mode", P.On and 1 or 0)
	P.Looked = LOOK_EVERY
	-- The camera follows the mode at once (camera.lua states it through the camera API).
	if PortalCamera ~= nil and PortalCamera.State ~= nil then
		PortalCamera.State()
	end
end

-- The hero's ranged weapon as an entity: it stands close by, on the back or in the hands.
local function find_weapon(hero)
	local _, ranged = Refii.equipped(hero)
	local x, y, z = Refii.position(hero)
	if ranged == nil or x == nil then
		return 0
	end
	for _, e in ipairs(Refii.near(x, y, z, 3, false)) do
		if tostring(Refii.definition(e)):lower() == tostring(ranged):lower() then
			return e
		end
	end
	return 0
end

Refii.on("ranged_shot", function(e)
	if e.player ~= P.Player or not P.On then
		return
	end
	Refii.signal("portal_gun.fire", "")
	return true     -- no bullet
end)

Refii.on("tick", function(e)
	P.Rest = math.max(0, P.Rest - e.seconds)
	P.Looked = (P.Looked or 0) + e.seconds
	if P.On and P.Looked >= LOOK_EVERY then
		P.Looked = 0
		local holder = Refii.player_hero(P.Player)
		Refii.publish("portal_gun.weapon", holder and find_weapon(holder) or 0)
	end
	local a, b = P.Portals[1], P.Portals[2]
	local hero = Refii.player_hero(P.Player)
	if a == nil or b == nil or hero == nil or not Refii.living(hero) or Refii.in_cutscene(P.Player) then
		return
	end
	local x, y, z = Refii.position(hero)
	if x == nil then
		return
	end
	local feet = { x, y, z }
	local in_a, in_b = touching(a, feet), touching(b, feet)
	if not in_a and not in_b then
		P.Armed = true
	elseif P.Armed and P.Rest <= 0 then
		local out = way_out(in_a and b or a)
		Refii.set_position(hero, out[1], out[2], out[3])
		P.Armed = false
		P.Rest = REST
		sound_on(SOUNDS.trip, hero)
	end
end)

local function close_both()
	P.Portals = {}
	P.Armed = true
	publish(1)
	publish(2)
end

Refii.on("level_unloading", close_both)
Refii.on("level_loaded", close_both)
