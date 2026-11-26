using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// The keyboard and mouse, as one more device on pad 0: keys become the buttons and stick the
/// guest already reads, and the mouse turns the camera through the camera's own rotate path.
/// </summary>
/// <remarks>
/// THE GUEST HAS ONE CONTROL SCHEME AND IT IS THE PAD'S. `GameControlsCombatOnRelease` over
/// `GameControlsBase` binds every GameAction to an XboxButton (decoded from `sub_821894A8`), and
/// each receiver filters by context — so a key that presses the pad's button reaches exactly
/// what the button reaches, hold semantics included: holding melee blocks and charges a
/// flourish (`FlourishChargeTime`), holding magic charges the spell (`QuickCastTimeSecs`), holding
/// ranged aims and the release fires.
///
/// Fable III's PC scheme where it is known: 1/2/3 put the left mouse button on melee, ranged or
/// magic, and the items sit on the function keys. The rest are the usual PC keys. Every action
/// is rebindable as `key_&lt;action&gt;` in refii.toml — a comma list of Godot key names
/// (`OS.FindKeycodeFromString`) or MouseLeft / MouseRight / MouseMiddle / Mouse4 / Mouse5.
///
/// THE 360 HAS NO KEYBOARD TO REUSE. The control-scheme loader (`sub_82883298`) still builds the
/// PC era's KeyboardKey (InputType 1), MouseButton (2) and mouse-movement (6) bindings, and the
/// GDB's Base scheme still binds keys, but the device scan (`sub_821A8CC8`) recognises an XInput
/// keyboard (capabilities Type 2) and builds no device for it, and the binary imports no
/// keystroke API, so no keyboard or mouse event is ever produced. The mouse-movement binding's
/// value (`sub_829C2358`) is the PC's counts / 64 clamped to unit length — a stick again. So keys
/// press the pad, and the mouse is handed to the camera directly (<see cref="FeedMouseMotion"/>).
/// </remarks>
public partial class GameInput
{
	/// <summary>
	/// One rebindable action: its setting key, default keys, and what it presses. <c>Superseded</c>
	/// lists the action's former defaults: the launcher writes every setting it offers, so an install
	/// holds the default of its day, and a stored value equal to a former default reads as today's.
	/// A stored value's <c>Pad:</c> tokens (<see cref="Refii.Core.Engine_PadActionBinding"/>) are no part of
	/// its keys: the keys are what is left without them.
	/// </summary>
	public readonly record struct KeyAction(string Name, string Defaults, string Description, string[]? Superseded = null)
	{
		/// <summary>What the action presses on pad 0 while it is down.</summary>
		public KeyPress Press { get; init; }

		/// <summary>When the action presses it (<see cref="KeyContext"/>): always, only in play, only in a menu, only while the title's choices are up.</summary>
		public KeyContext When { get; init; }

		private readonly string? _hostAction;

		/// <summary>For <see cref="KeyPress.HostAction"/>: the guest tick action posted once per press (the title's tick pump runs it).</summary>
		public string HostAction
		{
			get => _hostAction ?? string.Empty;
			init => _hostAction = value;
		}

		/// <summary>
		/// Whether the action may also be bound to a pad chord (<c>Pad:</c> tokens in its setting,
		/// <see cref="ApplyPadActions"/>): the Controls page then gives it a controller row. Unbound
		/// by default.
		/// </summary>
		public bool PadBindable { get; init; }

		/// <summary>
		/// For a pad-bindable <see cref="KeyPress.HostAction"/>: the action posted for the player
		/// (1 or 2) whose pad pressed the chord. Null posts <see cref="HostAction"/>.
		/// </summary>
		public System.Func<int, string>? HostActionFor { get; init; }

		/// <summary>Whether <paramref name="stored"/>'s keys mean "the default": empty, today's, or a former one.</summary>
		public bool IsDefault(string? stored)
		{
			string value = Normalised(Refii.Core.Engine_PadActionBinding.KeysOf(stored));
			if (value.Length == 0 || value == Normalised(Defaults))
			{
				return true;
			}

			foreach (string former in Superseded ?? System.Array.Empty<string>())
			{
				if (value == Normalised(former))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The keys in force for <paramref name="stored"/>: today's defaults when it means the default.</summary>
		public string Bound(string? stored) => IsDefault(stored) ? Defaults :
			Refii.Core.Engine_PadActionBinding.HasPadToken(stored) ? Refii.Core.Engine_PadActionBinding.KeysOf(stored) : stored!;

		private static string Normalised(string? list) =>
			string.Join(",", (list ?? string.Empty).Trim().Trim('"').Split(',',
				System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)).ToLowerInvariant();
	}

	/// <summary>What a key action presses.</summary>
	public enum KeyPress
	{
		None, A, B, X, Y, LeftShoulder, RightShoulder, Back, RightThumb, DpadUp, DpadDown, DpadLeft, DpadRight,
		LeftTrigger, RightTrigger,

		/// <summary>The attack mode's button: X, Y or B (<see cref="AttackMode"/>).</summary>
		Attack,
		MeleeMode, RangedMode, MagicMode,
		MoveForward, MoveBack, MoveLeft, MoveRight,

		/// <summary>The stick at half deflection while held.</summary>
		Walk,

		/// <summary>Start in play, B in a menu, each press keeping the meaning it began with (<see cref="Refii.Core.Engine_PauseBackKeys"/>).</summary>
		Pause,

		/// <summary>B, in a menu only (<see cref="Refii.Core.Engine_PauseBackKeys"/>).</summary>
		BackOut,

		/// <summary>Read by photo mode, never the pad.</summary>
		PhotoMode,

		/// <summary>Start itself (a title screen's "press START").</summary>
		Start,

		/// <summary>No pad button: <see cref="KeyAction.HostAction"/> is posted to the guest tick once per press.</summary>
		HostAction,

		/// <summary>The right stick at full deflection left or right while held.</summary>
		RightStickLeft, RightStickRight,
	}

	/// <summary>When a key action presses its button.</summary>
	public enum KeyContext
	{
		Always,

		/// <summary>The world has the input (<see cref="Playing"/>).</summary>
		Play,

		/// <summary>A menu, box or screen has the input, and no map (<see cref="Map"/>).</summary>
		Menu,

		/// <summary>A map of the guest's has the input (<see cref="GameTitleInput.MapUp"/>).</summary>
		Map,

		/// <summary>The title's on-screen choices are up (Fable 3's held expression choices).</summary>
		Choice,

		/// <summary>In play while no choice is up.</summary>
		PlayNoChoice,

		/// <summary>The title's "press START" screen is up.</summary>
		TitleScreen,
	}

	/// <summary>Every action of the running title, in the order the launcher and the Controls page list them.</summary>
	public static KeyAction[] KeyActions => Engine_TitleGame.Active.Input.KeyActions;

	/// <summary>The shell's table — Fable 2's: one action per game button, Fable III's 1/2/3 for the attack modes.</summary>
	public static readonly KeyAction[] DefaultKeyActions =
	{
		new("move_forward", "W", "Move forward (left stick up).") { Press = KeyPress.MoveForward },
		new("move_back", "S", "Move back (left stick down).") { Press = KeyPress.MoveBack },
		new("move_left", "A", "Move left (left stick left).") { Press = KeyPress.MoveLeft },
		new("move_right", "D", "Move right (left stick right).") { Press = KeyPress.MoveRight },
		new("walk", "Alt", "Hold to walk: the stick at half deflection.") { Press = KeyPress.Walk },
		new("sprint", "Shift", "Hold to sprint (A held).") { Press = KeyPress.A },
		new("interact", "E,Space,Enter", "Interact, talk, pick up; accept in menus (A).") { Press = KeyPress.A },
		new("attack", "MouseLeft", "Attack with the selected mode: melee, ranged or magic. Hold " +
			"to block and charge a flourish, to aim, or to charge a spell.") { Press = KeyPress.Attack },
		new("melee_mode", "1", "Put the attack button on melee (Fable III's 1).") { Press = KeyPress.MeleeMode },
		// Fable III PC: 1 melee, 2 magic, 3 ranged (`SwitchToSpellsPressedPC` key 3, `SwitchToRangedPressedPC`
		// key 4 — DirectInput scancodes for 2 and 3; the manual's "2 for Magic mode, or 3 for Ranged mode").
		new("ranged_mode", "3", "Put the attack button on the ranged weapon (Fable III's 3).", new[] { "2" }) { Press = KeyPress.RangedMode },
		new("magic_mode", "2", "Put the attack button on magic (Fable III's 2).", new[] { "3" }) { Press = KeyPress.MagicMode },
		new("melee", "F", "Melee directly, whatever the mode (X).") { Press = KeyPress.X },
		new("ranged", "R", "Ranged directly, whatever the mode (Y).") { Press = KeyPress.Y },
		new("magic", "C", "Magic directly, whatever the mode (B).") { Press = KeyPress.B },
		new("target_lock", "MouseRight,T", "Hold to lock on to a target (left trigger); flick the " +
			"mouse to switch target (a mod's lock may switch by the wheel instead: up left, down right). " +
			"T is the key for it where the mouse buttons mean pointer clicks (menus, the pub games' odds).",
			new[] { "MouseRight" }) { Press = KeyPress.LeftTrigger },
		new("first_person", "MouseMiddle,Z", "Hold for the first-person view (left bumper).") { Press = KeyPress.LeftShoulder },
		new("spells_menu", "Ctrl", "Hold to choose a spell (right trigger).") { Press = KeyPress.RightTrigger },
		new("expression_wheel", "Q", "Hold for the expression wheel.") { Press = KeyPress.Back },
		new("camera_mode", "V", "Toggle the camera mode (right stick click).") { Press = KeyPress.RightThumb },
		new("photo_mode", "F11", "Open or close Photo Mode (on a controller: the Back wheel's top wedge).") { Press = KeyPress.PhotoMode },
		new("pause", "Escape,Tab", "Pause and open the book (Start). Escape is back in menus.") { Press = KeyPress.Pause },
		new("back", "Escape,Backspace", "Back out of a menu (B). Only in menus.") { Press = KeyPress.BackOut },
		new("item_up", "F1,Up", "Item slot up / menu up (D-pad up).") { Press = KeyPress.DpadUp },
		new("item_down", "F2,Down", "Item slot down / menu down (D-pad down).") { Press = KeyPress.DpadDown },
		new("item_left", "F3,Left", "Item slot left / menu left (D-pad left).") { Press = KeyPress.DpadLeft },
		new("item_right", "F4,Right", "Item slot right / menu right (D-pad right).") { Press = KeyPress.DpadRight },
		new("tab_prev", "PageUp,BracketLeft", "Previous tab or page in a menu (left bumper).") { Press = KeyPress.LeftShoulder },
		new("tab_next", "PageDown,BracketRight", "Next tab or page in a menu (right bumper).") { Press = KeyPress.RightShoulder },
	};

	/// <summary>The attack button's mode — Fable III's 1, 2 and 3.</summary>
	private enum AttackMode { Melee, Ranged, Magic }

	private static AttackMode _attackMode = AttackMode.Melee;

	/// <summary>The mode last stated to the guest tick (<see cref="StateAttackMode"/>), 0 for none since it came up.</summary>
	private static int _attackModeStated;

	/// <summary>
	/// The attack mode for <paramref name="player"/> (1 or 2) as the mods read it — 1 melee, 2 ranged,
	/// 3 magic (REFII_ATTACK_MODE_*) — for the player the keyboard's pad plays here (pad 0's
	/// <c>ModPlayer</c>, the player its claims and presses are), else 0.
	/// </summary>
	public static int AttackModeOf(int player) =>
		player is 1 or 2 && player == Engine_TitleGame.Active.Input.ModPlayer(0) ? (int)_attackMode + 1 : 0;

	/// <summary>
	/// States the attack mode to the guest tick (the command <c>attack_mode P MODE</c>, which raises
	/// <c>attack_mode_changed</c> and answers <c>refii_mod_attack_mode</c>) when it differs from the last
	/// statement, and again whenever the guest has come up since.
	/// </summary>
	private static void StateAttackMode()
	{
		if (!HostBridge.IsGuestUp)
		{
			_attackModeStated = 0;
			return;
		}

		int player = Engine_TitleGame.Active.Input.ModPlayer(0);
		int mode = AttackModeOf(player);
		if (player != 0 && mode != _attackModeStated)
		{
			_attackModeStated = mode;
			HostBridge.PostModCommand($"attack_mode {player} {mode}");
		}
	}

	/// <summary>Each action's bound inputs, parsed from refii.toml with the defaults beneath.</summary>
	private static readonly Dictionary<string, List<BoundInput>> _bindings = new();

	/// <summary>Each action's context (<see cref="KeyAction.When"/>), read with the bindings.</summary>
	private static readonly Dictionary<string, KeyContext> _contexts = new();

	/// <summary>One bound input: a key or a mouse button.</summary>
	private readonly record struct BoundInput(Key Key, MouseButton Mouse);

	/// <summary>
	/// Whether the player is in the world with nothing holding the input: no front end, pause
	/// book, owned screen, box or card. Decides what Escape and the mouse buttons mean, and
	/// whether the mouse is captured for the camera. Once per frame.
	/// </summary>
	public static bool Playing { get; private set; }

	/// <summary>The mouse buttons a press made in play holds until released (<see cref="Refii.Core.Engine_PlayButtons"/>).</summary>
	private static readonly Refii.Core.Engine_PlayButtons PlayButtons = new((int)MouseButton.Xbutton2 + 1);

	private static readonly MouseButton[] BindableButtons =
	{
		MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.Xbutton1, MouseButton.Xbutton2,
	};

	/// <summary>Escape and Tab as Start and B, each press keeping the mode it started in.</summary>
	private static readonly Refii.Core.Engine_PauseBackKeys PauseBackKeys = new();

	/// <summary>Reads `key_*` from the values the settings pass already loaded.</summary>
	private static void ReadKeyBindings(IReadOnlyDictionary<string, string> values)
	{
		foreach (KeyAction action in KeyActions)
		{
			string stored = Refii.Core.Engine_RefiiConfig.String(values, "key_" + action.Name, action.Defaults);
			string raw = action.Bound(stored);
			ReadPadBinding(action, stored);
			var inputs = new List<BoundInput>();
			foreach (string part in raw.Split(','))
			{
				if (ParseInput(part.Trim()) is { } input)
				{
					inputs.Add(input);
				}
			}

			_bindings[action.Name] = inputs;
			_contexts[action.Name] = action.When;
		}

		// The controller's remap and the prompts' glyph settings come from the same read.
		ReadControllerSettings(values);
	}

	private static BoundInput? ParseInput(string name)
	{
		switch (name.ToLowerInvariant())
		{
			case "":
				return null;
			case "mouseleft":
			case "mouse1":
				return new BoundInput(Key.None, MouseButton.Left);
			case "mouseright":
			case "mouse2":
				return new BoundInput(Key.None, MouseButton.Right);
			case "mousemiddle":
			case "mouse3":
				return new BoundInput(Key.None, MouseButton.Middle);
			case "mouse4":
				return new BoundInput(Key.None, MouseButton.Xbutton1);
			case "mouse5":
				return new BoundInput(Key.None, MouseButton.Xbutton2);
		}

		Key key = OS.FindKeycodeFromString(name);
		return key == Key.None ? null : new BoundInput(key, MouseButton.None);
	}

	/// <summary>Whether any input bound to an action is down. A mouse button counts from a press
	/// made while the mouse was captured until its release (<see cref="PlayButtons"/>) — a free
	/// cursor's clicks belong to the menus.</summary>
	private static bool Down(string action) => ActionInContext(action) && Held(action);

	/// <summary>Whether an action's context (<see cref="KeyAction.When"/>) holds this frame; an action with none always does.</summary>
	private static bool ActionInContext(string action) =>
		!_contexts.TryGetValue(action, out KeyContext when) || InContext(when);

	/// <summary>Whether any input bound to an action is physically down, whatever the context (<see cref="Down"/> without it).</summary>
	private static bool Held(string action)
	{
		if (!_bindings.TryGetValue(action, out List<BoundInput>? inputs))
		{
			return false;
		}

		foreach (BoundInput input in inputs)
		{
			// A key a mod has CLAIMED (ModInput.Claim) is the mod's: it no longer presses the pad.
			if (input.Key != Key.None && _withheldKeys.Contains(input.Key))
			{
				continue;
			}

			if (input.Key != Key.None
				? Godot.Input.IsKeyPressed(input.Key) || Godot.Input.IsPhysicalKeyPressed(input.Key)
				: MouseInput && PlayButtons.Held((int)input.Mouse))
			{
				return true;
			}
		}

		return false;
	}

	public static bool PhotoModeKeyHeld => Down("photo_mode");

	/// <summary>
	/// Whether a key action a title reads itself is down: one that presses nothing on the pad
	/// (<see cref="KeyPress.None"/>), in its own context, as <see cref="PhotoModeKeyHeld"/> is read —
	/// by a key, or by its pad chord on any player's pad (<see cref="PadActionHeld"/>).
	/// </summary>
	public static bool KeyActionHeld(string action) => Down(action) || PadActionHeld(action);

	/// <summary>The keyboard's state for the debug channel (`input`): the contexts, the device and every action down now.</summary>
	public static object DescribeKeys()
	{
		var down = new List<string>();
		foreach (KeyAction action in KeyActions)
		{
			if (Down(action.Name))
			{
				down.Add(action.Name);
			}
		}

		GameTitleInput title = Engine_TitleGame.Active.Input;
		return new
		{
			playing = Playing, in_game = HostBridge.IsInGame, world_has_input = title.WorldHasInput,
			choices_up = title.ChoicesUp, title_screen_up = title.TitleScreenUp, map_up = title.MapUp,
			keyboard_mouse = Engine_UiInputMode.KeyboardMouse, pointer_free = Engine_UiPointer.Free,
			mouse_mode = Godot.Input.MouseMode.ToString(), actions = KeyActions.Length, down,
			title_input = title.Describe(),
		};
	}

	/// <summary>Whether an action of context <paramref name="when"/> presses now.</summary>
	private static bool InContext(KeyContext when) => when switch
	{
		KeyContext.Play => Playing,
		KeyContext.Menu => !Playing && !Engine_TitleGame.Active.Input.MapUp,
		KeyContext.Map => !Playing && Engine_TitleGame.Active.Input.MapUp,
		KeyContext.Choice => Engine_TitleGame.Active.Input.ChoicesUp,
		KeyContext.PlayNoChoice => Playing && !Engine_TitleGame.Active.Input.ChoicesUp,
		KeyContext.TitleScreen => Engine_TitleGame.Active.Input.TitleScreenUp,
		_ => true,
	};

	/// <summary>
	/// A title overlay is pointed at (Fable 2's expression wheel and spell blade): the cursor is
	/// free while one is held, and the camera stands still under it.
	/// </summary>
	public static bool InOverlay => Engine_TitleGame.Active.Input.PointerOverlay;

	/// <summary>
	/// The play state, the mouse capture and the attack mode, once per frame. The cursor is
	/// captured for the camera only in play with the window focused; anything that takes the
	/// input frees it for the menus.
	/// </summary>
	private void UpdateKeyboardMouse(bool devices)
	{
		Playing = Engine_TitleGame.Active.Input.WorldHasInput;

		// The open dev menu (F6, the one embedded window; its Tracy panels are its tabs) wants
		// the cursor. Its own flag, not `GetEmbeddedSubwindows()`: that builds a Godot Array
		// every frame, a finalizable handle for the collector.
		bool windowOpen = GameDevMenu.WindowOpen;
		// A finger is not a mouse to capture: the touch controls own the screen in play.
		// A guest map's cursor is a stick the mouse drives: captured there too (FeedMouseMotion).
		bool capture = MouseInput && MouseLock && devices && (Playing || MapHasMouse) && !InOverlay && !windowOpen &&
			!GameDevMenu.KeyboardCaptured && !Engine_UiInputMode.Touch;
		Godot.Input.MouseModeEnum wanted =
			capture ? Godot.Input.MouseModeEnum.Captured : Godot.Input.MouseModeEnum.Visible;
		if (Godot.Input.MouseMode != wanted)
		{
			Godot.Input.MouseMode = wanted;
		}

		if (devices)
		{
			bool captured = !Engine_UiPointer.Free;
			foreach (MouseButton button in BindableButtons)
			{
				PlayButtons.Update((int)button, Godot.Input.IsMouseButtonPressed(button), captured);
			}
		}
		else
		{
			PlayButtons.ReleaseAll();
		}

		// The look device, for the guest's camera: while it is the mouse, the camera keeps its
		// heading as the hero moves and a target lock takes the mouse as flicks
		// (Fable2/src/vfs/src/hooks/mouse_look.cpp); a pad keeps the shipped camera, and so does touch,
		// whose drag is posted as the mouse's turn into that camera (GameInput.Touch.cs).
		HostBridge.SetMouseLookDevice(Engine_UiInputMode.Keycaps && MouseInput);

		Engine_UiKeyGlyphs.LabelFor ??= KeyLabel;
		using (Refii.Core.Engine_FramePhases.Measure("input.glyphs"))
		{
			UpdateGlyphDevice();
			Engine_UiKeyGlyphs.Update();
		}

		foreach (KeyAction action in KeyActions)
		{
			if (action.Press is KeyPress.MeleeMode or KeyPress.RangedMode or KeyPress.MagicMode && Down(action.Name))
			{
				_attackMode = action.Press switch
				{
					KeyPress.RangedMode => AttackMode.Ranged,
					KeyPress.MagicMode => AttackMode.Magic,
					_ => AttackMode.Melee,
				};
				break;
			}
		}

		StateAttackMode();
	}

	/// <summary>
	/// The key a pad button stands for right now, for the keycap prompts: the first key bound to
	/// the action that presses that button, in the context that has the input — B is magic in
	/// the world and back in a menu.
	/// </summary>
	public static string? KeyLabel(string button) => GamePhotoMode.Active ? GamePhotoMode.KeyLabel(button) :
		Engine_TitleGame.Active.InteractionOwnsDevices ? Engine_TitleGame.Active.KeyLabel(button) :
		Engine_TitleGame.Active.Input.KeyLabel(button, Shown, Playing) ?? button switch
	{
		"A" => Shown("interact"),
		"B" => Shown(Playing ? "magic" : "back"),
		"X" => Shown("melee"),
		"Y" => Shown("ranged"),
		"LB" => Shown(Playing ? "first_person" : "tab_prev"),
		"RB" => Shown("tab_next"),
		"LT" => Shown("target_lock"),
		"RT" => Shown("spells_menu"),
		"Start" => Shown("pause"),
		"Back" => Shown("expression_wheel"),
		"DPad" => Playing ? "F1-F4" : "Arrows",
		// In a menu the stick prompt sits beside arrows the mouse clicks: no key to show.
		"LS" => Playing ? "WASD" : string.Empty,
		"RS" => Playing ? "Mouse" : "Wheel",
		_ => null,
	};

	/// <summary>
	/// The first input bound to an action that works now, in its short printed form: mouse buttons
	/// press the pad only in play (elsewhere they are the pointer's clicks), so outside play the first
	/// KEY is shown, and a mouse button only when the action has no key.
	/// </summary>
	private static readonly Dictionary<Key, string> KeycodeStrings = new();

	private static string GetCachedKeycodeString(Key key)
	{
		if (!KeycodeStrings.TryGetValue(key, out string? label))
		{
			label = OS.GetKeycodeString(key);
			KeycodeStrings[key] = label;
		}
		return label;
	}

	private static string Shown(string action)
	{
		if (!_bindings.TryGetValue(action, out List<BoundInput>? inputs) || inputs.Count == 0)
		{
			return "-";
		}

		BoundInput first = inputs[0];
		if (!Playing)
		{
			foreach (BoundInput input in inputs)
			{
				if (input.Mouse == MouseButton.None)
				{
					first = input;
					break;
				}
			}
		}
		return first.Mouse switch
		{
			MouseButton.Left => "LMB",
			MouseButton.Right => "RMB",
			MouseButton.Middle => "MMB",
			MouseButton.Xbutton1 => "M4",
			MouseButton.Xbutton2 => "M5",
			_ => first.Key switch
			{
				Key.Escape => "Esc",
				Key.Backspace => "Bksp",
				Key.Pageup => "PgUp",
				Key.Pagedown => "PgDn",
				Key.Bracketleft => "[",
				Key.Bracketright => "]",
				Key.Enter => "Enter",
				_ => GetCachedKeycodeString(first.Key),
			},
		};
	}

	/// <summary>
	/// The captured mouse as the camera: each movement's turn, in degrees at the profile's middle
	/// camera sensitivity, posted as it arrives — Godot's relative motion, summed per frame
	/// (<c>Input.UseAccumulatedInput</c>), with no curve, no averaging and no rate ceiling. The
	/// guest hands everything posted since its last camera update to the active camera, whole, in
	/// that update (Fable2/src/vfs/src/hooks/mouse_look.cpp), with the profile's sensitivity and vertical
	/// invert applied as to the stick; Godot draws the part no update has taken yet the frame the
	/// mouse moves (<see cref="Refii.Core.Engine_MouseLook"/>). The cursor is captured only in play
	/// (<see cref="UpdateKeyboardMouse"/>), so motion here is always the camera's; horizontal
	/// invert is ours, as for the stick.
	/// </summary>
	private static void FeedMouseMotion(InputEvent @event)
	{
		if (!MouseInput)
		{
			return;
		}

		// The cursor is captured in play, or free by choice (mouse_lock off) and still the camera's.
		if (@event is not InputEventMouseMotion motion ||
			(Godot.Input.MouseMode != Godot.Input.MouseModeEnum.Captured && !(!MouseLock && (Playing || MapHasMouse))))
		{
			return;
		}

		if (MapHasMouse)
		{
			MapMouse.Add(Time.GetTicksUsec(), motion.Relative);
			return;
		}

		Vector2 degrees = Refii.Core.Engine_MouseLook.Degrees(motion.Relative, MouseSensitivity, InvertCameraX);
		if (GameWorld.ActiveAimFov > 0f)
		{
			degrees *= ZoomSensitivityScale(GameWorld.ActiveAimFov, MouseZoomSensitivity);
		}
		if (degrees != Vector2.Zero)
		{
			if (GameWorld.HostAimActive)
			{
				_hostAimDelta += degrees;
			}
			else
			{
				HostBridge.PostMouseLook(degrees.X, degrees.Y);
			}
		}
	}

	/// <summary>
	/// The captured mouse's wheel: each notch posted to the guest, down +1 and up -1 (rightward
	/// positive). A notch switches the target of a lock a mod states switches by the wheel
	/// (refii_mod_wheel_switches_target), to the next target right or left (Fable2/src/vfs/src/hooks/mouse_look.cpp); the
	/// guest drops it otherwise. A free cursor's wheel is the menus' (<see cref="Engine_UiPointer"/>),
	/// and nothing else in play reads the wheel.
	/// </summary>
	private static void FeedMouseWheel(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true } button &&
			button.ButtonIndex is MouseButton.WheelDown or MouseButton.WheelUp &&
			Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Captured)
		{
			HostBridge.PostMouseWheel(button.ButtonIndex switch
			{
				MouseButton.WheelDown => 1,
				MouseButton.WheelUp => -1,
				_ => 0,
			});
		}
	}

