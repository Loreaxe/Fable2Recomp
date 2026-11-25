using Godot;

namespace Refii.Core;

/// <summary>
/// THE JOYPAD ROSTER, as the game reads it: which joypads are connected, whether each has a gamepad
/// mapping, what it says it is, and its motors. Godot's joypads, plus the debug channel's dummy
/// (<see cref="Engine_DummyJoypad"/>) while that is connected.
/// </summary>
/// <remarks>
/// Buttons and axes are not here: every joypad's, the dummy's included, are Godot's polled state
/// (<c>Input.IsJoyButtonPressed</c>, <c>Input.GetJoyAxis</c>), which <c>Input.ParseInputEvent</c>
/// writes for any device id. What Godot keeps per CONNECTED device only — the enumeration, the
/// mapping, the identity, the vibration — is what this answers, and with no dummy connected every
/// answer is Godot's own.
/// </remarks>
public static class Engine_Joypads
{
	/// <summary>The connected joypads' ids.</summary>
	public static int[] Connected()
	{
		Godot.Collections.Array<int> godot = Input.GetConnectedJoypads();
		try
		{
			var real = new int[godot.Count];
			for (int i = 0; i < real.Length; ++i)
			{
				real[i] = godot[i];
			}

			return Engine_DummyJoypad.Connected ? Engine_DummyJoypad.Roster(real) : real;
		}
		finally
		{
			((Godot.Collections.Array)godot).Dispose();
		}
	}

	/// <summary>Whether <paramref name="joypad"/> has a gamepad mapping.</summary>
	public static bool Known(int joypad) => Engine_DummyJoypad.Is(joypad) || Input.IsJoyKnown(joypad);

	/// <summary>The joypad's device name.</summary>
	public static string Name(int joypad) =>
		Engine_DummyJoypad.Is(joypad) ? Engine_DummyJoypad.Name : Input.GetJoyName(joypad);

	/// <summary>The joypad's SDL GUID.</summary>
	public static string Guid(int joypad) =>
		Engine_DummyJoypad.Is(joypad) ? Engine_DummyJoypad.Guid : Input.GetJoyGuid(joypad);

	/// <summary>The joypad's USB vendor and product ids, 0 where the platform states none.</summary>
	public static (int Vendor, int Product) Ids(int joypad)
	{
		if (Engine_DummyJoypad.Is(joypad))
		{
			return Engine_DummyJoypad.Ids;
		}

		int vendor = 0, product = 0;
		Godot.Collections.Dictionary info = Input.GetJoyInfo(joypad);
		try
		{
			if (info.TryGetValue("vendor_id", out Variant v) && v.VariantType == Variant.Type.Int)
			{
				vendor = v.AsInt32();
			}

			if (info.TryGetValue("product_id", out Variant p) && p.VariantType == Variant.Type.Int)
			{
				product = p.AsInt32();
			}
		}
		finally
		{
			info.Dispose();
		}

		return (vendor, product);
	}

	/// <summary>Starts the joypad's motors (0..1 each), until stopped.</summary>
	public static void StartVibration(int joypad, float weak, float strong)
	{
		if (Engine_DummyJoypad.Is(joypad))
		{
			Engine_DummyJoypad.StartVibration(weak, strong);
			return;
		}

		Input.StartJoyVibration(joypad, weak, strong, 0f);
	}

	/// <summary>Stops the joypad's motors.</summary>
	public static void StopVibration(int joypad)
	{
		if (Engine_DummyJoypad.Is(joypad))
		{
			Engine_DummyJoypad.StopVibration();
			return;
		}

		Input.StopJoyVibration(joypad);
	}
}
