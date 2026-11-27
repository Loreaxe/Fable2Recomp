extends Node
## Portal Gun: the aim and the view through each portal.
##
## G puts the hero's own gun into portal mode: it keeps the game's stance and aim, and draws the Portal
## Gun's model. When its shot goes off, mod/init.lua cancels the bullet and asks here for the aim (the
## fire signal): along the camera's line, back to init.lua, which finds where it lands and publishes the
## portal. While portal mode holds in play, the pad buttons the mouse would press otherwise are claimed
## (LT, the lock-on, and X and B, the attack button in the other modes), and each mouse button presses
## the game's ranged button, Y, so it aims and fires whichever attack mode the keyboard is on: the left
## blue, the right orange.
## Each portal is a quad showing what a second camera sees: that camera stands where the player's
## would stand if the other portal were this one, turned to look out of it, and draws the same world
## into its own viewport at a fraction of the screen.
##
## Portal mode is one player's at a time: the one whose G turned it on (mod/init.lua's portal_gun.player).
## Their buttons are claimed and pressed, their hero aims, and the mouse is theirs only where this
## machine's keyboard plays them (attack_mode).
##
## A shot shows as a muzzle flash, then, once init.lua has said where it ended, a beam to there and a
## burst of sparks, with the portal scaling open. init.lua plays the sounds.

const COLOURS := [Color(0.2, 0.62, 1.0), Color(1.0, 0.55, 0.12)]
const SIZE := Vector2(1.1, 2.0)
const PREVIEW_EVERY := 0.08         # seconds between asks of where the aim lands
const OUTLINE_SHADER := """
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled, shadows_disabled, blend_add, depth_test_disabled;

// Where a portal would open: its oval's edge, with a faint fill.
uniform vec3 shade = vec3(0.2, 0.6, 1.0);

void fragment() {
	float d = length((UV - vec2(0.5)) * 2.0);
	if (d > 1.0) {
		discard;
	}
	ALBEDO = shade;
	ALPHA = mix(0.08, 0.95, smoothstep(0.88, 0.96, d));
}
"""
const SHADER := """
shader_type spatial;
render_mode unshaded, cull_back, fog_disabled, shadows_disabled;

// One portal on a quad: the view out of the other through the oval, or a slow swirl while it has no
// partner, inside a glowing rim of the portal's colour.
uniform sampler2D view : source_color, repeat_disable, filter_linear;
uniform bool open = false;
uniform vec3 rim = vec3(0.2, 0.6, 1.0);

void fragment() {
	vec2 at = (UV - vec2(0.5)) * 2.0;
	float d = length(at);
	if (d > 1.0) {
		discard;
	}
	vec3 inside;
	if (open) {
		inside = texture(view, SCREEN_UV).rgb;
	} else {
		float turn = atan(at.y, at.x) * 3.0 + d * 9.0 - TIME * 2.0;
		inside = rim * (0.12 + 0.2 * (0.5 + 0.5 * sin(turn)));
	}
	float edge = smoothstep(0.86, 0.97, d);
	float pulse = 0.85 + 0.15 * sin(TIME * 3.0 + atan(at.y, at.x) * 2.0);
	ALBEDO = mix(inside, rim * 1.6 * pulse, edge);
}
"""
const BEAM_SECONDS := 0.18
const OPEN_SECONDS := 0.22
const MODEL := "game://art/inventory/weapons/portal_gun/portal_gun.mdl"
const RAY_LENGTH := 120.0
const TAP_SECONDS := 0.5            # how long a touch shot holds the game's ranged button: shorter only raises the gun
const TAP_AIM_SECONDS := 1.5        # how long the aim a touch shot was let go on waits for the shot
const PAD_REARM_SECONDS := 0.35     # the game's button stays up this long after a trigger's shot

