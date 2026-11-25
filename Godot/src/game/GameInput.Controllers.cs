using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// The controllers themselves: which family each connected pad is (for the prompts' glyphs), the
/// player's controller remap where a joypad enters a pad, the glyph settings, and the prompt
/// device — the player with the input.
/// </summary>
/// <remarks>
/// <para>THE REMAP TARGETS THE GAME'S BUTTONS (<see cref="Engine_PadRemap"/>): applied per joypad
/// in <see cref="PadState.Merge"/> through <see cref="RemapPad"/>, ahead of the mods' withhold and
/// presses and of the title's masks, on both pad paths. Pad 0 and every unmapped joypad merged
/// into it are player one's; a pad the title says is player two's (<c>ModPlayer</c>) takes player
/// two's remap. The keyboard, the touch controls and the scripted sources state the game's buttons
/// already and never pass through it.</para>
/// <para>MOD PAD BINDINGS NAME THE GAME'S BUTTONS TOO: a mod action on "A" fires on whatever the
/// player bound to the game's A (<see cref="GameButtonHeld"/>), and its claim withholds the game's
/// A — so one remap moves the game and every mod together.</para>
/// </remarks>
public partial class GameInput
{
	/// <summary>Each connected joypad's family, worked out once per connection.</summary>
	private static readonly Dictionary<int, ControllerFamily> _families = new();

	/// <summary>The joypad last pressed or pushed, −1 before any.</summary>
	private static int _lastJoypad = -1;

	/// <summary>The family of a connected joypad, from its name, GUID and USB ids.</summary>
	public static ControllerFamily FamilyOfJoypad(int joypad)
	{
		if (joypad < 0)
		{
			return ControllerFamily.Unknown;
		}

		if (_families.TryGetValue(joypad, out ControllerFamily known))
		{
			return known;
		}

		(int vendor, int product) = Engine_Joypads.Ids(joypad);
		string name = Engine_Joypads.Name(joypad);
		ControllerFamily family = Engine_ControllerFamily.Classify(name, Engine_Joypads.Guid(joypad), vendor, product);
		_families[joypad] = family;
		GD.Print($"[input] joypad {joypad} '{name}' is a {family} controller");
		return family;
	}

	/// <summary>The family of the joypad holding guest pad <paramref name="pad"/>, Unknown while it is free.</summary>
	public static ControllerFamily FamilyOfPad(int pad) => FamilyOfJoypad(JoypadForPad(pad));

	/// <summary>Drops the families: a connection changed, and a joypad id may now be another device.</summary>
	private static void ForgetControllerFamilies() => _families.Clear();

	/// <summary>The player a joypad's buttons are for: player two's on the pad the title gives player two, else player one's.</summary>
	private static int PlayerOfJoypad(int joypad)
	{
		int pad = System.Array.IndexOf(_padDevice, joypad);
		return pad > 0 && Engine_TitleGame.Active.Input.ModPlayer(pad) == 2 ? 2 : 1;
	}

	/// <summary>The guest pad <paramref name="player"/> plays on: pad 0 for player one, the title's for player two, −1 for none.</summary>
	public static int PadOfPlayer(int player)
	{
		if (player != 2)
		{
			return 0;
		}

		for (int pad = 1; pad < MaxControllers; ++pad)
		{
			if (Engine_TitleGame.Active.Input.ModPlayer(pad) == 2)
			{
				return pad;
			}
		}

		return -1;
	}

	/// <summary>
	/// One joypad's buttons and triggers through its player's remap (and, on Android, the phone's
	/// own controller map after it). Nothing passes while a rebind row is capturing a press.
	/// </summary>
	private static void RemapPad(int joypad, ref uint buttons, ref uint leftTrigger, ref uint rightTrigger)
	{
		if (Engine_UiInputCapture.Blocking)
		{
			buttons = 0;
			leftTrigger = rightTrigger = 0;
			return;
		}

		Engine_PadRemap.For(PlayerOfJoypad(joypad)).Apply(ref buttons, ref leftTrigger, ref rightTrigger);
		RemapPadAndroid(ref buttons, ref leftTrigger, ref rightTrigger);
	}

	/// <summary>
	/// Every input event, first: notes the joypad in use, and hands the event to a capturing rebind
	/// row. True when the capture took it.
	/// </summary>
	private static bool ObserveControllerEvent(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventJoypadButton { Pressed: true } button when Engine_Joypads.Known(button.Device):
				_lastJoypad = button.Device;
				break;
			case InputEventJoypadMotion motion when Engine_Joypads.Known(motion.Device) && Mathf.Abs(motion.AxisValue) > 0.5f:
				_lastJoypad = motion.Device;
				break;
		}

