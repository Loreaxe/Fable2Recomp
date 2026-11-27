extends Node
## First Person: the switch, per player.
##
## The view is the game's own first-person camera held through camera_override: the game's
## per-player camera override (the one its quests use for their first-person shots), which yields to
## a scene's own camera, steps aside for full cutscenes and canned actions, and never reaches a save.
## A hold names the player, so each player of a split screen or an online game has their own.
##
## How the held view behaves is stated beside the hold through camera_tune, for each player this
## machine plays (player_here): fp_view makes it the free-look view (only the look input turns it,
## the body is not faded, a scene's framed point is faced until the player looks away), with the
## field of view, the pitch limit, where the eye sits from the head, how the hero turns to the view
## and whether a scene's own camera is shown through it — the settings of settings.json. The mod
## has no camera Lua of its own.
##
## Holds and tunes are cleared each time the mods' Lua loads, so they are stated again on lua_ready;
## a new game VM also starts each player's toggle again as start_on says.

const MOD := "first_person"
const TOGGLE := "first_person.toggle"
const HOLD_SETTINGS := ["first_person.fov", "first_person.pitch_limit", "first_person.third_person_aim", "first_person.scenes",
	"first_person.eye_forward", "first_person.eye_height", "first_person.face_camera"]
const VIEW_OFF := { "fp_view": false, "fp_fov": false, "fp_pitch_limit": false, "fp_eye_forward": false,
	"fp_eye_height": false, "fp_face": false, "fp_scenes": false }

var host: Object
var first_person := 8
var interactive := 2
var on := [false, false, false]     # per player (1, 2): whether they want first person (the toggle)
var stated := ["", "", ""]          # per player: what was last stated, so an unchanged hold is not stated again
var l3_held := false                # a pad's left stick is clicked: L3 + R3 is the game's Photo mode


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	var named: Dictionary = host.constants()
	first_person = int(named.get("CAMERA_FIRST_PERSON", first_person))
	interactive = int(named.get("CUTSCENE_INTERACTIVE", interactive))
	host.input_action.connect(_on_input_action)
	host.setting_changed.connect(_on_setting_changed)
	host.cutscene.connect(_on_cutscene)
	host.hero_ready.connect(func(_e: Dictionary) -> void: _apply_both())
	host.hero_gone.connect(func(_e: Dictionary) -> void: _apply_both())
	host.lua_ready.connect(func(_e: Dictionary) -> void: _start())
	_start()


func _setting(name: String, fallback: float) -> float:
	return host.setting("first_person." + name, fallback)


# A session's start, and every new game VM: the toggles as start_on says, every hold stated afresh.
func _start() -> void:
	var start := _setting("start_on", 1.0) != 0.0
	on = [false, start, start]
	stated = ["", "", ""]
	_claim_toggle()
	_apply_both()


# R3 is the game's Camera mode, so the toggle takes it from the game. Claims are cleared when the
# mods' Lua loads, so this is stated again on lua_ready. Not while L3 is held: the game's Photo mode
# is L3 + R3, read from the pad the claim would hide R3 from.
func _claim_toggle() -> void:
	host.claim_input(1, TOGGLE, not l3_held)
	host.claim_input(2, TOGGLE, not l3_held)


func _l3_down() -> bool:
	for device in Input.get_connected_joypads():
		if Input.is_joy_button_pressed(device, JOY_BUTTON_LEFT_STICK):
			return true
	return false


func _process(_delta: float) -> void:
	var down := _l3_down()
	if down != l3_held:
		l3_held = down
		_claim_toggle()


# A shared screen is one camera framing both heroes, so neither player holds it there.
func _own_camera() -> bool:
	return host.coop_mode() != 1 or host.split_screen()


# With the scenes setting off an interactive scene (a conversation under the vignette) keeps the
# game's own camera.
func _wanted(p: int) -> bool:
	if not on[p] or not _own_camera():
		return false
	if _setting("scenes", 1.0) == 0.0 and host.in_cutscene(p, interactive):
		return false
	return true


# States player p's hold and how its view behaves, or lets both go. The view is stated before the
# hold, so the camera the hold activates finds it.
func _apply(p: int) -> void:
	var held := _wanted(p)
	var here: bool = host.player_here(p)
	var fov := _setting("fov", 80.0)
	var pitch := _setting("pitch_limit", 75.0)
	var aim := _setting("third_person_aim", 1.0) != 0.0
	var view := {
		"fp_view": 1.0, "fp_fov": fov, "fp_pitch_limit": pitch,
		"fp_eye_forward": _setting("eye_forward", 0.12), "fp_eye_height": _setting("eye_height", 0.05),
		"fp_face": _setting("face_camera", 1.0), "fp_scenes": _setting("scenes", 1.0),
	}
	var now := "%s/%s/%s" % [view, aim, here] if held else "off/%s" % here
	if stated[p] == now:
		return
	stated[p] = now
	if held:
		host.camera_tune(p, view if here else VIEW_OFF, MOD)
		host.camera_override(p, first_person, { "fov": fov, "pitch_limit": pitch, "aim": aim })
	else:
		host.camera_override(p, 0)
		host.camera_tune(p, VIEW_OFF, MOD)


func _apply_both() -> void:
	_apply(1)
	_apply(2)


# H or R3 (input.json "toggle", rebindable in Options > Controls): whoever pressed it.
func _on_input_action(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if e.get("name", "") == TOGGLE and e.get("pressed", false) and not l3_held and (p == 1 or p == 2):
		on[p] = not on[p]
		_apply(p)
		print("first person %s for player %d" % ["on" if on[p] else "off", p])


# A hold's options are stated again when the setting behind one changes.
func _on_setting_changed(key: String, _value: float) -> void:
	if key in HOLD_SETTINGS:
		_apply_both()


# An interactive scene starting or ending (the scenes setting).
func _on_cutscene(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if int(e.get("kind", 0)) == interactive and (p == 1 or p == 2):
		_apply(p)