var host: Object
var canvas: Control
var player := 1                     # whose portal mode: portal_gun.player
var colour := 1                     # the next shot's portal, 1 or 2
var outline: MeshInstance3D         # where the aim would open a portal
var outline_material: ShaderMaterial
var preview_wait := 0.0
var left_held := false
var right_held := false
var claimed := false
var y_claimed := false              # Y, on a pad or the screen, while portal mode holds
var pad_aim := false                # a controller's Y is down: the game aims, the outline shows, LT or RT fires
var pad_rearm := 0.0                # seconds after a trigger's shot before Y's hold goes back to the game
var pad_held := 0.0                 # seconds the game's button has been held this time; a shorter press won't fire
var pad_queued := 0                 # a trigger pulled too soon: its portal, fired once the gun is up
var shot_void_left := 0.0           # seconds in which the game's next shot is Y let go without firing: no portal
var on_pad := false                 # the controller was used last, rather than the mouse or keyboard
var tap_left := 0.0                 # seconds a touch shot still presses the game's ranged button
var tap_ray: Array = []             # the camera's line when a touch shot was let go: the shot's aim
var tap_ray_left := 0.0
var published_next := -1            # the colour last published for Y's caption, 0 for none
var model: Mesh
var dressed: MeshInstance3D         # the gun drawn as the Portal Gun
var dressed_mesh: Mesh              # what it drew before
var gun_glow: StandardMaterial3D    # the model's second surface: the tube, the lens and the shell's rim
var loading := false
var interactive := 2                # the cutscene kind that also takes the player's hands
var portals: Array = [{}, {}]
var flash := [0.0, 0.0]
var clock := 0.0
var muzzle := Vector3.ZERO
var aim := Vector3.FORWARD
var fading: Array[Dictionary] = []    # { node, material, left, seconds, colour, grow }


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	interactive = int(host.constants().get("CUTSCENE_INTERACTIVE", interactive))
	host.input_action.connect(_on_input_action)
	host.mod_signal.connect(_on_mod_signal)
	host.lua_ready.connect(_on_lua_ready)
	host.level_unloading.connect(_on_level_unloading)
	host.level_loaded.connect(func(_e): loading = false)
	var layer := CanvasLayer.new()
	layer.layer = 6                     # over the UI (5), under the loading screen (8)
	add_child(layer)
	canvas = Control.new()
	canvas.mouse_filter = Control.MOUSE_FILTER_IGNORE
	canvas.set_anchors_preset(Control.PRESET_FULL_RECT)
	canvas.draw.connect(_draw_crosshair)
	layer.add_child(canvas)
	var shader := Shader.new()
	shader.code = SHADER
	for i in 2:
		portals[i] = _build(i, shader)
	_build_outline()


func _build(i: int, shader: Shader) -> Dictionary:
	var root := Node3D.new()
	root.visible = false
	add_child(root)
	var quad := MeshInstance3D.new()
	var mesh := QuadMesh.new()
	mesh.size = SIZE
	quad.mesh = mesh
	quad.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var material := ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("rim", Vector3(COLOURS[i].r, COLOURS[i].g, COLOURS[i].b))
	quad.material_override = material
	root.add_child(quad)
	var seen := VisibleOnScreenNotifier3D.new()
	seen.aabb = AABB(Vector3(-SIZE.x * 0.5, -SIZE.y * 0.5, -0.1), Vector3(SIZE.x, SIZE.y, 0.2))
	root.add_child(seen)
	var viewport := SubViewport.new()
	viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
	viewport.handle_input_locally = false
	viewport.gui_disable_input = true
	viewport.positional_shadow_atlas_size = 2048
	root.add_child(viewport)
	var camera := Camera3D.new()
	viewport.add_child(camera)
	material.set_shader_parameter("view", viewport.get_texture())
	return { "root": root, "quad": quad, "material": material, "seen": seen, "viewport": viewport, "camera": camera, "opening": 0.0 }


## A reload clears the claims.
func _on_lua_ready(_e) -> void:
	claimed = false
	y_claimed = false
	published_next = -1


func _on_level_unloading(_e: Dictionary) -> void:
	loading = true
	for p: Dictionary in portals:
		p.root.visible = false
		p.viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED


func _on_input_action(e: Dictionary) -> void:
	var from := int(e.get("player", 1))
	var name: String = e.get("name", "")
	if name == "portal_gun.toggle":
		if e.get("pressed", false) and host.in_play(from) and not host.in_cutscene(from, interactive):
			host.run_lua("PortalGun.toggle(%d)" % from)
		return
	if from != player:
		return
	# aim_orange is the same Y, there only for Y's caption while orange is next.
	if name == "portal_gun.aim_blue":
		if host.touch_mode():
			_touch_fire(e.get("pressed", false))
		else:
			_pad_aim(e.get("pressed", false))
		return
	if not e.get("pressed", false):
		return
	if not _in_play():
		return
	if name == "portal_gun.swap" and claimed:
		colour = 3 - colour
	elif name == "portal_gun.fire_blue":
		_pad_fire(1)
	elif name == "portal_gun.fire_orange":
		_pad_fire(2)


