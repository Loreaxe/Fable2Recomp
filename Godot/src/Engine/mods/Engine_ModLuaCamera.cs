namespace Refii.Mods;

/// <summary>
/// THE CAMERA MOD API's STATEMENTS, in the Lua preamble both titles run: what a mod states about a
/// player's camera (<see cref="ModApi.CameraShapeMeaning"/>) is kept here, in the game VM, as plain
/// values; each title's core camera chunk reads them at the points its camera scripts read their own
/// (Fable 2: CoopHeroLua's camera chunk; Fable 3: Fable3CoreLua's <c>Refii._wrap_camera</c>).
/// </summary>
/// <remarks>
/// <para>THE STORE. <c>Refii._cam[p]</c> for player 1 and 2: <c>layers[slot][owner] = { value,
/// serial }</c>, and the values IN FORCE resolved from them — <c>tune</c> (key → number),
/// <c>frame</c>, <c>focus</c>, <c>combat</c>, <c>interest</c>, <c>pitch</c>, <c>lock</c>,
/// <c>deny</c> (mode → true), <c>frame_serial</c>, <c>any</c>. A slot's value in force is the one
/// stated LAST among the owners still stating it, so a mod that lets its statement go gives the
/// slot back to the mod under it. The owner is the mod (a mod's block calls through its view,
/// <c>Refii._as_mod</c>), <c>godot:&lt;owner&gt;</c> for a Godot script, the empty name otherwise.</para>
/// <para>NEVER SAVED. The store hangs off <c>Refii</c>, a Pluto permanent written by name
/// (<see cref="ModLua.RequireRuntime"/>), and holds numbers, booleans and strings only: nothing of
/// it is reachable from the save's tables. It is made new with every preamble run (a new game VM, a
/// reload of the mods), so a mod states its camera when it loads.</para>
/// <para>THE CORE'S HOOK. <c>Refii._camera_changed(p)</c>, when the title's chunk has set it, is
/// called after every statement with the values in force already resolved.</para>
/// </remarks>
public static class ModLuaCamera
{
	/// <summary>The calls a mod's view carries with the mod as owner, in catalog order.</summary>
	public static readonly string[] Calls =
	{
		"camera_tune", "camera_frame", "camera_focus", "camera_combat_blend", "camera_interest",
		"camera_pitch_hold", "camera_lock_frame", "camera_mode_allow", "camera_offset", "camera_shake",
	};

	/// <summary>The read calls the preamble answers (from the title's <c>Refii._camera_read</c>, and the zoom record).</summary>
	public static readonly string[] Reads =
	{
		"camera_pose", "camera_stated", "camera_zoom", "camera_screen_mode", "camera_split", "camera_view", "camera_ray",
	};