	/// <summary>The keys the mods' claims withhold from the pad mapping this frame (ModInput.WithheldKeys).</summary>
	private static IReadOnlySet<Key> _withheldKeys = new HashSet<Key>();

	/// <summary>The host actions' key presses: each posts once per press (<see cref="Engine_PressLatch{TInput}"/>).</summary>
	private static readonly Refii.Core.Engine_PressLatch<string> _hostActionPresses = new();

	/// <summary>
	/// Every host action's press this frame: posted on the frame its key goes down in context, and
	/// not again until the key is released. <paramref name="mayRun"/> false (the keyboard is not
	/// play's: the dev menu, a rebind capture or a text entry has it) still latches a press, so a
	/// key held into play is not a new press.
	/// </summary>
	private static void PostHostActions(bool mayRun)
	{
		foreach (KeyAction action in KeyActions)
		{
			if (action.Press == KeyPress.HostAction &&
				_hostActionPresses.Press(action.Name, Held(action.Name), mayRun && ActionInContext(action.Name)) &&
				!string.IsNullOrEmpty(action.HostAction))
			{
				HostBridge.PostFrontEndAction(action.HostAction);
			}
		}
	}

	/// <summary>The pad bit a plain button press sets.</summary>
	private static uint ButtonOf(KeyPress press) => press switch
	{
		KeyPress.A => ButtonA,
		KeyPress.B => ButtonB,
		KeyPress.X => ButtonX,
		KeyPress.Y => ButtonY,
		KeyPress.LeftShoulder => LeftShoulder,
		KeyPress.RightShoulder => RightShoulder,
		KeyPress.Back => Back,
		KeyPress.RightThumb => RightThumb,
		KeyPress.DpadUp => DpadUp,
		KeyPress.DpadDown => DpadDown,
		KeyPress.DpadLeft => DpadLeft,
		KeyPress.DpadRight => DpadRight,
		KeyPress.Start => Start,
		_ => 0u,
	};

