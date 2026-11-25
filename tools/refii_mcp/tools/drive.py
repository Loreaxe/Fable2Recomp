"""Driving the run: the pad, and waiting for the state a press was meant to reach."""
from ..channel import ask
from ..registry import tool

# The guest's own button bits, as XInput numbers them.
BUTTONS = {
    "dpad_up": 0x0001, "dpad_down": 0x0002, "dpad_left": 0x0004, "dpad_right": 0x0008,
    "start": 0x0010, "back": 0x0020,
    "left_thumb": 0x0040, "right_thumb": 0x0080,
    "left_shoulder": 0x0100, "right_shoulder": 0x0200,
    "a": 0x1000, "b": 0x2000, "x": 0x4000, "y": 0x8000,
}


PRESS_MODES = ("scripted", "joypad")
DUMMY_PAD = "dummy"
PAD_VERBS = ("list", "connect", "disconnect")
PAD_FAMILIES = ("xbox", "playstation", "playstation5", "nintendo")
AXES = ("lx", "ly", "rx", "ry", "lt", "rt")


def press_payload(args):
    """The `press` op's request for a `refii_press` call, or an error dict (`ok` False).

    mode 'scripted' (the default) is the request it always was; 'joypad' adds `joypad` and, when
    given, `player` (1 or 2), which names the pad in place of `pad`. pad 'dummy' is the debug
    dummy controller's pad: a joypad press (the mode may be left out) that adds `dummy`.
    """
    dummy = isinstance(args.get("pad"), str) and args["pad"].strip().lower() == DUMMY_PAD
    mode = str(args.get("mode") or ("joypad" if dummy else "scripted")).lower()
    if mode not in PRESS_MODES:
        return {"ok": False, "error": f"mode is one of {', '.join(PRESS_MODES)}"}
    if dummy and mode != "joypad":
        return {"ok": False, "error": "pad='dummy' is a joypad press (mode='joypad')"}
    mask = 0
    unknown = []
    for button in args.get("buttons", []):
        bit = BUTTONS.get(str(button).lower())
        if bit is None:
            unknown.append(button)
        else:
            mask |= bit
    if unknown:
        return {"ok": False, "error": f"unknown button(s): {', '.join(unknown)}",
                "known": sorted(BUTTONS)}
    payload = {"op": "press", "buttons": mask, "frames": args.get("frames", 8)}
    if args.get("pad") is not None and not dummy:
        payload["pad"] = int(args["pad"])
    if args.get("player") is not None:
        if mode != "joypad":
            return {"ok": False, "error": "player goes with mode='joypad' (the scripted mode names a pad)"}
        if args.get("pad") is not None:
            return {"ok": False, "error": "name the player or the pad, not both"}
        player = int(args["player"])
        if player not in (1, 2):
            return {"ok": False, "error": "player is 1 or 2"}
        payload["player"] = player
    for axis in AXES:
        if args.get(axis) is not None:
            payload[axis] = float(args[axis])
    if mask == 0 and not any(axis in payload for axis in AXES):
        return {"ok": False, "error": "no buttons named"}
    if mode == "joypad":
        payload["joypad"] = True
    if dummy:
        payload["dummy"] = True
    return payload


