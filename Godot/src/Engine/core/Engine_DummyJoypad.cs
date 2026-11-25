using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>
/// The debug channel's dummy controller: one more joypad on the roster the game reads
/// (<see cref="Engine_Joypads"/>), there only while the channel has connected it. Pure: its identity,
/// its seat and what its motors were last told; its buttons and axes live in Godot's own polled
/// state under <see cref="Device"/>, where every reader of a joypad finds them.
/// </summary>
/// <remarks>
/// <para>THE SEAT. A joypad takes the lowest free guest pad on its first press. Player one of a
/// debug-driven run has no joypad (the scripted presses are pad 0's), so the dummy would become
/// player one: it states the guest pad its first press takes instead (<see cref="Seat"/>, pad 1
/// unless asked otherwise) and takes no other.</para>
/// <para>Not a device of the machine: nothing outside the game process can see or read it.</para>
/// </remarks>
public static class Engine_DummyJoypad
{
	/// <summary>The dummy's joypad id: the last of Godot's sixteen, which hands real pads the lowest unused id.</summary>
	public const int Device = 15;

	/// <summary>The guest pad the dummy takes when none is asked for: the local drop-in's.</summary>
	public const int DefaultSeat = 1;

	/// <summary>The guest pads a joypad can hold.</summary>
	public const int Pads = 4;

	/// <summary>Whether the debug channel has the dummy connected.</summary>
	public static bool Connected { get; private set; }

	/// <summary>The guest pad the dummy's first press takes; −1 while disconnected.</summary>
	public static int Seat { get; private set; } = -1;

	/// <summary>The family the dummy states itself as, for the prompts' glyphs.</summary>
	public static ControllerFamily Family { get; private set; } = ControllerFamily.Xbox;

	/// <summary>The pair the dummy's motors were last told (0..1), null while they are stopped.</summary>
	public static (float Weak, float Strong)? Rumble { get; private set; }

	/// <summary>Raised after a connect and after a disconnect, as Godot's own connection signal is.</summary>
	public static event Action? Changed;

	/// <summary>Whether <paramref name="joypad"/> is the connected dummy.</summary>
	public static bool Is(int joypad) => Connected && joypad == Device;

	/// <summary>The dummy's device name as a joypad of <paramref name="family"/> states it.</summary>
	public static string NameOf(ControllerFamily family) => family switch
	{
		ControllerFamily.PlayStation => "Refii Debug Dummy (DualShock 4)",
		ControllerFamily.PlayStation5 => "Refii Debug Dummy (DualSense)",
		ControllerFamily.Nintendo => "Refii Debug Dummy (Switch Pro Controller)",
		_ => "Refii Debug Dummy (Xbox 360 Controller)",
	};

	/// <summary>The USB vendor and product ids a joypad of <paramref name="family"/> states.</summary>
	public static (int Vendor, int Product) IdsOf(ControllerFamily family) => family switch
	{
		ControllerFamily.PlayStation => (Engine_ControllerFamily.SonyVendor, 0x09CC),
		ControllerFamily.PlayStation5 => (Engine_ControllerFamily.SonyVendor, 0x0CE6),
		ControllerFamily.Nintendo => (Engine_ControllerFamily.NintendoVendor, 0x2009),
		_ => (Engine_ControllerFamily.MicrosoftVendor, 0x028E),
	};

	/// <summary>The SDL GUID of a USB joypad with those ids: bus, CRC, vendor, product and version as little-endian words.</summary>
	public static string GuidOf(ControllerFamily family)
	{
		(int vendor, int product) = IdsOf(family);
		static string Le16(int value) => $"{value & 0xFF:x2}{(value >> 8) & 0xFF:x2}";
		return "03000000" + Le16(vendor) + "0000" + Le16(product) + "000000000000";
	}

	/// <summary>The connected dummy's name, GUID and ids.</summary>
	public static string Name => NameOf(Family);
	public static string Guid => GuidOf(Family);
	public static (int Vendor, int Product) Ids => IdsOf(Family);

