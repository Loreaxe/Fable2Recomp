using Refii.Ui;
using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The phone's part of <see cref="GameInput"/>: the physical-button capture that keeps a
/// controller's event edges through slow frames, the phase publication the single-player mailbox
/// keeps until the guest has read it, pad 0's rumble on the phone's own vibrator, and the debug
/// channel's controller snapshot. The shared sampler calls in through one-line hooks; on the
/// desktop each hook does what the sampler did before (polls, nothing captured, nothing pulsed).
/// </summary>
public partial class GameInput
{
	private readonly GameJoypadButtonCapture _buttonCapture = new();
	private GameInputButtonRoute _captureRoute;
	private long _deliveryEpoch, _deliveryCaptureGeneration = -1;
	private uint _deliveryMask = uint.MaxValue;
	private int _deliveryPad0 = int.MinValue;

	/// <summary>Android retains event edges through slow frame polling; desktop keeps its original sampler.</summary>
	private bool CapturePhysicalButtons => DevicePolicyOverride?.CapturePhysicalButtons ?? Engine_Platform.IsAndroid;

	/// <summary>Captures mapped physical button events for Android without dispatching actions; called from <see cref="_Input"/>.</summary>
	/// <param name="inputEvent">The real Godot joypad event; axes, keyboard and other event types are unchanged.</param>
	private void CapturePhysicalButton(InputEvent inputEvent)
	{
		if (!CapturePhysicalButtons || inputEvent is not InputEventJoypadButton button) return;
		_buttonCapture.RefreshDevices(ConnectedJoypadIds());
		_buttonCapture.SetContext(HasInputFocus(), _captureRoute);
		_buttonCapture.Capture(button.Device, JoyButtonBit(button.ButtonIndex), button.Pressed);
	}

	/// <summary>The capture's devices and route for this frame, before the pads are sampled; called from <see cref="_Process"/>.</summary>
	/// <param name="devices">Whether real devices have application/window focus.</param>
	private void RefreshButtonCapture(bool devices)
	{
		if (!CapturePhysicalButtons) return;
		_buttonCapture.RefreshDevices(ConnectedJoypadIds());
		_captureRoute = ReadButtonRoute();
		_buttonCapture.SetContext(devices, _captureRoute);
	}

	/// <summary>Retires pending physical identities when this input owner leaves the scene.</summary>
	private void RetireButtonCapture()
	{
		_buttonCapture.RefreshDevices(System.Array.Empty<int>());
		if (CapturePhysicalButtons)
			ControllerStateSink?.Invoke(new GameControllerPacket(0, false, 0, 0, 0, 0, 0, 0, 0));
	}

	/// <summary>The latest raw player-one sample before production menu ownership masks.</summary>
	public GameControllerPacket? LastRawPacket { get; private set; }

	/// <summary>The latest player-one sample after masks and held-across-unmask protection.</summary>
	public GameControllerPacket? LastFilteredPacket { get; private set; }

