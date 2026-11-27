namespace Refii.Titles.Fable3;

using System.Linq;
using Refii.Game;
using KeyAction = Refii.Game.GameInput.KeyAction;
using KeyContext = Refii.Game.GameInput.KeyContext;
using KeyPress = Refii.Game.GameInput.KeyPress;

/// <summary>
/// Fable 3's part in the pads: the guest sees every pad as it is, but for the host's text entry
/// (<see cref="Fable3Keyboard"/>) — while the console's keyboard is up the game gets no input, so
/// every pad is published neutral, and on the hand-back the buttons still down stay hidden until
/// released, so the A that accepted is not a fresh press in the game. And the keyboard and mouse
/// as Fable III's PC release binds them (<see cref="KeyActions"/>). The host's Controls page
/// (<see cref="Fable3HostOptions"/>) holds the guest's pad neutral the same way.
/// </summary>
public sealed class Fable3Input : GameTitleInput
{
	public static Fable3Input Instance { get; } = new();

	/// <summary>
	/// Per pad: the buttons hidden from the guest until they are released — those down when the
	/// world got the input back from anything that had it (<see cref="WorldHasInput"/>: a guest
	/// screen, box or menu, a map, one of ours), and after the host's text entry and boxes.
	/// </summary>
	private readonly Refii.Core.Engine_HandBackHold _handBack = new(4);

	/// <summary>The HUD's levers, made from each pad as it is published to the guest (<see cref="Fable3HudInput"/>).</summary>
	private readonly Fable3HudInput _hud = new();
	private readonly System.Collections.Generic.List<string> _hudLevers = new();

	public override Published PublishPad(int pad, bool connected, GameInput.PadState seen, GameInput.PadState guest)
	{
		Published published = Masked(pad, connected, seen, guest);
		if (Refii.Host.HostBridge.IsGuestUp)
		{
			Fable3UiStatements.TopBoxMessage? top = Fable3UiStatements.TopBox.Current;
			_hudLevers.Clear();
			_hud.Step(pad,
				Fable3HudInput.Mask(published.Packet.Buttons, published.Packet.LeftTrigger, published.Packet.RightTrigger),
				System.Environment.TickCount64,
				new Fable3HudInput.Surfaces(Fable3WorldIcons.ChoicesUp, top is { ShowA: true }, top is { ShowY: true },
					Fable3UiStatements.InteractionPad.Owned, PlayOf(pad), Fable3PointerStatements.GuestHoldsThePad,
					Fable3HudInput.SanctuaryHolds(Fable3ActingPlayer.OfPad(pad), Fable3GuiWorld.ListUp, Fable3GuiWorld.Player)),
				_hudLevers);
			foreach (string lever in _hudLevers)
			{
				Refii.Host.HostBridge.PostFrontEndAction(lever);
			}
		}

		return published;
	}