	/// <summary>The family a `family` argument names (xbox, playstation, playstation5, nintendo), or null.</summary>
	public static ControllerFamily? ParseFamily(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
	{
		"" or "xbox" => ControllerFamily.Xbox,
		"playstation" or "ps4" => ControllerFamily.PlayStation,
		"playstation5" or "ps5" => ControllerFamily.PlayStation5,
		"nintendo" or "switch" => ControllerFamily.Nintendo,
		_ => null,
	};

	/// <summary>
	/// Connects the dummy at <paramref name="seat"/>. <paramref name="joypads"/> are the joypads
	/// Godot has connected, <paramref name="holders"/> the joypad holding each guest pad (−1 free).
	/// Returns null, or why not: nothing changes then.
	/// </summary>
	public static string? Connect(int seat, ControllerFamily family, IReadOnlyCollection<int> joypads,
		IReadOnlyList<int> holders)
	{
		if (Connected)
		{
			return $"the dummy controller is already connected (joypad {Device}, guest pad {Seat})";
		}

		if (seat < 0 || seat >= Pads)
		{
			return $"guest pad {seat} is not 0..{Pads - 1}";
		}

		foreach (int joypad in joypads)
		{
			if (joypad == Device)
			{
				return $"a real joypad holds id {Device}, the dummy's";
			}
		}

		if (seat < holders.Count && holders[seat] >= 0)
		{
			return $"guest pad {seat} is held by joypad {holders[seat]}";
		}

		Connected = true;
		Seat = seat;
		Family = family == ControllerFamily.Unknown ? ControllerFamily.Xbox : family;
		Rumble = null;
		Changed?.Invoke();
		return null;
	}

	/// <summary>Disconnects the dummy; false when it was not connected. Its held inputs are the caller's to release first.</summary>
	public static bool Disconnect()
	{
		if (!Connected)
		{
			return false;
		}

		Connected = false;
		Seat = -1;
		Rumble = null;
		Changed?.Invoke();
		return true;
	}

	/// <summary>The joypads the game reads: <paramref name="real"/>, and the dummy after them while connected.</summary>
	public static int[] Roster(IReadOnlyList<int> real)
	{
		var roster = new int[real.Count + (Connected ? 1 : 0)];
		for (int i = 0; i < real.Count; ++i)
		{
			roster[i] = real[i];
		}

		if (Connected)
		{
			roster[^1] = Device;
		}

		return roster;
	}

	/// <summary>
	/// The guest pad a press on the dummy reaches: the pad it holds, else its seat while that is
	/// free; −1 while disconnected or when another joypad took the seat.
	/// </summary>
	public static int PadOnPress(IReadOnlyList<int> holders)
	{
		if (!Connected)
		{
			return -1;
		}

		for (int pad = 0; pad < holders.Count; ++pad)
		{
			if (holders[pad] == Device)
			{
				return pad;
			}
		}

		return Seat >= 0 && Seat < holders.Count && holders[Seat] < 0 ? Seat : -1;
	}

	/// <summary>
	/// The guest pad <paramref name="joypad"/> takes on its first press: the dummy its seat, any
	/// other joypad the lowest free pad. −1 for none.
	/// </summary>
	public static int PadForFirstPress(int joypad, IReadOnlyList<int> holders)
	{
		if (Is(joypad))
		{
			return PadOnPress(holders);
		}

		for (int pad = 0; pad < holders.Count; ++pad)
		{
			if (holders[pad] < 0)
			{
				return pad;
			}
		}

		return -1;
	}

	/// <summary>The guest states the dummy's motors: kept for the debug channel, nothing buzzes.</summary>
	public static void StartVibration(float weak, float strong) => Rumble = (weak, strong);

	/// <summary>The dummy's motors are stopped.</summary>
	public static void StopVibration() => Rumble = null;
}