@tool(
    "refii_press",
    "Hold pad buttons for a few frames, as a player would. Goes to the game the handshake "
    "names NOW, so a press cannot land in a previous run. Names: "
    + ", ".join(sorted(BUTTONS))
    + ". Several may be given at once. A press that appears not to land on the title is "
      "usually the armed byte being false — check `refii_frontend`'s canPressA first. "
      "mode='scripted' (default) ORs the buttons into the guest pad directly: it is not a device, "
      "so the prompts keep whatever device was last used. mode='joypad' raises the press as real "
      "joypad events (InputEventJoypadButton / InputEventJoypadMotion through Input.ParseInputEvent) "
      "on the joypad that holds the pad, or would take it on this press: the device switch turns "
      "the prompts to pad glyphs, the joypad's family picks their art, and the guest gets it through "
      "the device path and that player's remap. It passes the focus gate alone, while held and "
      "0.5 s after. A stick with no button does not assign a pad, as on a real controller. "
      "pad='dummy' presses the debug dummy controller (`refii_pads do=connect` first): a joypad "
      "press on a second controller that exists only inside the game, held beside a press on a "
      "real joypad or the scripted pad; player=2 reaches it too while no pad is player two's yet.",
    {
        "buttons": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Button names to hold together.",
        },
        "frames": {
            "type": "integer",
            "description": "Frames to hold (default 8). The guest edge-detects.",
        },
        "mode": {"type": "string", "enum": list(PRESS_MODES),
                 "description": "scripted (default) or joypad (a real joypad event)."},
        "pad": {"type": ["integer", "string"], "description": "Guest pad 0..3 (default 0). START "
                "on a pad that is not player one's is the local co-op drop-in; a scripted press "
                "keeps the pad connected for the rest of the run. 'dummy': the debug dummy "
                "controller's pad, a joypad press."},
        "player": {"type": "integer", "description": "mode='joypad' only: player 1 or 2, the pad "
                   "the title says that player plays on (in place of pad)."},
        "lx": {"type": "number", "description": "Left stick X, -1..1, held like the buttons."},
        "ly": {"type": "number", "description": "Left stick Y, -1..1 (up positive)."},
        "rx": {"type": "number", "description": "Right stick X, -1..1."},
        "ry": {"type": "number", "description": "Right stick Y, -1..1."},
        "lt": {"type": "number", "description": "Left trigger 0..1."},
        "rt": {"type": "number", "description": "Right trigger 0..1."},
    },
    ["buttons"],
)
def press(args):
    payload = press_payload(args)
    if payload.get("ok") is False:
        return payload
    return ask(payload)


def pads_payload(args):
    """The `pads` op's request for a `refii_pads` call, or an error dict (`ok` False).

    `do` list (the default) reads; connect takes the dummy's guest `pad` (0..3) and `family`.
    """
    verb = str(args.get("do") or "list").lower()
    if verb not in PAD_VERBS:
        return {"ok": False, "error": f"do is one of {', '.join(PAD_VERBS)}"}
    payload = {"op": "pads", "do": verb}
    for name in ("pad", "family"):
        if args.get(name) is not None and verb != "connect":
            return {"ok": False, "error": f"{name} goes with do='connect'"}
    if args.get("pad") is not None:
        pad = int(args["pad"])
        if not 0 <= pad <= 3:
            return {"ok": False, "error": "pad is 0..3"}
        payload["pad"] = pad
    if args.get("family") is not None:
        family = str(args["family"]).lower()
        if family not in PAD_FAMILIES:
            return {"ok": False, "error": f"family is one of {', '.join(PAD_FAMILIES)}"}
        payload["family"] = family
    return payload


@tool(
    "refii_pads",
    "The controllers, and the debug dummy controller. do='list' (default): every joypad the game "
    "reads (id, name, known, dummy, family, the guest pad it holds or -1, that pad's player, the "
    "buttons polled now as an XInput mask), the four guest pads (joypad or -1, the title's player "
    "or 0, connected as the guest is told) and the dummy (connected, device, seat, pad, family, "
    "rumble: the pair the guest last stated to it, null while stopped). do='connect' plugs in the "
    "DUMMY: a second controller that exists only inside this game process (no OS device), on the "
    "same roster as a real joypad, so pad assignment, the START drop-in, the prompts' glyph "
    "family, the remap, rumble and unplugging behave as for one. Its first button press takes "
    "guest pad `pad` (default 1, player two's on a local drop-in), never the lowest free pad: "
    "player one of a driven run has no joypad. It passes the focus gate while connected. Drive it "
    "with refii_press pad='dummy'. do='disconnect' releases what it holds and unplugs it: the "
    "guest sees its pad go. Every answer carries the list.",
    {
        "do": {"type": "string", "enum": list(PAD_VERBS)},
        "pad": {"type": "integer", "description": "connect: the guest pad 0..3 the dummy's first "
                "press takes (default 1)."},
        "family": {"type": "string", "enum": list(PAD_FAMILIES),
                   "description": "connect: the controller the dummy states itself as, for the "
                                  "glyph art (default xbox)."},
    },
)
def pads(args):
    payload = pads_payload(args)
    if payload.get("ok") is False:
        return payload
    return ask(payload)