	/// <summary>The keyboard as pad 0's buttons, triggers and left stick.</summary>
	private static void KeyboardMouseState(ref PadState state)
	{
		// Typing into the dev menu is not play; nor is the key a rebind row is capturing, nor a
		// name typed into the host's text entry.
		if (GameDevMenu.KeyboardCaptured || Engine_UiInputCapture.Blocking || Refii.Ui.Engine_UiTextEntry.AnyOpen)
		{
			PostHostActions(mayRun: false);
			return;
		}

		_withheldKeys = Refii.Mods.ModInput.WithheldKeys();
		uint buttons = 0;
		bool pause = false, backOut = false, walk = false;
		var move = Vector2.Zero;
		float lookX = 0f;
		PostHostActions(mayRun: true);
		foreach (KeyAction action in KeyActions)
		{
			if (action.Press is KeyPress.HostAction or KeyPress.None or KeyPress.PhotoMode or KeyPress.MeleeMode or KeyPress.RangedMode or
				KeyPress.MagicMode || !Down(action.Name))
			{
				continue;
			}

			switch (action.Press)
			{
				case KeyPress.Attack:
					buttons |= _attackMode switch
					{
						AttackMode.Ranged => ButtonY,
						AttackMode.Magic => ButtonB,
						_ => ButtonX,
					};
					break;
				case KeyPress.LeftTrigger: state.LeftTrigger = 255; break;
				case KeyPress.RightTrigger: state.RightTrigger = 255; break;
				case KeyPress.Pause: pause = true; break;
				case KeyPress.BackOut: backOut = true; break;
				case KeyPress.Walk: walk = true; break;
				case KeyPress.MoveForward: move.Y += 1f; break;
				case KeyPress.MoveBack: move.Y -= 1f; break;
				case KeyPress.MoveRight: move.X += 1f; break;
				case KeyPress.MoveLeft: move.X -= 1f; break;
				case KeyPress.RightStickLeft: lookX -= 1f; break;
				case KeyPress.RightStickRight: lookX += 1f; break;
				default: buttons |= ButtonOf(action.Press); break;
			}
		}

		// Escape pauses in play and backs out in a menu; a held press keeps the meaning it started
		// with (Engine_PauseBackKeys).
		(bool start, bool back) = PauseBackKeys.Map(Playing, pause, backOut);
		if (start) buttons |= Start;
		if (back) buttons |= ButtonB;

		state.Buttons |= buttons;

		// THE KEYS AS THE LEFT STICK: full deflection, the diagonal kept to unit length, half
		// with walk held.
		if (move != Vector2.Zero)
		{
			move = move.Normalized() * (walk ? 0.5f : 1f);
			state.LeftX = Dominant(state.LeftX, Mathf.RoundToInt(move.X * 32767f));
			state.LeftY = Dominant(state.LeftY, Mathf.RoundToInt(move.Y * 32767f));
		}

		if (lookX != 0f)
		{
			state.RightX = Dominant(state.RightX, Mathf.RoundToInt(Mathf.Clamp(lookX, -1f, 1f) * 32767f));
		}

		// A guest map's cursor stick, from the mouse (MapMouseStick).
		if (MapHasMouse)
		{
			Vector2 stick = MapMouse.Stick(Time.GetTicksUsec());
			state.LeftX = Dominant(state.LeftX, Mathf.RoundToInt(stick.X * 32767f));
			state.LeftY = Dominant(state.LeftY, Mathf.RoundToInt(stick.Y * 32767f));
		}
		else
		{
			MapMouse.Clear();
		}
	}