func _on_mod_signal(mod: String, name: String, payload: String) -> void:
	if mod != "portal_gun":
		return
	match name:
		"fire":
			if shot_void_left > 0.0:
				shot_void_left = 0.0
			else:
				_fire(colour)
		"shot":
			_on_shot(payload)


## The buttons are the mod's only while portal mode holds in play; otherwise they are the game's, and a
## menu has its own B back. Y is the mod's on a pad and a touchscreen. A mouse's clicks would press the
## game's lock, melee and magic through its own mapping, so those wait out portal mode; on a controller
## melee and magic stay the game's, and the triggers are the gun's only while Y aims.
func _claim() -> void:
	if not claimed:
		player = maxi(1, int(_value("portal_gun.player")))
	var wanted: bool = _value("portal_gun.mode") >= 0.5 and _in_play()
	if wanted != claimed:
		claimed = wanted
		if not wanted:
			_let_go()      # a menu takes the mouse, and with it the button's release
	var touch: bool = host.touch_mode()
	var mouse: bool = claimed and not touch and not on_pad
	var triggers: bool = claimed and not touch and pad_aim
	host.claim_input(player, "portal_gun.fire_blue", mouse or triggers)
	host.claim_input(player, "portal_gun.fire_orange", triggers)
	host.claim_input(player, "portal_gun.no_melee", mouse)
	host.claim_input(player, "portal_gun.no_magic", mouse)
	if claimed != y_claimed:
		y_claimed = claimed
		host.claim_input(player, "portal_gun.aim_blue", y_claimed)
	_publish_next()


## Which colour Y's caption names: portal_gun.blue_next and orange_next, published as they change.
func _publish_next() -> void:
	var next := colour if claimed else 0
	if next != published_next:
		published_next = next
		host.publish("portal_gun.blue_next", 1.0 if next == 1 else 0.0)
		host.publish("portal_gun.orange_next", 1.0 if next == 2 else 0.0)


func _let_go() -> void:
	left_held = false
	right_held = false
	if pad_aim and pad_rearm <= 0.0:
		shot_void_left = TAP_AIM_SECONDS
	pad_aim = false
	pad_rearm = 0.0
	pad_queued = 0
	_press_gun()


## Each button aims and fires its own portal, holding the game's ranged button while either is down.
func _input(event: InputEvent) -> void:
	if event is InputEventJoypadButton:
		on_pad = true
	elif event is InputEventJoypadMotion:
		on_pad = on_pad or absf(event.axis_value) > 0.4
	elif event is InputEventKey or event is InputEventMouseButton:
		on_pad = false
	var click := event as InputEventMouseButton
	if click == null or not claimed or Input.mouse_mode != Input.MOUSE_MODE_CAPTURED or host.attack_mode(player) == 0:
		return
	if click.pressed and not _in_play():
		return
	if click.button_index == MOUSE_BUTTON_LEFT:
		left_held = click.pressed
		if left_held:
			colour = 1
	elif click.button_index == MOUSE_BUTTON_RIGHT:
		right_held = click.pressed
		if right_held:
			colour = 2
	else:
		return
	_press_gun()


## On a touchscreen the screen's Y fires the colour its caption names in portal mode (input.json's
## aim_blue, claimed), and the Swap Colour pill changes it: held, the outline shows where the portal
## would open, the thumb sliding off the button turns the camera; let go, a short press of the game's
## ranged button fires. The game's Y is NOT held while aiming: held long, the gun's own aim takes the
## camera over, zooms in behind the hero and turns its aim point instead of the camera the shot
## follows, which a thumb on the screen can't steer. A controller's stick can: see _pad_aim.
func _touch_fire(pressed: bool) -> void:
	if pressed and (not claimed or not _in_play()):
		return
	var was_aiming := left_held
	left_held = pressed
	if not pressed and was_aiming and claimed and _in_play():
		# The game turns its camera to the shot while the button is down, so the aim is the line let go on.
		tap_ray = _ray()
		tap_ray_left = TAP_AIM_SECONDS
		tap_left = TAP_SECONDS
		host.press(player, "Y", 1)


