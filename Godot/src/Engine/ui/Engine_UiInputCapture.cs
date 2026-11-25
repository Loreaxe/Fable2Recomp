using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// "PRESS A KEY": the next fresh press on the asked-for device, taken for a rebind row. While a
/// capture is armed, and until the captured input is released, no device reaches the menus or
/// the guest (<see cref="Blocking"/>) — the press that binds must not also step, commit or swing.
/// </summary>
/// <remarks>
/// <para>Fed every input event by <c>GameInput._Input</c> before anything else sees it
/// (<see cref="Offer"/>). Only fresh presses count: an echo, or a key or button already down when
/// the capture armed (the D-pad press that stepped onto the row), never binds. A trigger binds on
/// crossing <see cref="TriggerOn"/> from below <see cref="TriggerOff"/>.</para>
/// <para>Escape cancels; so does <see cref="Timeout"/> without a press. The keyboard capture takes
/// keys and the five mouse buttons by the names refii.toml spells them (Godot key names, MouseLeft
/// ... Mouse5); the pad capture takes the sixteen pad inputs by their <see cref="PadInput"/> names.</para>
/// </remarks>
public static class Engine_UiInputCapture
{
	public enum Kind
	{
		Keyboard,
		Pad,
	}

	public const float TriggerOn = 0.6f;
	public const float TriggerOff = 0.3f;
	public const double Timeout = 8.0;

	/// <summary>The longest a captured input is waited on to come up before the devices return.</summary>
	private const double ReleaseTimeout = 3.0;

	private static Kind? _armed;
	private static Action<string>? _done;
	private static Action? _cancelled;
	private static ulong _armedAt;

	/// <summary>The captured input still down: the devices stay withheld until its release.</summary>
	private static InputEvent? _waiting;
	private static ulong _waitingSince;

	/// <summary>Per joypad and trigger, whether it has been seen at rest since the capture armed.</summary>
	private static readonly HashSet<(int Device, JoyAxis Axis)> _triggerRested = new();

	/// <summary>What the armed capture is waiting for, or null.</summary>
	public static Kind? Armed => _armed;

	/// <summary>Whether the devices are withheld: a capture armed, or its input not yet released.</summary>
	public static bool Blocking
	{
		get
		{
			Tick();
			return _armed is not null || _waiting is not null;
		}
	}

	/// <summary>Arms a capture; a capture already armed is cancelled first.</summary>
	public static void Begin(Kind kind, Action<string> done, Action cancelled)
	{
		Cancel();
		_armed = kind;
		_done = done;
		_cancelled = cancelled;
		_armedAt = Time.GetTicksMsec();
		_triggerRested.Clear();
		if (kind == Kind.Pad)
		{
			foreach (int device in Engine_Joypads.Connected())
			{
				foreach (JoyAxis axis in new[] { JoyAxis.TriggerLeft, JoyAxis.TriggerRight })
				{
					if (Input.GetJoyAxis(device, axis) < TriggerOff)
					{
						_triggerRested.Add((device, axis));
					}
				}
			}
		}
	}

	/// <summary>Disarms the capture, telling its row; nothing when none is armed.</summary>
	public static void Cancel()
	{
		if (_armed is null)
		{
			return;
		}

		Action? cancelled = _cancelled;
		Disarm();
		cancelled?.Invoke();
	}

	private static void Disarm()
	{
		_armed = null;
		_done = null;
		_cancelled = null;
	}

	/// <summary>The timeouts, checked whenever <see cref="Blocking"/> is read.</summary>
	private static void Tick()
	{
		ulong now = Time.GetTicksMsec();
		if (_armed is not null && now - _armedAt > Timeout * 1000.0)
		{
			Cancel();
		}

		if (_waiting is not null && (now - _waitingSince > ReleaseTimeout * 1000.0 || !StillDown(_waiting)))
		{
			_waiting = null;
		}
	}