	/// <summary>A bounded main-thread snapshot of device identities, raw axes, assignments and masks.</summary>
	/// <returns>Observed physical state and the actual production packet; no input is changed.</returns>
	public object DescribeControllerInput()
	{
		var devices = new System.Collections.Generic.List<object>();
		foreach (int id in Engine_Joypads.Connected())
		{
			if (devices.Count >= 8) break;
			devices.Add(new
			{
				id, name = Engine_Joypads.Name(id), guid = Engine_Joypads.Guid(id), known = Engine_Joypads.Known(id),
				selected_guest_pad = System.Array.IndexOf(_padDevice, id), buttons = JoypadButtons(id),
				left_x = Input.GetJoyAxis(id, JoyAxis.LeftX), left_y = Input.GetJoyAxis(id, JoyAxis.LeftY),
				right_x = Input.GetJoyAxis(id, JoyAxis.RightX), right_y = Input.GetJoyAxis(id, JoyAxis.RightY),
				left_trigger = Input.GetJoyAxis(id, JoyAxis.TriggerLeft),
				right_trigger = Input.GetJoyAxis(id, JoyAxis.TriggerRight)
			});
		}
		return new
		{
			devices, selected_pad0_device = _padDevice[0], focused = HasInputFocus(),
			raw_packet = LastRawPacket, filtered_packet = LastFilteredPacket,
			title_input = Engine_TitleGame.Active.Input.Describe(),
			physical_button_capture = CapturePhysicalButtons, captured_button_overflows = _buttonCapture.Overflows,
			touch_mode = Engine_UiInputMode.Touch, touch_hold = Touch,
			pointer_free = Engine_UiPointer.Free, pointer_canvas = Engine_UiPointer.Canvas?.ToString(),
			mouse_mode = Input.MouseMode.ToString(), playing = Playing, in_overlay = InOverlay,
			transport = ControllerStateSink == null ? "direct desktop bridge" : "single-player guest-owner mailbox"
		};
	}

	/// <summary>
	/// Pad 0's packet with the phase publication the mailbox retains until the guest has read it,
	/// when the physical buttons are captured and the owner's mailbox is the transport; otherwise
	/// the packet as it was.
	/// </summary>
	private GameControllerPacket WithPhysicalPublication(GameControllerPacket packet, GameTitleInput.Published published)
	{
		if (CapturePhysicalButtons && ControllerStateSink != null)
		{
			if (_deliveryCaptureGeneration != _buttonCapture.ContextGeneration ||
				_deliveryMask != published.MaskBits || _deliveryPad0 != _padDevice[0])
			{
				++_deliveryEpoch;
				_deliveryCaptureGeneration = _buttonCapture.ContextGeneration;
				_deliveryMask = published.MaskBits;
				_deliveryPad0 = _padDevice[0];
			}

			bool focus = HasInputFocus();
			// An immutable record: an unchanged sample is the last one again, not a new copy.
			GameControllerPhysicalPublication? physical = LastFilteredPacket?.Physical;
			if (physical is null || physical.Epoch != _deliveryEpoch || physical.Enabled != focus ||
				physical.Buttons != published.PhysicalButtons || physical.OtherButtons != published.OtherButtons)
			{
				physical = new GameControllerPhysicalPublication(_deliveryEpoch, focus, published.PhysicalButtons, published.OtherButtons);
			}

			packet = packet with { Physical = physical };
		}

		return packet;
	}

	/// <summary>Keeps pad 0's raw and filtered samples for <see cref="DescribeControllerInput"/>.</summary>
	private void RecordPad0Packets(int pad, bool connected, PadState state, GameControllerPacket packet)
	{
		if (LastRawPacket is not { } raw || raw.Pad != pad || raw.Connected != connected ||
			raw.Buttons != state.Buttons || raw.LeftTrigger != state.LeftTrigger ||
			raw.RightTrigger != state.RightTrigger || raw.LeftX != state.LeftX ||
			raw.LeftY != state.LeftY || raw.RightX != state.RightX || raw.RightY != state.RightY ||
			raw.Physical is not null)
		{
			LastRawPacket = new GameControllerPacket(pad, connected, state.Buttons,
				state.LeftTrigger, state.RightTrigger, state.LeftX, state.LeftY, state.RightX, state.RightY);
		}

		LastFilteredPacket = packet;
		// What the guest is handed, the mailbox's split where it carries one (mods' claims and presses included).
		GuestPad0Buttons = packet.Physical is { } physical ? physical.Buttons | physical.OtherButtons : packet.Buttons;
	}

	/// <summary>The buttons pad 0 last handed the guest, after every mask, claim and press.</summary>
	public static uint GuestPad0Buttons { get; private set; }

	private readonly System.Collections.Generic.List<int> _connectedJoypadIds = new();