## A controller's Y holds the game's own aim for as long as it is down, down the sights and turned by the
## stick. The game fires on letting go, so letting Y go without a trigger makes no portal.
func _pad_aim(pressed: bool) -> void:
	if pressed == pad_aim or (pressed and (not claimed or not _in_play())):
		return
	pad_aim = pressed
	pad_held = 0.0
	pad_queued = 0
	if not pressed and pad_rearm <= 0.0:
		shot_void_left = TAP_AIM_SECONDS
	_press_gun()


## While Y aims, LT fires blue and RT orange along the camera's line as it is now. A pull before the gun is
## up waits until it is.
func _pad_fire(which: int) -> void:
	if not pad_aim or pad_rearm > 0.0:
		return
	if pad_held < TAP_SECONDS:
		pad_queued = which
		return
	_pad_shoot(which)


## The game's button goes up, which is its shot, and back down after a moment for the next one while Y is
## still held.
func _pad_shoot(which: int) -> void:
	colour = which
	pad_queued = 0
	pad_held = 0.0
	shot_void_left = 0.0
	tap_ray = _ray()
	tap_ray_left = TAP_AIM_SECONDS
	pad_rearm = PAD_REARM_SECONDS
	_press_gun()


## Lets go of the game's ranged button once a touch shot's press is over, and forgets its aim later.
func _tap(delta: float) -> void:
	shot_void_left = maxf(0.0, shot_void_left - delta)
	if pad_rearm > 0.0:
		pad_rearm -= delta
		if pad_rearm <= 0.0:
			_press_gun()
	elif pad_aim:
		pad_held += delta
		if pad_queued != 0 and pad_held >= TAP_SECONDS:
			_pad_shoot(pad_queued)
	if tap_ray_left > 0.0:
		tap_ray_left -= delta
		if tap_ray_left <= 0.0:
			tap_ray = []
	if tap_left <= 0.0:
		return
	tap_left -= delta
	if tap_left <= 0.0:
		_press_gun()


func _press_gun() -> void:
	host.press(player, "Y", 1 if left_held or right_held or (pad_aim and pad_rearm <= 0.0) else 0)


func _exit_tree() -> void:
	for action in ["portal_gun.fire_blue", "portal_gun.fire_orange", "portal_gun.no_melee", "portal_gun.no_magic"]:
		host.claim_input(player, action, false)
	if claimed:
		host.press(player, "Y", 0)
	if y_claimed:
		host.claim_input(player, "portal_gun.aim_blue", false)


func _in_play() -> bool:
	return host.in_play(player) and not host.in_cutscene(player, interactive)


## The game's axes from Godot's, and back: X is mirrored and Y and Z trade places.
func _to_guest(v: Vector3) -> Vector3:
	return Vector3(-v.x, v.z, v.y)


func _to_godot(v: Vector3) -> Vector3:
	return Vector3(-v.x, v.z, v.y)


## The line through the middle of the portal player's own view (camera_ray: their half on a split screen),
## in Godot's axes, starting level with the hero so that what stands between the camera and them is not
## what is hit. Empty with no picture of that player drawn here.
func _ray() -> Array:
	var line: Dictionary = host.camera_ray(player)
	if line.is_empty():
		return []
	var along := _to_godot(line["direction"]).normalized()
	var from := _to_godot(line["origin"])
	var body = host.node_for(host.hero(player))
	if body is Node3D:
		from += along * maxf(0.0, along.dot(body.global_position - from))
	return [from, along, body]


## The pose the portal player's picture is drawn with (camera_pose), as a Godot transform, and its
## vertical angle; empty with none drawn here.
func _eye() -> Dictionary:
	var pose: Dictionary = host.camera_pose(player)
	if pose.is_empty():
		return {}
	var forward := _to_godot(pose["forward"]).normalized()
	var up := _to_godot(pose["up"]).normalized()
	var back := -forward
	var right := up.cross(back).normalized()
	var basis := Basis(right, back.cross(right).normalized(), back)
	return { "transform": Transform3D(basis, _to_godot(pose["position"])), "fov": float(pose["vfov"]) }