	/// <summary>The pad as the guest is to see it, behind this title's masks.</summary>
	private Published Masked(int pad, bool connected, GameInput.PadState seen, GameInput.PadState guest)
	{
		// THE HAND-BACK MASK, for every surface at once: a button that went down while anything but
		// the world had the input is not the world's when the world has it again (Engine_HandBackHold).
		// Followed before every mask below, so a surface that returns early is still a hand-back.
		_handBack.Follow(WorldHasInput);

		// The mods' wheel (Fable3BoxesModWheel): Back while it has it, the whole pad while it stands.
		if (Fable3Boxes.FollowModWheel(ModPlayer(pad), ref guest, WorldHasInput))
		{
			return new(new GameControllerPacket(pad, connected, 0u, 0u, 0u, 0, 0, 0, 0), guest.Buttons, 0u, 0u);
		}

		// A mod's own screen (ModScreen) has the pad; only Start reaches the game, to enter and leave the pause.
		if (Refii.Mods.ModScreen.Owned && Refii.Host.HostBridge.IsInGame)
		{
			uint start = guest.Buttons & GameInput.Start;
			return new(new GameControllerPacket(pad, connected, start, 0u, 0u, 0, 0, 0, 0), guest.Buttons, 0u, 0u);
		}

		if (Fable3Keyboard.IsOpen || Fable3HostOptions.IsOpen || Fable3Boxes.RefiiUp || Fable3Boxes.ModMenuUp)
		{
			return new(new GameControllerPacket(pad, connected, 0u, 0u, 0u, 0, 0, 0, 0), guest.Buttons, 0u, 0u);
		}

		uint hidden = _handBack.Hidden(pad, guest.Buttons);
		guest.Buttons &= ~hidden;
		guest.PhysicalButtons &= ~hidden;
		guest.OtherButtons &= ~hidden;

		// The world map's buttons are Godot's levers, as the map's owner (Fable3MapInput) — player one's
		// hands when the owner has no pad here; the pad stays published for the map's analog actions.
		Fable3MapInput.Drive(pad, connected, guest.Buttons, lever => Refii.Host.HostBridge.PostFrontEndAction(lever));

		// The mouse's pick on a guest menu, walked by the guest's own cursor (Fable3PointerSteps).
		if (pad == 0)
		{
			(int stickY, bool accept) = Fable3PointerSteps.Held();
			if (stickY != 0)
			{
				guest.LeftY = stickY;
				guest.LeftX = 0;
			}

			if (accept)
			{
				guest.Buttons |= GameInput.ButtonA;
			}
		}

		// The Sanctuary's pad is Godot's: its presses become `guiworld:` levers (the guest's pad reaches
		// the mode's handlers and is answered there without a consequence, interaction_guiworld.cpp) —
		// only while nothing of the guest's is ahead of the mode (Fable3HudInput.SanctuaryTakesPad); a
		// pad not fed starts again from what it holds, with no edge.
		if (Fable3HudInput.SanctuaryTakesPad(Fable3GuiWorld.Up, Fable3ScreenBoxes.Showing, Fable3PointerStatements.GuestHoldsThePad))
		{
			Fable3GuiWorld.Feed(pad, guest);
		}
		return base.PublishPad(pad, connected, seen, guest);
	}

	/// <summary>
	/// Guest pad 0 is player one's and pad 1 the local player two's (<see cref="Fable3ActingPlayer"/>):
	/// the mods' claims and presses, the pad chords, the remap and the mods' wheel follow it, and the
	/// host surfaces read that pad for player two (GameInput.PadOfPlayer).
	/// </summary>
	public override int ModPlayer(int pad) => Fable3ActingPlayer.LocalOfPad(pad);

	public override void HoldUntilReleased() => _handBack.HoldAll();

	/// <summary>
	/// The world has the input in play with nothing of the guest's or ours over it — THE gate of every
	/// world lever (<see cref="Fable3HudInput"/>). The guest's own words that one of its surfaces has
	/// the pad (<see cref="Fable3PointerStatements"/>): the UI mode on top of the presented game (a
	/// box's pause form, the menu box, the save menu), the GUI's pause (a GUI screen: the epilogue,
	/// the credits) and a box's hold on the heroes ("DisBox", the tail of its Show). The HUD root at state 2 (`@Hud.Root`,
	/// TU3 sub_824CD7E8 / disc sub_825C1CB0) stays a term: 17 Shows make that call, some with neither
	/// hold (the box types 5..8 hold nothing). Under any of them the keys take their menu meanings and
	/// the cursor is free for the menus (<see cref="Refii.Ui.Engine_UiPointer.Free"/>). An interaction
	/// list with the stick (<see cref="Fable3InteractionList.HasPad"/>) is one more.
	/// </summary>
	public override bool WorldHasInput => WorldHasInputBesidesTheLists && !Fable3InteractionList.HasPad;

	/// <summary><see cref="WorldHasInput"/> without its last term, a list with the stick: the terms that hold every player's pad.</summary>
	private bool WorldHasInputBesidesTheLists => base.WorldHasInput && !Fable3PointerStatements.GuestHoldsThePad &&
		!Fable3Elements.HudHidden && !MapHasInput &&
		!Fable3Keyboard.IsOpen && !Fable3HostOptions.IsOpen && !Fable3Boxes.RefiiUp && !Fable3Boxes.ModMenuUp &&
		!Refii.Mods.ModScreen.Owned;

	/// <summary>
	/// The world has guest pad <paramref name="pad"/>'s player's input (<see cref="Fable3HudInput.PlayOf"/>): as
	/// <see cref="WorldHasInput"/>, except that a Sanctuary list standing for the OTHER player takes nothing of
	/// this one's — both players are principals.
	/// </summary>
	private bool PlayOf(int pad) => Fable3HudInput.PlayOf(Fable3ActingPlayer.OfPad(pad), WorldHasInputBesidesTheLists,
		Fable3InteractionList.HasPad, Fable3GuiWorld.ListUp,
		Fable3UiStatements.InteractionListUp && Fable3UiStatements.InteractionList.Count > 0, Fable3GuiWorld.Player);