	/// <summary>
	/// The connected identities for the capture lifetime: the same cached enumeration pad
	/// assignment uses (refreshed on Godot's connection signal), unless a native fixture supplied
	/// metadata.
	/// </summary>
	/// <remarks>
	/// Android refreshes the capture every frame. Asking Godot each time made a Godot array (a
	/// finalizable wrapper) per frame, and the capture a set and a list besides.
	/// </remarks>
	private System.Collections.Generic.List<int> ConnectedJoypadIds()
	{
		_connectedJoypadIds.Clear();
		foreach ((int id, _) in ConnectedJoypadStates())
		{
			_connectedJoypadIds.Add(id);
		}

		return _connectedJoypadIds;
	}

	/// <summary>Augments only Android physical buttons before the existing aggregation and ownership masks.</summary>
	/// <param name="device">The assigned or unmapped device sampled once this frame.</param>
	/// <returns>The current physical hold plus a bounded unsampled event edge.</returns>
	private uint PhysicalButtons(int device) => CapturePhysicalButtons
		? _buttonCapture.Sample(device, JoypadButtons(device)) : JoypadButtons(device);

	/// <summary>
	/// Screen rectangles the phone's own UI holds (the Options panel and the menus it opens), in canvas
	/// units; null on the desktop. A pointer event there is that UI's alone.
	/// </summary>
	/// <remarks>Adds them to the list it is given (reused, so a pointer event allocates nothing), and adds none while that UI is closed.</remarks>
	public static System.Action<System.Collections.Generic.List<Rect2>>? HostUiRects { get; set; }

	/// <summary>The rectangles <see cref="HostUiRects"/> gave for the event being routed, refilled for each.</summary>
	private readonly System.Collections.Generic.List<Rect2> _hostUiRects = new();

	/// <summary>
	/// Whether a mouse event lands on the phone's own UI, so the game's menu pointer leaves it: a tap on an
	/// Options button over a game menu would also click the entry under it. Always false on the desktop.
	/// </summary>
	private bool OnHostUi(InputEvent inputEvent)
	{
		if (HostUiRects is not { } rects || inputEvent is not InputEventMouse mouse) return false;
		_hostUiRects.Clear();
		rects(_hostUiRects);
		if (_hostUiRects.Count == 0) return false;
		Vector2 canvas = GetViewport().GetFinalTransform().AffineInverse() * mouse.Position;
		foreach (Rect2 rect in _hostUiRects)
			if (rect.HasPoint(canvas)) return true;
		return false;
	}

	/// <summary>How strong the rumble is, joypad and phone alike: the phone's <c>vibration_strength</c> as a fraction; full on the desktop.</summary>
	public static float VibrationStrength { get; private set; } = 1f;

	/// <summary>Reads <see cref="VibrationStrength"/> with the other control settings.</summary>
	private static void ReadVibrationStrength(System.Collections.Generic.IReadOnlyDictionary<string, string> values) =>
		VibrationStrength = Engine_Platform.IsAndroid
			? Mathf.Clamp(Engine_RefiiConfig.Int(values, Refii.AndroidRuntime.AndroidQuickSettings.VibrationStrengthKey,
				Refii.AndroidRuntime.AndroidQuickSettings.VibrationStrengthDefault), 1, 100) / 100f
			: 1f;

	/// <summary>One device's buttons and triggers through the phone's own controller map, after the core remap (GameInput.Controllers.cs); unchanged on the desktop.</summary>
	private static void RemapPadAndroid(ref uint buttons, ref uint leftTrigger, ref uint rightTrigger)
	{
		if (Engine_Platform.IsAndroid)
			Refii.AndroidRuntime.AndroidPadRemap.Apply(ref buttons, ref leftTrigger, ref rightTrigger);
	}

	/// <summary>A captured press not yet sampled, so a tap already released before this frame still claims a pad; zero on the desktop.</summary>
	private uint PendingPhysicalButtons(int device) =>
		CapturePhysicalButtons ? _buttonCapture.PendingButtons(device) : 0;

