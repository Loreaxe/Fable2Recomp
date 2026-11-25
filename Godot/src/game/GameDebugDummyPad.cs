using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The debug channel's `pads` op: the roster report, and the dummy controller's connect and
/// disconnect (<see cref="Engine_DummyJoypad"/>). The dummy is driven by the `press` op's joypad
/// mode like any joypad (<see cref="GameDebugJoypad"/>).
/// </summary>
public static class GameDebugDummyPad
{
	/// <summary>The guest pad a press on the dummy reaches, or −1 with <paramref name="refused"/> saying why not.</summary>
	public static int PressPad(out string? refused)
	{
		refused = null;
		if (!Engine_DummyJoypad.Connected)
		{
			refused = "the dummy controller is not connected (the pads op, do=connect)";
			return -1;
		}

		int pad = Engine_DummyJoypad.PadOnPress(GameInput.PadHolders);
		if (pad < 0)
		{
			refused = $"the dummy controller's seat, guest pad {Engine_DummyJoypad.Seat}, was taken: {GameInput.DescribeJoypads()}";
		}

		return pad;
	}

	/// <summary>
	/// Answers `pads`: <paramref name="verb"/> list (the default), connect (at guest pad
	/// <paramref name="seat"/>, as a <paramref name="family"/> controller) or disconnect. Every
	/// answer carries the report; a refusal carries `error` and changes nothing.
	/// </summary>
	public static Dictionary<string, object?> Answer(string? verb, int? seat, string? family)
	{
		string? refused = null;
		switch ((verb ?? "list").Trim().ToLowerInvariant())
		{
			case "list":
				break;
			case "connect":
				if (Engine_DummyJoypad.ParseFamily(family) is not { } stated)
				{
					refused = $"family '{family}' is not xbox, playstation, playstation5 or nintendo";
					break;
				}

				refused = Engine_DummyJoypad.Connect(seat ?? Engine_DummyJoypad.DefaultSeat, stated,
					Engine_Joypads.Connected(), GameInput.PadHolders);
				if (refused is null)
				{
					GD.Print($"[input] debug dummy controller connected: joypad {Engine_DummyJoypad.Device} " +
						$"'{Engine_DummyJoypad.Name}', its first press takes guest pad {Engine_DummyJoypad.Seat}");
				}

				break;
			case "disconnect":
				if (!Disconnect())
				{
					refused = "the dummy controller is not connected";
				}

				break;
			default:
				refused = "do is list, connect or disconnect";
				break;
		}

		Dictionary<string, object?> body = Report();
		if (refused is not null)
		{
			body["ok"] = false;
			body["error"] = refused;
		}

		return body;
	}

	/// <summary>Releases what the dummy holds and unplugs it; false when it was not connected.</summary>
	public static bool Disconnect()
	{
		if (!Engine_DummyJoypad.Connected)
		{
			return false;
		}

		GameDebugJoypad.ReleaseDummy();
		Engine_DummyJoypad.Disconnect();
		GD.Print("[input] debug dummy controller disconnected");
		return true;
	}

	private static Dictionary<string, object?> Report()
	{
		(List<Dictionary<string, object?>> joypads, List<Dictionary<string, object?>> pads) = GameInput.DescribePads();
		return new Dictionary<string, object?>
		{
			["dummy"] = new Dictionary<string, object?>
			{
				["connected"] = Engine_DummyJoypad.Connected,
				["device"] = Engine_DummyJoypad.Device,
				["seat"] = Engine_DummyJoypad.Seat,
				["pad"] = Engine_DummyJoypad.Connected ? GameInput.PadOfJoypad(Engine_DummyJoypad.Device) : -1,
				["family"] = Engine_DummyJoypad.Connected ? Engine_DummyJoypad.Family.ToString() : null,
				["rumble"] = Engine_DummyJoypad.Rumble is { } told
					? new Dictionary<string, object?> { ["weak"] = told.Weak, ["strong"] = told.Strong }
					: null,
			},
			["joypads"] = joypads,
			["pads"] = pads,
		};
	}
}