	/// <summary>A guest map has the input (<see cref="GameTitleInput.MapUp"/>): the mouse is its cursor stick.</summary>
	private static bool MapHasMouse => !Playing && Engine_TitleGame.Active.Input.MapUp;

	private static readonly MapMouseStick MapMouse = new();

	/// <summary>
	/// THE MOUSE AS A MAP'S CURSOR STICK. The 360 build reads a map's cursor from the left stick
	/// (Fable III's GameActionWorldMapMoveCursorXBOX, InputType 4) and makes no mouse device, so the
	/// mouse's motion is handed to it as that stick, by the engine's own rule for a mouse-movement
	/// binding: the counts since the last input read over 64, y up, clamped to unit length (Fable 2
	/// TU1 `sub_829C2358`: `v16[0] * 0.015625`, `v16[1] * -0.015625`, then the length clamp at
	/// 0x829c2400). The guest reads its input once per tick at the pump's 30 Hz, so the stick each
	/// frame is the motion of the last 1/30 s — each count is read once whatever the frame rate.
	/// </summary>
	private sealed class MapMouseStick
	{
		private const ulong ReadMicroseconds = 1_000_000UL / 30UL;
		private const float CountsPerUnit = 64f;

		private readonly Queue<(ulong At, Vector2 Counts)> _motion = new();

		public void Add(ulong at, Vector2 counts)
		{
			// Read every frame; the bound only covers frames the window was not focused for.
			if (_motion.Count >= 256)
			{
				_motion.Dequeue();
			}

			_motion.Enqueue((at, counts));
		}

		public void Clear() => _motion.Clear();

		public Vector2 Stick(ulong now)
		{
			while (_motion.Count > 0 && now - _motion.Peek().At > ReadMicroseconds)
			{
				_motion.Dequeue();
			}

			Vector2 sum = Vector2.Zero;
			foreach ((ulong At, Vector2 Counts) entry in _motion)
			{
				sum += entry.Counts;
			}

			var stick = new Vector2(sum.X, -sum.Y) / CountsPerUnit;
			return stick.LengthSquared() > 1f ? stick.Normalized() : stick;
		}
	}
}