	/// <summary>Keys pending taps to actual native screens/modals and host ownership, without bypassing input gates.</summary>
	/// <returns>A changes-only identity; highlights and animation progress deliberately do not invalidate a held input.</returns>
	private GameInputButtonRoute ReadButtonRoute()
	{
		return DevicePolicyOverride is { } policy ? policy.Route : Engine_TitleGame.Active.Input.ButtonRoute();
	}

	/// <summary>Uses the existing Xbox button vocabulary for event capture; unmapped extras remain ignored.</summary>
	/// <param name="button">The actual mapped Godot button index.</param>
	/// <returns>One existing XInput bit or zero for unsupported buttons.</returns>
	internal static uint JoyButtonBit(JoyButton button) => button switch
	{
		JoyButton.DpadUp => DpadUp, JoyButton.DpadDown => DpadDown,
		JoyButton.DpadLeft => DpadLeft, JoyButton.DpadRight => DpadRight,
		JoyButton.Start => Start, JoyButton.Back => Back,
		JoyButton.LeftStick => LeftThumb, JoyButton.RightStick => RightThumb,
		JoyButton.LeftShoulder => LeftShoulder, JoyButton.RightShoulder => RightShoulder,
		JoyButton.A => ButtonA, JoyButton.B => ButtonB, JoyButton.X => ButtonX, JoyButton.Y => ButtonY,
		_ => 0,
	};

	/// <summary>Pad 0's rumble as last applied to the phone's own vibrator.</summary>
	private static readonly Engine_HandheldRumble _handheldRumble = new();

	/// <summary>
	/// Pad 0's rumble on the phone itself, while the touchscreen is the device on Android and no
	/// controller holds pad 0 — a touch player has no joypad for <see cref="DriveRumble"/> to tell.
	/// The same gates as the joypad's (focus, the guest's switch, <see cref="ControllerVibration"/>);
	/// pulses and stops as <see cref="Engine_HandheldRumble"/> decides, so the Java bridge hears only
	/// a change or a renewal. A controller taking the pad, or a key press, stops the phone at once.
	/// </summary>
	private static void DriveHandheldRumble(bool devices)
	{
		uint left = 0;
		uint right = 0;
		bool stated = devices && ControllerVibration && Engine_Platform.IsAndroid && Engine_UiInputMode.Touch &&
			_padDevice[0] < 0 && HostBridge.TryGetVibration(0, out left, out right);
		ApplyHandheldRumble(stated ? _handheldRumble.Observe(left, right, Time.GetTicksMsec()) : _handheldRumble.Silence());
	}

	/// <summary>Tells the phone's vibrator a verdict; <c>vibrate_handheld</c> has no stop, so a stop is a pulse too short to feel.</summary>
	private static void ApplyHandheldRumble(Engine_HandheldRumble.Verdict verdict)
	{
		switch (verdict)
		{
			case Engine_HandheldRumble.Verdict.Pulse:
				Input.VibrateHandheld(Engine_HandheldRumble.PulseMs, _handheldRumble.Amplitude * VibrationStrength);
				break;
			case Engine_HandheldRumble.Verdict.Stop:
				Input.VibrateHandheld(Engine_HandheldRumble.StopMs, Engine_HandheldRumble.StopAmplitude);
				break;
		}
	}

	/// <summary>Stops the phone's vibrator with the joypads' effects (<see cref="SilenceRumble"/>).</summary>
	private static void SilenceHandheldRumble() => ApplyHandheldRumble(_handheldRumble.Silence());

	/// <summary>Pad 0's rumble on the phone for the debug channel: whether a pulse runs, and its strength.</summary>
	public static (bool Running, float Amplitude) HandheldRumbleReadout() =>
		(_handheldRumble.Running, _handheldRumble.Amplitude);
}