func _fire(which: int) -> void:
	var ray := tap_ray if not tap_ray.is_empty() else _ray()
	tap_ray = []
	if ray.is_empty():
		return
	var from: Vector3 = ray[0]
	var along: Vector3 = ray[1]
	var body = ray[2]
	muzzle = body.global_position + Vector3.UP * 1.3 + along * 0.6 if body is Node3D else from
	aim = along
	_launched(which - 1)
	var a := _to_guest(from)
	var d := _to_guest(along)
	host.run_lua("PortalGun.fire(%d,%f,%f,%f,%f,%f,%f)" % [which, a.x, a.y, a.z, d.x, d.y, d.z])


func _aiming() -> bool:
	return _value("portal_gun.mode") >= 0.5 and _in_play() and \
		(left_held or right_held or pad_aim)


func _build_outline() -> void:
	var shader := Shader.new()
	shader.code = OUTLINE_SHADER
	outline_material = ShaderMaterial.new()
	outline_material.shader = shader
	outline = MeshInstance3D.new()
	var mesh := QuadMesh.new()
	mesh.size = SIZE
	outline.mesh = mesh
	outline.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	outline.material_override = outline_material
	outline.visible = false
	add_child(outline)


## While aiming, asks where the shot would land, and shows the portal's outline there: red with no room.
func _update_outline(delta: float) -> void:
	if not _aiming():
		outline.visible = false
		preview_wait = 0.0
		return
	preview_wait -= delta
	if preview_wait <= 0.0:
		preview_wait = PREVIEW_EVERY
		var ray := _ray()
		if not ray.is_empty():
			var a := _to_guest(ray[0])
			var d := _to_guest(ray[1])
			host.run_lua("PortalGun.preview(%d,%f,%f,%f,%f,%f,%f)" % [colour, a.x, a.y, a.z, d.x, d.y, d.z])
	var state := int(_value("portal_gun.aim_state"))
	var normal := _to_godot(Vector3(_value("portal_gun.aim_nx"), _value("portal_gun.aim_ny"), _value("portal_gun.aim_nz")))
	var up := _to_godot(Vector3(_value("portal_gun.aim_ux"), _value("portal_gun.aim_uy"), _value("portal_gun.aim_uz")))
	if state == 0 or normal.length() < 0.5 or up.length() < 0.5:
		outline.visible = false
		return
	var at := _to_godot(Vector3(_value("portal_gun.aim_x"), _value("portal_gun.aim_y"), _value("portal_gun.aim_z")))
	outline.global_transform = Transform3D(Basis(up.cross(normal).normalized(), up.normalized(), normal.normalized()), at + normal.normalized() * 0.04)
	var shade: Color = COLOURS[colour - 1] if state == 2 else Color(1.0, 0.2, 0.15)
	outline_material.set_shader_parameter("shade", Vector3(shade.r, shade.g, shade.b))
	outline.visible = true


func _process(delta: float) -> void:
	clock += delta
	_claim()
	_tap(delta)
	var eye := _eye()
	for i in 2:
		_read(i)
		flash[i] = maxf(0.0, flash[i] - delta)
		var p: Dictionary = portals[i]
		p.opening = maxf(0.0, p.opening - delta)
		p.quad.scale = Vector3.ONE * lerpf(1.0, 0.05, pow(p.opening / OPEN_SECONDS, 2.0))
	_update_outline(delta)
	_fade(delta)
	_dress_gun()
	var both: bool = portals[0].get("on", false) and portals[1].get("on", false)
	for i in 2:
		_view(i, eye, both)
	canvas.queue_redraw()


func _value(key: String) -> float:
	var v: float = host.get_value(key)
	return 0.0 if is_nan(v) else v


func _read(i: int) -> void:
	var n := i + 1
	var p: Dictionary = portals[i]
	p.on = _value("portal_gun.on%d" % n) >= 0.5 and not loading
	p.root.visible = p.on
	if not p.on:
		return
	var normal := _to_godot(Vector3(_value("portal_gun.nx%d" % n), _value("portal_gun.ny%d" % n), _value("portal_gun.nz%d" % n)))
	var up := _to_godot(Vector3(_value("portal_gun.ux%d" % n), _value("portal_gun.uy%d" % n), _value("portal_gun.uz%d" % n)))
	var at := _to_godot(Vector3(_value("portal_gun.x%d" % n), _value("portal_gun.y%d" % n), _value("portal_gun.z%d" % n)))
	if normal.length() < 0.5 or up.length() < 0.5:
		return
	p.root.global_transform = Transform3D(Basis(up.cross(normal).normalized(), up.normalized(), normal.normalized()), at)


