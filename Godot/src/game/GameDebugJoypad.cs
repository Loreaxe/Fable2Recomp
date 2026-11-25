using Godot;

namespace Refii.Game;

/// <summary>
/// The debug channel's JOYPAD press (`press` with `joypad`): the buttons and axes raised as
/// <see cref="InputEventJoypadButton"/> / <see cref="InputEventJoypadMotion"/> through
/// <see cref="Input.ParseInputEvent"/> on one joypad, held for a number of frames and released the
/// same way. Everything a player's controller reaches sees it: the device switch
/// (GameInput.DetectDevice, prompts to pad glyphs), the joypad's family for the glyph art, the pad
/// assignment and the device path to the guest, through that player's remap.
/// </summary>
/// <remarks>
/// The joypad passes the focus gate while it is held and for <see cref="GraceMs"/> after its release
/// (<see cref="Scripted"/>), so an unfocused run still delivers the press and its release; every other
/// device stays muted. The debug channel's dummy controller (<see cref="Refii.Core.Engine_DummyJoypad"/>)
/// is the channel's for as long as it is connected, and its hold runs beside a real joypad's: both
/// players can be held at once.
/// </remarks>
public static class GameDebugJoypad
{
	/// <summary>How long after its release the scripted joypad still passes the focus gate.</summary>
	public const ulong GraceMs = 500;

	/// <summary>The joypad buttons for an XInput mask (<see cref="GameInput.JoyButtonBit"/>), in
	/// <see cref="JoyButton"/> order.</summary>
	public static JoyButton[] Buttons(uint mask)
	{
		var buttons = new System.Collections.Generic.List<JoyButton>();
		for (JoyButton button = JoyButton.A; button <= JoyButton.DpadRight; ++button)
		{
			if ((GameInput.JoyButtonBit(button) & mask) != 0u)
			{
				buttons.Add(button);
			}
		}

		return buttons.ToArray();
	}

	/// <summary>
	/// The axis values for the press's sticks (−1..1, Y up positive as XInput states it) and triggers
	/// (0..1), in Godot's frame: stick Y is negated (Godot's is down positive). Only the axes named.
	/// </summary>
	public static (JoyAxis Axis, float Value)[] Axes(float? lx, float? ly, float? rx, float? ry, float? lt, float? rt)
	{
		var axes = new System.Collections.Generic.List<(JoyAxis, float)>();
		static float Stick(float value) => System.Math.Clamp(value, -1f, 1f);
		static float Trigger(float value) => System.Math.Clamp(value, 0f, 1f);
		if (lx is { } a) axes.Add((JoyAxis.LeftX, Stick(a)));
		if (ly is { } b) axes.Add((JoyAxis.LeftY, -Stick(b)));
		if (rx is { } c) axes.Add((JoyAxis.RightX, Stick(c)));
		if (ry is { } d) axes.Add((JoyAxis.RightY, -Stick(d)));
		if (lt is { } e) axes.Add((JoyAxis.TriggerLeft, Trigger(e)));
		if (rt is { } f) axes.Add((JoyAxis.TriggerRight, Trigger(f)));
		return axes.ToArray();
	}

	/// <summary>One joypad's running hold, and when it last ended.</summary>
	private sealed class Hold
	{
		public int Device = -1;
		public JoyButton[] Held = System.Array.Empty<JoyButton>();
		public (JoyAxis Axis, float Value)[] Pushed = System.Array.Empty<(JoyAxis, float)>();
		public int Frames;
		public ulong ReleasedAt;
	}

	private static readonly Hold _real = new();
	private static readonly Hold _dummy = new();

	/// <summary>Whether <paramref name="joypad"/> is the debug channel's: the connected dummy, or a
	/// joypad held now or released less than <see cref="GraceMs"/> ago.</summary>
	public static bool Scripted(int joypad) =>
		joypad >= 0 && (Refii.Core.Engine_DummyJoypad.Is(joypad) ||
		(joypad == _real.Device && (_real.Frames > 0 || Time.GetTicksMsec() - _real.ReleasedAt < GraceMs)));

	/// <summary>Raises the press on <paramref name="device"/>; a hold still running on it, or on
	/// another real joypad, is released first.</summary>
	public static void Press(int device, JoyButton[] buttons, (JoyAxis Axis, float Value)[] axes, int frames)
	{
		Hold hold = Refii.Core.Engine_DummyJoypad.Is(device) ? _dummy : _real;
		Release(hold);
		hold.Device = device;
		hold.Held = buttons;
		hold.Pushed = axes;
		hold.Frames = System.Math.Max(1, frames);
		foreach (JoyButton button in buttons)
		{
			Input.ParseInputEvent(new InputEventJoypadButton
			{
				Device = device, ButtonIndex = button, Pressed = true,
			});
		}

		foreach ((JoyAxis axis, float value) in axes)
		{
			Input.ParseInputEvent(new InputEventJoypadMotion { Device = device, Axis = axis, AxisValue = value });
		}
	}

	/// <summary>Once a frame: ends each hold once its frames are spent, through the same pipeline.</summary>
	public static void Tick()
	{
		Tick(_real);
		Tick(_dummy);
	}

	/// <summary>Releases what the dummy holds now: it is about to be disconnected.</summary>
	public static void ReleaseDummy() => Release(_dummy);

	private static void Release(Hold hold)
	{
		if (hold.Frames > 0)
		{
			hold.Frames = 1;
			Tick(hold);
		}
	}

	private static void Tick(Hold hold)
	{
		if (hold.Frames <= 0 || --hold.Frames > 0)
		{
			return;
		}

		foreach (JoyButton button in hold.Held)
		{
			Input.ParseInputEvent(new InputEventJoypadButton { Device = hold.Device, ButtonIndex = button, Pressed = false });
		}

		foreach ((JoyAxis axis, _) in hold.Pushed)
		{
			Input.ParseInputEvent(new InputEventJoypadMotion { Device = hold.Device, Axis = axis, AxisValue = 0f });
		}

		hold.Held = System.Array.Empty<JoyButton>();
		hold.Pushed = System.Array.Empty<(JoyAxis, float)>();
		hold.ReleasedAt = Time.GetTicksMsec();
	}
}