MOUSE_BUTTONS = ("left", "right", "middle")


def mouse_payload(args):
    """The `mouse` op's request for a `refii_mouse` call, or an error dict (`ok` False).

    Wheel notches are signed for the game: negative up, positive down (list order).
    """
    has_x, has_y = args.get("x") is not None, args.get("y") is not None
    if has_x != has_y:
        return {"ok": False, "error": "x and y go together"}
    payload = {"op": "mouse"}
    normalized = bool(args.get("normalized", False))
    if has_x:
        x, y = float(args["x"]), float(args["y"])
        if normalized and not (0.0 <= x <= 1.0 and 0.0 <= y <= 1.0):
            return {"ok": False, "error": "normalized x and y are 0..1"}
        if not normalized and (x < 0.0 or y < 0.0):
            return {"ok": False, "error": "window pixels are not negative"}
        payload.update({"x": x, "y": y, "normalized": normalized})
    click = args.get("click")
    if click is not None:
        click = str(click).lower()
        if click not in MOUSE_BUTTONS:
            return {"ok": False, "error": f"click is one of {', '.join(MOUSE_BUTTONS)}"}
        payload["click"] = click
        payload["frames"] = max(1, int(args.get("frames", 3)))
    wheel = args.get("wheel")
    if wheel is not None:
        wheel = str(wheel).lower()
        if wheel not in ("up", "down"):
            return {"ok": False, "error": "wheel is up or down"}
        steps = int(args.get("steps", 1))
        if steps < 1:
            return {"ok": False, "error": "steps is at least 1"}
        payload["wheel"] = -steps if wheel == "up" else steps
    if not has_x and click is None and wheel is None:
        return {"ok": False, "error": "name a move (x, y), a click or a wheel"}
    return payload


@tool(
    "refii_mouse",
    "The mouse, through Godot's own input pipeline, as a player's would arrive: a move to x, y "
    "(window pixels, or 0..1 of the window with normalized=true), a click (left / right / middle, "
    "held `frames` frames, at x, y when given or where the pointer is), and wheel notches (up / "
    "down, `steps`). A real move or click makes the keyboard and mouse the device, so prompts "
    "turn to keycaps and the menus to the PC list; a pad press turns them back.",
    {
        "x": {"type": "number", "description": "Window pixel X, or 0..1 with normalized."},
        "y": {"type": "number", "description": "Window pixel Y, or 0..1 with normalized."},
        "normalized": {"type": "boolean", "description": "x and y are 0..1 of the window (default false)."},
        "click": {"type": "string", "enum": list(MOUSE_BUTTONS)},
        "frames": {"type": "integer", "description": "Frames a click is held (default 3)."},
        "wheel": {"type": "string", "enum": ["up", "down"]},
        "steps": {"type": "integer", "description": "Wheel notches (default 1)."},
    },
)
def mouse(args):
    payload = mouse_payload(args)
    if payload.get("ok") is False:
        return payload
    return ask(payload)