	public override bool PointerOverlay => Fable3Keyboard.IsOpen || Fable3HostOptions.IsOpen;

	/// <summary>
	/// A map table is presented (<see cref="Fable3MapTable.Presented"/>) with nothing of ours over it:
	/// the map has the pad, its cursor on the left stick (GameActionWorldMapMoveCursorXBOX, InputType 4),
	/// so the mouse drives that stick and the keys take the map's meanings.
	/// </summary>
	public override bool MapUp => MapHasInput && !Fable3Keyboard.IsOpen && !Fable3HostOptions.IsOpen;

	/// <summary>
	/// A presented map table reads the input — unless it is the world map's scene left standing after
	/// its game mode was popped (<see cref="Fable3MapTable.Stranded"/>), which nothing reads.
	/// </summary>
	private static bool MapHasInput => Fable3MapTable.Presented && !Fable3MapTable.Stranded;

	/// <summary>Expression choices take the keys only while the world has the input: a screen over them has its own.</summary>
	public override bool ChoicesUp => Fable3WorldIcons.ChoicesUp && WorldHasInput;

	public override bool TitleScreenUp => Fable3Splash.Showing;

	/// <summary>
	/// THE KEYBOARD AND MOUSE AS FABLE III PC BINDS THEM, each pressing the 360 button the shipped
	/// scheme gives that action. The PC defaults are the PC build's own (its globals.gdb,
	/// `PCKeyboardAndMouse` / `GameControlsPC`, DirectInput codes): WASD, LShift run, E interact,
	/// Space roll, the left button attacking with the mode 1/2/3 picked, the right button the
	/// secondary, LCtrl first person, Esc the Sanctuary, F1-F4 the d-pad shortcuts; in menus 1/2/3
	/// are A/X/Y (its prompts show those keycaps) with Esc back; while expression choices are up
	/// 1-5 pick choices 1-5 and the left button accepts the first. The pad side is the 360 scheme
	/// the guest runs (GDB 0x50ac87b1 over 0x8c1585e4 over Base 0xcb96641a): A interact / sprint /
	/// dive and choice 1, X melee and block (choice 2), Y ranged aim and fire (choice 3), B magic,
	/// RB the quick menu (choice 4), LB first person (choice 5), LT target lock and zoom aim, RT
	/// held, RS the camera mode, Start pause, Back the nickname; the roll is A in combat.
	/// </summary>
	private static readonly KeyAction[] Actions =
	{
		new("move_forward", "W", "Move forward (left stick up).") { Press = KeyPress.MoveForward },
		new("move_back", "S", "Move back (left stick down).") { Press = KeyPress.MoveBack },
		new("move_left", "A", "Move left (left stick left).") { Press = KeyPress.MoveLeft },
		new("move_right", "D", "Move right (left stick right).") { Press = KeyPress.MoveRight },
		new("walk", "Alt", "Hold to walk: the stick at half deflection.") { Press = KeyPress.Walk },
		new("sprint", "Shift", "Hold to run (A held).") { Press = KeyPress.A, When = KeyContext.Play },
		new("interact", "E", "Interact, talk, pick up (A).") { Press = KeyPress.A, When = KeyContext.Play },
		new("roll", "Space", "Roll in combat (A).") { Press = KeyPress.A, When = KeyContext.Play },
		new("attack", "MouseLeft", "Attack with the mode 1/2/3 picked: melee, magic or ranged. Hold to block, " +
			"charge or aim.") { Press = KeyPress.Attack, When = KeyContext.PlayNoChoice },
		new("melee_mode", "1", "Put the attack button on melee.") { Press = KeyPress.MeleeMode, When = KeyContext.PlayNoChoice },
		// Fable III PC: 1 melee, 2 magic, 3 ranged (`SwitchToSpellsPressedPC` key 3 / `SwitchToRangedPressedPC`
		// key 4, DirectInput scancodes for 2 and 3).
		new("ranged_mode", "3", "Put the attack button on the ranged weapon.", new[] { "2" }) { Press = KeyPress.RangedMode, When = KeyContext.PlayNoChoice },
		new("magic_mode", "2", "Put the attack button on magic.", new[] { "3" }) { Press = KeyPress.MagicMode, When = KeyContext.PlayNoChoice },
		new("melee", "F", "Melee directly, whatever the mode (X).") { Press = KeyPress.X, When = KeyContext.Play },
		new("ranged", "R", "Ranged directly, whatever the mode (Y).") { Press = KeyPress.Y, When = KeyContext.Play },
		new("magic", "C", "Magic directly, whatever the mode (B).") { Press = KeyPress.B, When = KeyContext.Play },
		new("target_lock", "MouseRight,T", "Hold to lock on to a target, or to zoom-aim the ranged weapon (left trigger).")
			{ Press = KeyPress.LeftTrigger, When = KeyContext.Play },
		new("first_person", "Ctrl", "Hold for the first-person view (left bumper).") { Press = KeyPress.LeftShoulder, When = KeyContext.Play },
		// Fable III PC's Tab looks at what the game points out: on the 360 that is the right trigger held
		// (sub_8276A038 at 0x8276a9b0 starts the look on raw button 16; action 95 ends it at 0x828d9c18).
		new("look_at", "Tab", "Hold to look at what the game points out (right trigger).") { Press = KeyPress.RightTrigger, When = KeyContext.Play },
		new("quick_menu", "Q", "The right bumper in play (the quick menu).", new[] { "Tab" }) { Press = KeyPress.RightShoulder, When = KeyContext.Play },
		new("camera_mode", "V", "Toggle the camera mode (right stick click).") { Press = KeyPress.RightThumb, When = KeyContext.Play },
		new("nickname", "Backspace", "The Back button: change your nickname; in co-op, return to the host.")
			{ Press = KeyPress.Back, When = KeyContext.Play },
		// TWO FUNCTIONS THE PC BUILD PUTS ON KEYS AND THE 360 SCHEME DOES NOT BIND (GameControlsPC:
		// `QuickSavePC`, `ToggleSafetyMode`), each run on the 360 guest's own function
		// (Fable3/src/hooks/src/pc_extras.cpp). The keys are ours: the PC's F5 is this host's
		// performance overlay and its Backspace is the Back button here. The key is player one's (the
		// keyboard is player one's device); each also takes a pad chord, unbound by default, which acts
		// for the player whose pad pressed it (GameInput.PadActions.cs).
		new("quick_save", "F9", "Save the game now, where the game allows a save.")
			{ Press = KeyPress.HostAction, HostAction = Fable3PcExtras.QuickSave(Fable3ActingPlayer.One), When = KeyContext.Play,
				PadBindable = true, HostActionFor = Fable3PcExtras.QuickSave },
		new("safety_mode", "G", "Turn the safety on or off: with it on, your attacks cannot hurt the innocent.")
			{ Press = KeyPress.HostAction, HostAction = Fable3PcExtras.Safety(Fable3ActingPlayer.One), When = KeyContext.Play,
				PadBindable = true, HostActionFor = Fable3PcExtras.Safety },
		// The PC's `LiteMenuPause` (P): the host stops stepping the simulation (Fable3LitePause reads it).
		new(Fable3LitePause.Action, "P", "Pause the game where it stands (not online). Press again to go on.")
			{ Press = KeyPress.None, PadBindable = true },
		new("photo_mode", "F11", "Open or close Photo Mode (L3 + R3 on controller).") { Press = KeyPress.PhotoMode },
		new("pause", "Escape", "The Sanctuary (Start). Escape is back in menus.") { Press = KeyPress.Pause },
		// Fable III PC: F1 right, F2 left, F3 down, F4 up (`GUIRightPC` 59, `GUILeftPC` 60, `GUIDownPC` 61,
		// `GUIUpPC` 62); the potion is F1 (manual).
		new("shortcut_up", "F4", "D-pad up: its shortcut.", new[] { "F1" }) { Press = KeyPress.DpadUp, When = KeyContext.Play },
		new("shortcut_left", "F2", "D-pad left: its shortcut.") { Press = KeyPress.DpadLeft, When = KeyContext.Play },
		new("shortcut_down", "F3", "D-pad down: its shortcut.") { Press = KeyPress.DpadDown, When = KeyContext.Play },
		new("shortcut_right", "F1", "D-pad right: its shortcut.", new[] { "F4" }) { Press = KeyPress.DpadRight, When = KeyContext.Play },

		new("press_start", "Enter,Space,Escape", "Press START on the title screen.") { Press = KeyPress.Start, When = KeyContext.TitleScreen },
		new("menu_accept", "1,Enter,E,Space", "Accept in a menu (A).") { Press = KeyPress.A, When = KeyContext.Menu },
		// The PC swaps them on the keyboard (sub_1359C50: GUIX 97 <-> GUIY 98 when the last device
		// was not the pad), so its prompts' keycaps read 3 on X and 2 on Y; these match them.
		new("menu_x", "3", "A menu's X.") { Press = KeyPress.X, When = KeyContext.Menu },
		new("menu_y", "2", "A menu's Y.") { Press = KeyPress.Y, When = KeyContext.Menu },
		new("back", "Escape", "Back out of a menu (B).") { Press = KeyPress.BackOut },
		new("menu_up", "Up", "Menu up (D-pad up).") { Press = KeyPress.DpadUp, When = KeyContext.Menu },
		new("menu_down", "Down", "Menu down (D-pad down).") { Press = KeyPress.DpadDown, When = KeyContext.Menu },
		new("menu_left", "Left", "Menu left (D-pad left).") { Press = KeyPress.DpadLeft, When = KeyContext.Menu },
		new("menu_right", "Right", "Menu right (D-pad right).") { Press = KeyPress.DpadRight, When = KeyContext.Menu },
		new("tab_prev", "Minus,PageUp", "Previous category in a menu (left bumper).") { Press = KeyPress.LeftShoulder, When = KeyContext.Menu },
		new("tab_next", "Equal,PageDown", "Next category in a menu (right bumper).") { Press = KeyPress.RightShoulder, When = KeyContext.Menu },
		new("scroll_prev", "BracketLeft", "Scroll back in a menu (left trigger).") { Press = KeyPress.LeftTrigger, When = KeyContext.Menu },
		new("scroll_next", "BracketRight", "Scroll on in a menu (right trigger).") { Press = KeyPress.RightTrigger, When = KeyContext.Menu },

		// THE MAP TABLE as Fable III PC binds it (GameControlsPC): the mouse moves the cursor
		// (`WorldMapMoveCursorPC`, mouse movement; the 360's cursor is the left stick, which the mouse
		// drives here, and WASD with it), the left button accepts and the right cancels
		// (`WorldMapAcceptPC` / `WorldMapCancelPC`), 3 and 2 are the context actions (`...Action1PC`
		// DirectInput 4, `...Action2PC` 3 — the pad's Y and X, XboxButton 14 and 13), 4 lifts a map
		// item (`WorldMapMoveItemTogglePC` 5 — the pad's left trigger, XboxButton 15), Z and C orbit
		// (`WorldMapOrbitClockwisePC` 44 at ControlDirection X -1, `...AntiClockwisePC` 46 at +1 — the
		// pad's right stick, `WorldMapOrbitCameraXBOX` InputType 5).
		new("map_accept", "MouseLeft", "Accept on the map table (A).") { Press = KeyPress.A, When = KeyContext.Map },
		new("map_cancel", "MouseRight", "Cancel on the map table (B). Escape backs out too.") { Press = KeyPress.B, When = KeyContext.Map },
		new("map_action_1", "3", "The map table's first context action (Y).") { Press = KeyPress.Y, When = KeyContext.Map },
		new("map_action_2", "2", "The map table's second context action (X).") { Press = KeyPress.X, When = KeyContext.Map },
		new("map_move_item", "4", "Lift or set down a map item (left trigger).") { Press = KeyPress.LeftTrigger, When = KeyContext.Map },
		new("map_orbit_clockwise", "Z", "Orbit the map clockwise (right stick left).") { Press = KeyPress.RightStickLeft, When = KeyContext.Map },
		new("map_orbit_anticlockwise", "C", "Orbit the map anticlockwise (right stick right).") { Press = KeyPress.RightStickRight, When = KeyContext.Map },

		new("choice_1", "1,MouseLeft", "The first expression choice (A).") { Press = KeyPress.A, When = KeyContext.Choice },
		new("choice_2", "2", "The second expression choice (X).") { Press = KeyPress.X, When = KeyContext.Choice },
		new("choice_3", "3", "The third expression choice (Y).") { Press = KeyPress.Y, When = KeyContext.Choice },
		new("choice_4", "4", "The fourth expression choice (right bumper).") { Press = KeyPress.RightShoulder, When = KeyContext.Choice },
		new("choice_5", "5", "The fifth expression choice (left bumper).") { Press = KeyPress.LeftShoulder, When = KeyContext.Choice },
	};