	/// <summary>
	/// One input event. True when the capture took it — the press it binds, a cancel, or anything
	/// else of the devices' while armed — so nothing else may see it.
	/// </summary>
	public static bool Offer(InputEvent @event)
	{
		if (_armed is not { } kind)
		{
			return _waiting is not null && SameInput(_waiting, @event);
		}

		switch (@event)
		{
			case InputEventKey { Pressed: true, Echo: false } key when KeyOf(key) == Key.Escape:
				Cancel();
				Hold(@event);
				return true;
			case InputEventKey { Pressed: true, Echo: false } key when kind == Kind.Keyboard:
				return Take(OS.GetKeycodeString(KeyOf(key)), @event);
			case InputEventMouseButton { Pressed: true } mouse when kind == Kind.Keyboard:
				return MouseName(mouse.ButtonIndex) is { } name ? Take(name, @event) : true;
			case InputEventJoypadButton { Pressed: true } button when kind == Kind.Pad:
				return PadName(button.ButtonIndex) is { } padName ? Take(padName, @event) : true;
			case InputEventJoypadMotion motion when kind == Kind.Pad &&
				motion.Axis is JoyAxis.TriggerLeft or JoyAxis.TriggerRight:
				if (motion.AxisValue < TriggerOff)
				{
					_triggerRested.Add((motion.Device, motion.Axis));
				}
				else if (motion.AxisValue > TriggerOn && _triggerRested.Contains((motion.Device, motion.Axis)))
				{
					return Take(motion.Axis == JoyAxis.TriggerLeft ? "LT" : "RT", @event);
				}

				return true;
			case InputEventKey or InputEventMouseButton or InputEventJoypadButton:
				return true;
		}

		return false;
	}

	private static bool Take(string name, InputEvent @event)
	{
		Action<string>? done = _done;
		Disarm();
		Hold(@event);
		done?.Invoke(name);
		return true;
	}

	private static void Hold(InputEvent @event)
	{
		_waiting = @event;
		_waitingSince = Time.GetTicksMsec();
	}

	/// <summary>Whether two events are the same key, mouse button, joypad button or trigger.</summary>
	private static bool SameInput(InputEvent held, InputEvent @event) => (held, @event) switch
	{
		(InputEventKey a, InputEventKey b) => KeyOf(a) == KeyOf(b),
		(InputEventMouseButton a, InputEventMouseButton b) => a.ButtonIndex == b.ButtonIndex,
		(InputEventJoypadButton a, InputEventJoypadButton b) => a.Device == b.Device && a.ButtonIndex == b.ButtonIndex,
		(InputEventJoypadMotion a, InputEventJoypadMotion b) => a.Device == b.Device && a.Axis == b.Axis,
		_ => false,
	};

	private static bool StillDown(InputEvent @event) => @event switch
	{
		InputEventKey key => Input.IsKeyPressed(KeyOf(key)) || Input.IsPhysicalKeyPressed(KeyOf(key)),
		InputEventMouseButton mouse => Input.IsMouseButtonPressed(mouse.ButtonIndex),
		InputEventJoypadButton button => Input.IsJoyButtonPressed(button.Device, button.ButtonIndex),
		InputEventJoypadMotion motion => Input.GetJoyAxis(motion.Device, motion.Axis) > TriggerOff,
		_ => false,
	};

	private static Key KeyOf(InputEventKey key) => key.Keycode != Key.None ? key.Keycode : key.PhysicalKeycode;

	/// <summary>A mouse button's name as refii.toml's <c>key_*</c> spells it, or null for the wheel.</summary>
	public static string? MouseName(MouseButton button) => button switch
	{
		MouseButton.Left => "MouseLeft",
		MouseButton.Right => "MouseRight",
		MouseButton.Middle => "MouseMiddle",
		MouseButton.Xbutton1 => "Mouse4",
		MouseButton.Xbutton2 => "Mouse5",
		_ => null,
	};

	/// <summary>A joypad button's <see cref="PadInput"/> name, or null for one the game has no use for (Guide, paddles).</summary>
	public static string? PadName(JoyButton button) => button switch
	{
		JoyButton.A => nameof(PadInput.A),
		JoyButton.B => nameof(PadInput.B),
		JoyButton.X => nameof(PadInput.X),
		JoyButton.Y => nameof(PadInput.Y),
		JoyButton.LeftShoulder => nameof(PadInput.LB),
		JoyButton.RightShoulder => nameof(PadInput.RB),
		JoyButton.LeftStick => nameof(PadInput.L3),
		JoyButton.RightStick => nameof(PadInput.R3),
		JoyButton.Start => nameof(PadInput.Start),
		JoyButton.Back => nameof(PadInput.Back),
		JoyButton.DpadUp => nameof(PadInput.DpadUp),
		JoyButton.DpadDown => nameof(PadInput.DpadDown),
		JoyButton.DpadLeft => nameof(PadInput.DpadLeft),
		JoyButton.DpadRight => nameof(PadInput.DpadRight),
		_ => null,
	};
}