		return Engine_UiInputCapture.Offer(@event);
	}

	/// <summary><c>padmap_*</c>, <c>padmap2_*</c>, <c>glyph_style</c> and <c>glyph_pack</c>, from the control settings.</summary>
	private static void ReadControllerSettings(IReadOnlyDictionary<string, string> values)
	{
		Engine_PadRemap.ReadAll(values);
		Engine_UiKeyGlyphs.StyleSetting = Engine_RefiiConfig.String(values, "glyph_style", "auto");
		string pack = Engine_RefiiConfig.String(values, "glyph_pack", string.Empty).Trim();
		if (pack != Engine_UiKeyGlyphs.PackName)
		{
			Engine_UiKeyGlyphs.PackName = pack;
		}
		else
		{
			// Re-read on a settings change, so files dropped into the folder show without a restart.
			Engine_UiGlyphPack.Forget();
		}
	}

	/// <summary>
	/// The prompts' device, once per frame: the player whose menu is open on split screen, else the
	/// player whose joypad was used last; that player's controller family and remap.
	/// </summary>
	private static void UpdateGlyphDevice()
	{
		int player = 1;
		int owner = Engine_TitleGame.Active.MenuPlayer;
		if (Engine_Views.Split && owner is 1 or 2)
		{
			player = owner;
		}
		else if (_lastJoypad >= 0)
		{
			player = PlayerOfJoypad(_lastJoypad);
		}

		int joypad = PadOfPlayer(player) is >= 0 and var pad ? JoypadForPad(pad) : -1;
		if (joypad < 0 && _lastJoypad >= 0 && PlayerOfJoypad(_lastJoypad) == player)
		{
			joypad = _lastJoypad;
		}

		Engine_UiKeyGlyphs.GlyphPlayer = player;
		Engine_UiKeyGlyphs.PadFamily = FamilyOfJoypad(joypad);
	}

	/// <summary>The controller family the player with the input is holding, as the prompts draw it.</summary>
	public static ControllerFamily PromptFamily => Engine_UiKeyGlyphs.PadFamily;

	/// <summary>One joypad's game buttons this frame: its raw buttons and triggers through its player's remap.</summary>
	private static readonly Dictionary<int, (ulong Frame, uint Buttons, uint Left, uint Right)> _gameButtons = new();

	private static (uint Buttons, uint Left, uint Right) GameButtonsOf(int joypad)
	{
		ulong frame = Engine.GetProcessFrames();
		if (_gameButtons.TryGetValue(joypad, out var cached) && cached.Frame == frame)
		{
			return (cached.Buttons, cached.Left, cached.Right);
		}

		uint buttons = JoypadButtons(joypad);
		uint left = Trigger(joypad, JoyAxis.TriggerLeft), right = Trigger(joypad, JoyAxis.TriggerRight);
		Engine_PadRemap.For(PlayerOfJoypad(joypad)).Apply(ref buttons, ref left, ref right);
		_gameButtons[joypad] = (frame, buttons, left, right);
		return (buttons, left, right);
	}

	/// <summary>Whether the game's <paramref name="button"/> is down on <paramref name="joypad"/> through the remap.</summary>
	public static bool GameButtonHeld(int joypad, JoyButton button)
	{
		uint bit = JoyButtonBit(button);
		return joypad >= 0 && bit != 0 && (GameButtonsOf(joypad).Buttons & bit) != 0;
	}

	/// <summary>The game's trigger on <paramref name="joypad"/> through the remap, 0..1.</summary>
	public static float GameTrigger(int joypad, JoyAxis trigger)
	{
		if (joypad < 0)
		{
			return 0f;
		}

		(_, uint left, uint right) = GameButtonsOf(joypad);
		return (trigger == JoyAxis.TriggerLeft ? left : trigger == JoyAxis.TriggerRight ? right : 0u) / 255f;
	}

	/// <summary>The Godot input at a <see cref="PadInput"/>'s position: a button, or a trigger axis.</summary>
	public static (JoyButton Button, JoyAxis Trigger) JoyInputOf(PadInput input) => input switch
	{
		PadInput.A => (JoyButton.A, JoyAxis.Invalid),
		PadInput.B => (JoyButton.B, JoyAxis.Invalid),
		PadInput.X => (JoyButton.X, JoyAxis.Invalid),
		PadInput.Y => (JoyButton.Y, JoyAxis.Invalid),
		PadInput.LB => (JoyButton.LeftShoulder, JoyAxis.Invalid),
		PadInput.RB => (JoyButton.RightShoulder, JoyAxis.Invalid),
		PadInput.LT => (JoyButton.Invalid, JoyAxis.TriggerLeft),
		PadInput.RT => (JoyButton.Invalid, JoyAxis.TriggerRight),
		PadInput.L3 => (JoyButton.LeftStick, JoyAxis.Invalid),
		PadInput.R3 => (JoyButton.RightStick, JoyAxis.Invalid),
		PadInput.Start => (JoyButton.Start, JoyAxis.Invalid),
		PadInput.Back => (JoyButton.Back, JoyAxis.Invalid),
		PadInput.DpadUp => (JoyButton.DpadUp, JoyAxis.Invalid),
		PadInput.DpadDown => (JoyButton.DpadDown, JoyAxis.Invalid),
		PadInput.DpadLeft => (JoyButton.DpadLeft, JoyAxis.Invalid),
		_ => (JoyButton.DpadRight, JoyAxis.Invalid),
	};

	/// <summary>The InputMap events of the physical inputs player one's remap binds to the game's <paramref name="game"/>.</summary>
	public static List<InputEvent> PhysicalPadEvents(PadInput game, int device)
	{
		var events = new List<InputEvent>();
		foreach (PadInput source in Engine_PadRemap.For(1).Sources(game))
		{
			(JoyButton button, JoyAxis trigger) = JoyInputOf(source);
			events.Add(trigger != JoyAxis.Invalid
				? new InputEventJoypadMotion { Axis = trigger, AxisValue = 1f, Device = device }
				: new InputEventJoypadButton { ButtonIndex = button, Device = device });
		}

		return events;
	}

	/// <summary>The <see cref="PadInput"/> a Godot button or trigger is, or null for one the game has no use for.</summary>
	public static PadInput? PadInputOf(JoyButton button, JoyAxis trigger = JoyAxis.Invalid)
	{
		if (trigger == JoyAxis.TriggerLeft)
		{
			return PadInput.LT;
		}

		if (trigger == JoyAxis.TriggerRight)
		{
			return PadInput.RT;
		}

		return Engine_UiInputCapture.PadName(button) is { } name && Engine_PadRemap.TryParse(name, out PadInput input)
			? input
			: null;
	}
}