@tool(
    "refii_wait",
    "Block until the RUNNING game reaches a named state, answered by the game itself the "
    "frame it holds: 'frontend' (title or menu exists), 'frontend-armed' (the title takes A), "
    "'world' (the in-game screen manager exists), 'gameplay' (no screen up), or 'screen' with "
    "a screen id (0 = the pause root). Replaces polling loops that could not see what they "
    "waited for.",
    {
        "for": {
            "type": "string",
            "enum": ["frontend", "frontend-armed", "world", "gameplay", "screen", "log"],
        },
        "screen": {"type": "integer", "description": "With for='screen': the id."},
        "match": {"type": "string",
                  "description": "With for='log': waits for a NEW godot.log line containing "
                                 "this (history never satisfies it)."},
        "timeout": {"type": "integer", "description": "Seconds (default 120, max 570)."},
    },
    ["for"],
)
def wait(args):
    timeout = max(1, min(int(args.get("timeout", 120)), 570))
    return ask(
        {
            "op": "wait",
            "for": args.get("for", ""),
            "screen": args.get("screen", 0),
            "match": args.get("match", ""),
            "timeout": timeout,
        },
        timeout=timeout + 15,
    )


@tool(
    "refii_menu",
    "Drive a menu with the GUEST'S OWN verbs instead of synthesized pad input. verb: "
    "up/down/left/right (the screen's directional codes 28..31; held=false sends the release "
    "32..35 — only the options page answers them, list cursors are Godot's), select (the "
    "screen's A, code 36, on the row Godot states), action (a screen input code, default 36=A; "
    "B's root test reads Godot's fold), event (the row `name` pressed as a player's A: on Fable 2 "
    "the Godot list that holds a VISIBLE row of that name — its name, else its label; type/id pick "
    "among several — highlights it and runs its own A path, the pause levers and row commands "
    "included; answered 'queued', the outcome logged as '[game-ui] menu event'; a row inside a "
    "closed folder is not visible: open the folder first. Titles whose lists are not Godot's post "
    "name/type/id as the guest's MenuEvent).",
    {
        "verb": {"type": "string",
                 "enum": ["up", "down", "left", "right", "select", "action", "event"]},
        "held": {"type": "boolean", "description": "Directions: the press (true) or its release (default false)."},
        "code": {"type": "integer", "description": "action: the input code (36=A, 37=back)."},
        "name": {"type": "string", "description": "event: the row name."},
        "type": {"type": "integer", "description": "event: the event type."},
        "id": {"type": "integer", "description": "event: the row id."},
    },
    ["verb"],
)
def menu(args):
    payload = {"op": "menu"}
    payload.update({key: args[key] for key in
                    ("verb", "held", "code", "name", "type", "id")
                    if args.get(key) is not None})
    return ask(payload)


@tool(
    "refii_action",
    "Post a named action onto the guest's tick-thread queue — the same lever the host's own "
    "menus use (New Game, LoadSave:N, MenuEvent:..., ScreenAction:N, ...). Returns the queue depth; "
    "a depth that climbs means the pump is not draining.",
    {
        "name": {"type": "string", "description": "The action string, verbatim."},
    },
    ["name"],
)
def action(args):
    return ask({"op": "action", "name": str(args.get("name", ""))})


@tool(
    "refii_replay",
    "Record the published pad stream, or replay a tape — a repro becomes a script. "
    "do='start' arms recording, 'stop' saves the tape (returns its path), 'play' feeds a "
    "tape back frame-for-frame (the game-state input masks still apply, because they are "
    "the game's).",
    {
        "do": {"type": "string", "enum": ["start", "stop", "play"]},
        "path": {"type": "string", "description": "stop: save here; play: the tape to feed."},
    },
    ["do"],
)
def replay(args):
    what = args.get("do")
    if what in ("start", "stop"):
        payload = {"op": "input-record", "do": what}
        if args.get("path"):
            payload["path"] = str(args["path"])
        return ask(payload)
    if what == "play":
        if not args.get("path"):
            return {"ok": False, "error": "play needs the tape's path"}
        return ask({"op": "input-replay", "path": str(args["path"])})
    return {"ok": False, "error": "do must be start, stop or play"}