	public override KeyAction[] KeyActions => _actions ??= WithShellDefaultsSuperseded(Actions);

	private static KeyAction[]? _actions;

	/// <summary>
	/// The launcher writes every setting it offers, so a title config saved before this table holds
	/// the shell's (Fable 2's) defaults under the shared names — `key_interact = "E,Space,Enter"`. A
	/// stored value equal to the shell's default of the same name reads as this table's default.
	/// </summary>
	private static KeyAction[] WithShellDefaultsSuperseded(KeyAction[] actions)
	{
		var shell = new System.Collections.Generic.Dictionary<string, KeyAction>();
		foreach (KeyAction action in GameInput.DefaultKeyActions)
		{
			shell[action.Name] = action;
		}

		var result = new KeyAction[actions.Length];
		for (int i = 0; i < actions.Length; ++i)
		{
			KeyAction action = actions[i];
			if (!shell.TryGetValue(action.Name, out KeyAction former))
			{
				result[i] = action;
				continue;
			}

			// The shell's default of the day and every one it superseded read as this table's default.
			var superseded = new System.Collections.Generic.List<string>(action.Superseded ?? System.Array.Empty<string>());
			foreach (string old in new[] { former.Defaults }.Concat(former.Superseded ?? System.Array.Empty<string>()))
			{
				if (old != action.Defaults && !superseded.Contains(old))
				{
					superseded.Add(old);
				}
			}

			result[i] = action with { Superseded = superseded.ToArray() };
		}

		return result;
	}