## The view out of portal i: the portal player's drawn pose (camera_pose) carried through the pair. The
## world, environment and far plane are the viewport camera's (Godot's render state, not the pose).
func _view(i: int, pose: Dictionary, both: bool) -> void:
	var p: Dictionary = portals[i]
	p.material.set_shader_parameter("open", both)
	var viewport: SubViewport = p.viewport
	var camera := canvas.get_viewport().get_camera_3d()
	var drawn: Transform3D = pose.get("transform", Transform3D.IDENTITY)
	var near_enough: bool = camera != null and not pose.is_empty() and p.on and \
		drawn.origin.distance_to(p.root.global_position) < host.setting("portal_gun.view_range", 60.0)
	if not both or not near_enough or not p.seen.is_on_screen() or not _in_play():
		viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
		return
	var screen := canvas.get_viewport_rect().size
	var wanted := Vector2i(screen * clampf(host.setting("portal_gun.quality", 0.5), 0.25, 1.0))
	if viewport.size != wanted:
		viewport.size = wanted
	if viewport.world_3d != camera.get_world_3d():
		viewport.world_3d = camera.get_world_3d()
	var here: Transform3D = p.root.global_transform
	var there: Transform3D = portals[1 - i].root.global_transform
	var turned := there * Transform3D(Basis(Vector3.UP, PI), Vector3.ZERO)
	var eye: Camera3D = p.camera
	eye.global_transform = turned * here.affine_inverse() * drawn
	eye.fov = float(pose.get("fov", camera.fov))
	eye.environment = camera.environment
	eye.attributes = camera.attributes
	eye.far = camera.far
	# Nothing between the exit and the eye: the wall the exit is on stands there.
	eye.near = maxf(0.05, absf((eye.global_position - there.origin).dot(there.basis.z)) - 0.05)
	eye.current = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS


func _launched(i: int) -> void:
	_spark(muzzle, aim, COLOURS[i], 8, 0.18)
	_flash_light(muzzle, COLOURS[i])


## What init.lua said about a shot, "which opened x y z" with no place when it met nothing: a beam to
## where it ended, and what happened there.
func _on_shot(payload: String) -> void:
	var said := payload.split(" ")
	var i := clampi(said[0].to_int() - 1, 0, 1)
	var opened := said[1] == "1"
	var hit := said.size() >= 5
	if not opened:
		flash[i] = 0.5
	var end := muzzle + aim * RAY_LENGTH
	if hit:
		end = _to_godot(Vector3(said[2].to_float(), said[3].to_float(), said[4].to_float()))
	var shade: Color = COLOURS[i] if opened else Color(1.0, 0.25, 0.15)
	_beam(muzzle, end, COLOURS[i])
	if not hit:
		return
	var back := (muzzle - end).normalized()
	_spark(end, back, shade, 28 if opened else 12, 0.45)
	_flash_light(end, shade)
	if opened:
		portals[i].opening = OPEN_SECONDS


func _glow_material(colour: Color) -> StandardMaterial3D:
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	material.blend_mode = BaseMaterial3D.BLEND_MODE_ADD
	material.cull_mode = BaseMaterial3D.CULL_DISABLED
	material.albedo_color = colour
	return material


func _beam(from: Vector3, to: Vector3, colour: Color) -> void:
	var length := from.distance_to(to)
	if length < 0.2:
		return
	var mesh := CylinderMesh.new()
	mesh.top_radius = 0.035
	mesh.bottom_radius = 0.035
	mesh.height = length
	mesh.radial_segments = 8
	mesh.rings = 1
	var node := MeshInstance3D.new()
	node.mesh = mesh
	node.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var material := _glow_material(colour)
	node.material_override = material
	add_child(node)
	var along := (to - from) / length
	var side := along.cross(Vector3.UP)
	if side.length() < 0.01:
		side = along.cross(Vector3.RIGHT)
	side = side.normalized()
	node.global_transform = Transform3D(Basis(side, along, side.cross(along)), (from + to) * 0.5)
	fading.append({ "node": node, "material": material, "left": BEAM_SECONDS, "seconds": BEAM_SECONDS, "colour": colour })