	/// <summary>The preamble text; appended after <see cref="ModLua.RequireRuntime"/>.</summary>
	public const string Runtime = """
		Refii._cam = { { layers = {} }, { layers = {} }, serial = Refii._cam ~= nil and Refii._cam.serial or 0 }
		Refii._camera_calls = { 'camera_tune', 'camera_frame', 'camera_focus', 'camera_combat_blend', 'camera_interest', 'camera_pitch_hold', 'camera_lock_frame', 'camera_mode_allow', 'camera_offset', 'camera_shake' }
		function Refii._camera_resolve(p)
			local s = Refii._cam[p]
			local tune, deny, any = nil, nil, false
			local top = {}
			for slot, layers in pairs(s.layers) do
				local best = nil
				for _, layer in pairs(layers) do
					if best == nil or layer.serial > best.serial then best = layer end
				end
				if best ~= nil then
					any = true
					local _, _, kind, key = string.find(slot, '^(%a+)%.(.+)$')
					if kind == 'tune' then
						tune = tune or {}
						tune[key] = best.value
					elseif kind == 'deny' then
						deny = deny or {}
						deny[tonumber(key)] = true
					else
						top[slot] = best
					end
				end
			end
			s.tune, s.deny, s.any = tune, deny, any
			s.frame = top.frame ~= nil and top.frame.value or nil
			s.frame_serial = top.frame ~= nil and top.frame.serial or nil
			s.focus = top.focus ~= nil and top.focus.value or nil
			s.combat = top.combat ~= nil and top.combat.value or nil
			s.interest = top.interest ~= nil and top.interest.value or nil
			s.pitch = top.pitch ~= nil and top.pitch.value or nil
			s.lock = top.lock ~= nil and top.lock.value or nil
			s.offset = top.offset ~= nil and top.offset.value or nil
			Refii._camera_offset_state(p)
			if Refii._camera_changed ~= nil then Refii._camera_changed(p) end
		end
		function Refii._camera_offset_state(p)
			if Refii._camera_offset_stated == nil then return end
			local o = Refii._cam[p].offset
			if o ~= nil then
				Refii._camera_offset_stated(p, true, o.right, o.up, o.back, o.yaw, o.pitch, o.roll, o.fov)
			else
				Refii._camera_offset_stated(p, false, 0, 0, 0, 0, 0, 0, 0)
			end
		end
		function Refii._camera_put(owner, p, slot, value)
			local s = (p == 1 or p == 2) and Refii._cam[p] or nil
			if s == nil then return false end
			owner = type(owner) == 'string' and owner or ''
			local layers = s.layers[slot]
			if layers == nil then
				if value == nil then return true end
				layers = {}
				s.layers[slot] = layers
			end
			if value == nil and layers[owner] == nil then return true end
			Refii._cam.serial = Refii._cam.serial + 1
			layers[owner] = value ~= nil and { value = value, serial = Refii._cam.serial } or nil
			if next(layers) == nil then s.layers[slot] = nil end
			Refii._camera_resolve(p)
			return true
		end
		Refii._camera_do = {}
		function Refii._camera_do.camera_tune(owner, p, t)
			local s = (p == 1 or p == 2) and Refii._cam[p] or nil
			if s == nil then return false end
			owner = type(owner) == 'string' and owner or ''
			if t == nil or t == false then
				local mine = {}
				for slot, layers in pairs(s.layers) do
					if string.sub(slot, 1, 5) == 'tune.' and layers[owner] ~= nil then mine[#mine + 1] = slot end
				end
				for _, slot in ipairs(mine) do Refii._camera_put(owner, p, slot, nil) end
				return true
			end
			if type(t) ~= 'table' then return false end
			for k, v in pairs(t) do
				if type(k) == 'string' then
					if v == true then v = 1 end
					Refii._camera_put(owner, p, 'tune.' .. k, type(v) == 'number' and v or nil)
				end
			end
			return true
		end
		function Refii._camera_do.camera_frame(owner, p, t)
			local frame = nil
			if type(t) == 'table' then
				frame = { levels = t.levels == 'all' and 'all' or 'first' }
				for _, k in ipairs({ 'distance', 'interior_scale', 'height', 'focus_height', 'slot', 'track' }) do
					if type(t[k]) == 'number' then frame[k] = t[k] end
				end
			end
			return Refii._camera_put(owner, p, 'frame', frame)
		end
		function Refii._camera_do.camera_focus(owner, p, t)
			local focus = nil
			if type(t) == 'table' then
				focus = { right = type(t.right) == 'number' and t.right or 0, up = type(t.up) == 'number' and t.up or 0,
					ease = type(t.ease) == 'number' and t.ease or 1 }
			end
			return Refii._camera_put(owner, p, 'focus', focus)
		end
		function Refii._camera_do.camera_combat_blend(owner, p, weight)
			return Refii._camera_put(owner, p, 'combat', type(weight) == 'number' and weight or nil)
		end
		function Refii._camera_do.camera_interest(owner, p, t)
			local interest = nil
			if type(t) == 'table' then interest = { foes = t.foes ~= false, passers = t.passers ~= false } end
			return Refii._camera_put(owner, p, 'interest', interest)
		end
		function Refii._camera_do.camera_pitch_hold(owner, p, on, o)
			local pitch = nil
			if on == true or (type(on) == 'number' and on ~= 0) then
				o = type(o) == 'table' and o or {}
				pitch = { min = type(o.min) == 'number' and o.min or nil, max = type(o.max) == 'number' and o.max or nil,
					shared = o.shared == true }
			end
			return Refii._camera_put(owner, p, 'pitch', pitch)
		end
		function Refii._camera_do.camera_lock_frame(owner, p, on, o)
			local lock = nil
			if on == true or (type(on) == 'number' and on ~= 0) then
				o = type(o) == 'table' and o or {}
				lock = { strength = type(o.strength) == 'number' and o.strength or nil, tilt = o.tilt ~= false }
			end
			return Refii._camera_put(owner, p, 'lock', lock)
		end
		function Refii._camera_do.camera_mode_allow(owner, p, mode, allow)
			if type(mode) ~= 'number' then return false end
			return Refii._camera_put(owner, p, 'deny.' .. mode, allow == false and true or nil)
		end
		function Refii._camera_do.camera_offset(owner, p, t)
			local offset = nil
			if type(t) == 'table' then
				offset = {}
				for _, k in ipairs({ 'right', 'up', 'back', 'yaw', 'pitch', 'roll', 'fov' }) do
					offset[k] = type(t[k]) == 'number' and t[k] or 0
				end
			end
			return Refii._camera_put(owner, p, 'offset', offset)
		end
		Refii._shake_kinds = { banshee = 0, troll = 1, explosion = 2, quake = 3, magic_charge = 4, magic_cast = 5, combat = 6 }
		function Refii._camera_do.camera_shake(owner, p, t)
			if (p ~= 1 and p ~= 2) or type(t) ~= 'table' or type(Player) ~= 'table' then return false end
			if Refii.camera_pose == nil or Refii.camera_pose(p) == nil then return false end
			local hero = Refii.entity ~= nil and Refii.player_hero ~= nil and Refii.entity(Refii.player_hero(1)) or nil
			if hero == nil then return false end
			local kind = t.kind
			if type(kind) == 'string' then kind = Refii._shake_kinds[kind] end
			if type(kind) ~= 'number' then kind = 3 end
			local function n(v, d) return type(v) == 'number' and v or d end
			local call, spec
			if type(t.fade) == 'number' then
				call, spec = Player.FadeScreenShake, { ID = kind, DecayTime = t.fade }
			else
				call, spec = Player.AddScreenShake, { ID = kind, MaxLevel = n(t.level, 1), AttackTime = n(t.attack, 0),
					DecayTime = n(t.decay, 0.5), Duration = n(t.seconds, 1), LeaveOpen = t.open == true,
					Cumulative = t.add == true, DoNetwork = false }
			end
			if type(call) ~= 'function' then return false end
			local partner = p == 2 and Refii._partner_screen ~= nil
			if partner then Refii._partner_screen(true) end
			local ok = pcall(call, hero, spec)
			if partner then Refii._partner_screen(false) end
			return ok
		end
		function Refii._camera_call(owner, name, ...)
			local call = Refii._camera_do[name]
			if call == nil then return false end
			return call(owner, ...)
		end
		for _, name in ipairs(Refii._camera_calls) do
			Refii[name] = function(...) return Refii._camera_call('', name, ...) end
		end
		function Refii._camera_view(view, mod)
			for _, name in ipairs(Refii._camera_calls) do
				rawset(view, name, function(...) return Refii._camera_call(mod, name, ...) end)
			end
		end
		if Refii._camera_changed ~= nil then
			Refii._camera_changed(1)
			Refii._camera_changed(2)
		end
		Refii._camera_offset_state(1)
		Refii._camera_offset_state(2)
		Refii._cam_zoom = {}
		function Refii._camera_zoom_record(camera, p, hero)
			local level = camera.ScaledActiveZoomLevel
			if (p ~= 1 and p ~= 2) or type(level) ~= 'table' then return end
			local set = type(camera.CurrentZoomSet) == 'table' and camera.CurrentZoomSet.Name or nil
			set = type(set) == 'string' and set or ''
			local z = Refii._cam_zoom[p] or {}
			Refii._cam_zoom[p] = z
			z.set, z.level = set, type(camera.ZoomLevel) == 'number' and camera.ZoomLevel or 1
			z.combat = type(camera.ZoomLevelCombatWeight) == 'number' and camera.ZoomLevelCombatWeight or 0
			local function n(v) return type(v) == 'number' and v or 0 end
			z.string_length, z.cage_radius, z.max_cage_radius = n(level.StringLength), n(level.CageRadius), n(level.MaxCageRadius)
			z.height, z.focal_offset, z.fov, z.max_coop_distance = n(level.HeightOffset), n(level.FocalOffset), n(level.FOV), n(level.MaxCoopDistance)
			if Refii._camera_zoom_stated ~= nil and hero ~= nil then
				Refii._camera_zoom_stated(hero, set, z.level, z.combat, z.string_length, z.cage_radius, z.max_cage_radius,
					z.height, z.focal_offset, z.fov, z.max_coop_distance)
			end
		end
		function Refii.camera_zoom(p)
			local z = Refii._cam_zoom[p]
			if z == nil and p == 2 and not Refii.camera_split() then z = Refii._cam_zoom[1] end
			if z == nil then return nil end
			local copy = {}
			for k, v in pairs(z) do copy[k] = v end
			return copy
		end
		function Refii.camera_pose(p)
			if Refii._camera_read == nil then return nil end
			local x, y, z, fx, fy, fz, ux, uy, uz, fov, vfov, cut = Refii._camera_read(1, p)
			if x == nil then return nil end
			return { x = x, y = y, z = z, fx = fx, fy = fy, fz = fz, ux = ux, uy = uy, uz = uz, fov = fov, vfov = vfov, cut = cut ~= 0 }
		end
		function Refii.camera_stated(p)
			if Refii._camera_read == nil then return nil end
			local x, y, z, fx, fy, fz, ux, uy, uz, fov, cut = Refii._camera_read(2, p)
			if x == nil then return nil end
			return { x = x, y = y, z = z, fx = fx, fy = fy, fz = fz, ux = ux, uy = uy, uz = uz, fov = fov, cut = cut ~= 0 }
		end
		function Refii.camera_view(p)
			if Refii._camera_read == nil then return nil end
			return Refii._camera_read(3, p)
		end
		function Refii.camera_screen_mode()
			local mode = Refii._camera_read ~= nil and Refii._camera_read(4, 0) or 0
			if mode == 1 then return 'split_vertical' elseif mode == 2 then return 'split_horizontal' elseif mode == 3 then return 'hybrid' end
			return 'shared'
		end
		function Refii.camera_split()
			if Refii._camera_read == nil then return false end
			local _, split = Refii._camera_read(4, 0)
			return split == 1
		end
		function Refii.camera_ray(p, x, y)
			if Refii._camera_read == nil then return nil end
			return Refii._camera_read(5, p, x, y)
		end

		""";
}