	/// <summary>
	/// The key a prompt shows, in the context that has the input: a held choice's own 1-5, a menu's
	/// 1/2/3 and Esc (as the PC build's own prompts show), and in play the action the game gives
	/// the button — the attack button on the mode's X, Y or B.
	/// </summary>
	public override string? KeyLabel(string button, System.Func<string, string> shown, bool playing)
	{
		if (TitleScreenUp && button == "Start")
		{
			return shown("press_start");
		}

		if (ChoicesUp)
		{
			string? choice = button switch
			{
				"A" => shown("choice_1"),
				"X" => shown("choice_2"),
				"Y" => shown("choice_3"),
				"RB" => shown("choice_4"),
				"LB" => shown("choice_5"),
				_ => null,
			};
			if (choice is not null)
			{
				return choice;
			}
		}

		if (!playing && MapUp)
		{
			string? map = button switch
			{
				"A" => shown("map_accept"),
				"B" => shown("map_cancel"),
				"X" => shown("map_action_2"),
				"Y" => shown("map_action_1"),
				"LT" => shown("map_move_item"),
				"LS" => "Mouse",
				"RS" => shown("map_orbit_clockwise") + "/" + shown("map_orbit_anticlockwise"),
				"Start" => shown("pause"),
				_ => null,
			};
			if (map is not null)
			{
				return map;
			}
		}

		if (!playing)
		{
			return button switch
			{
				"A" => shown("menu_accept"),
				"B" => shown("back"),
				"X" => shown("menu_x"),
				"Y" => shown("menu_y"),
				"LB" => shown("tab_prev"),
				"RB" => shown("tab_next"),
				"LT" => shown("scroll_prev"),
				"RT" => shown("scroll_next"),
				"Start" => shown("pause"),
				"Back" => shown("back"),
				"DPad" => "Arrows",
				"LS" => string.Empty,
				"RS" => "Wheel",
				_ => null,
			};
		}

		int mode = GameInput.AttackModeOf(ModPlayer(0));
		return button switch
		{
			"A" => shown("interact"),
			"X" => mode == 1 ? shown("attack") : shown("melee"),
			"Y" => mode == 2 ? shown("attack") : shown("ranged"),
			"B" => mode == 3 ? shown("attack") : shown("magic"),
			"LB" => shown("first_person"),
			"RB" => shown("quick_menu"),
			"LT" => shown("target_lock"),
			"RT" => shown("look_at"),
			"Start" => shown("pause"),
			"Back" => shown("nickname"),
			"DPad" => "F1-F4",
			"LS" => "WASD",
			"RS" => "Mouse",
			_ => null,
		};
	}
}