func _spark(at: Vector3, towards: Vector3, colour: Color, amount: int, life: float) -> void:
	var sparks := CPUParticles3D.new()
	var dot := SphereMesh.new()
	dot.radius = 0.025
	dot.height = 0.05
	dot.radial_segments = 6
	dot.rings = 3
	dot.material = _glow_material(colour)
	sparks.mesh = dot
	sparks.amount = amount
	sparks.lifetime = life
	sparks.one_shot = true
	sparks.explosiveness = 1.0
	sparks.direction = towards if towards.length() > 0.01 else Vector3.UP
	sparks.spread = 55.0
	sparks.initial_velocity_min = 1.5
	sparks.initial_velocity_max = 5.0
	sparks.gravity = Vector3(0, -6, 0)
	sparks.scale_amount_min = 0.5
	sparks.scale_amount_max = 1.2
	add_child(sparks)
	sparks.global_position = at
	sparks.emitting = true
	sparks.finished.connect(sparks.queue_free)


func _flash_light(at: Vector3, colour: Color) -> void:
	var light := OmniLight3D.new()
	light.light_color = colour
	light.light_energy = 3.0
	light.omni_range = 5.0
	add_child(light)
	light.global_position = at
	fading.append({ "node": light, "left": 0.2, "seconds": 0.2, "energy": 3.0 })


func _fade(delta: float) -> void:
	for n in range(fading.size() - 1, -1, -1):
		var f: Dictionary = fading[n]
		f.left -= delta
		if f.left <= 0.0:
			f.node.queue_free()
			fading.remove_at(n)
			continue
		var share: float = f.left / f.seconds
		if f.has("energy"):
			f.node.light_energy = f.energy * share
		else:
			f.material.albedo_color = Color(f.colour, share)


## Draws the hero's gun as the Portal Gun while portal mode holds, and as itself otherwise. Looked at
## every frame, because the gun's node can be rebuilt under us.
func _dress_gun() -> void:
	var wanted: MeshInstance3D = null
	if _value("portal_gun.mode") >= 0.5:
		var node = host.node_for(int(_value("portal_gun.weapon")))
		if node is MeshInstance3D:
			wanted = node
		elif node is Node:
			var inside: Array = node.find_children("*", "MeshInstance3D", true, false)
			wanted = inside.front() if not inside.is_empty() else null
	if dressed != null and is_instance_valid(dressed) and dressed != wanted:
		if dressed.mesh == model and model.get_surface_count() > 1:
			dressed.set_surface_override_material(1, null)
		dressed.mesh = dressed_mesh
	if dressed != wanted or (wanted != null and wanted.mesh != model):
		if wanted != null:
			if model == null:
				model = load(MODEL) as Mesh
			if model != null and wanted.mesh != model:
				dressed_mesh = wanted.mesh
				wanted.mesh = model
				if model.get_surface_count() > 1:
					wanted.set_surface_override_material(1, _gun_glow())
				print("portal_gun: dressed %s, %d surfaces" % [wanted.name, model.get_surface_count()])
		dressed = wanted
	if gun_glow != null:
		gun_glow.albedo_color = COLOURS[colour - 1].lerp(Color.WHITE, 0.3)


## What glows on the gun shines in the colour of the portal it would open next, whatever the light.
func _gun_glow() -> StandardMaterial3D:
	if gun_glow == null:
		gun_glow = StandardMaterial3D.new()
		gun_glow.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
		gun_glow.disable_fog = true
	return gun_glow


## A small exact crosshair in the shot's colour at the centre of the aim, while aiming, and in red when
## a shot could not open.
func _draw_crosshair() -> void:
	if not _aiming() and flash[colour - 1] <= 0.0:
		return
	if _value("portal_gun.mode") < 0.5 or not _in_play():
		return
	var view := canvas.get_viewport_rect().size
	var unit := view.y / 720.0
	var mark: Color = COLOURS[colour - 1]
	if flash[colour - 1] > 0.0:
		mark = Color(1, 0.2, 0.15)
	var at := view * 0.5
	for direction: Vector2 in [Vector2.UP, Vector2.DOWN, Vector2.LEFT, Vector2.RIGHT]:
		canvas.draw_line(at + direction * 4.0 * unit, at + direction * 9.0 * unit, Color(0.03, 0.02, 0.01, 0.85), 3.0 * unit, true)
		canvas.draw_line(at + direction * 4.0 * unit, at + direction * 9.0 * unit, mark, 1.4 * unit, true)
	canvas.draw_circle(at, 1.2 * unit, mark, true, -1.0, true)
