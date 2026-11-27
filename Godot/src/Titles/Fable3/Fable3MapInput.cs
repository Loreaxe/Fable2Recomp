namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// The world map's BUTTONS as Godot's levers (Fable3/src/hooks/src/map_input.cpp): while the map's
/// game mode has the input (<see cref="Fable3WorldMapMode.Owner"/>), each press of its owner's pad —
/// and of the keys and mouse buttons bound onto it — is handed to the map's own action forwarder as
/// that player, and LB / RB are stated held and released. The guest's pad no longer reaches the
/// forwarders for these. Pure, so the tests run it.
/// <para>THE OWNER IS THE GUEST'S, THE HANDS ARE WHOEVER IS HERE (<see cref="ActsAs"/>). The mode's
/// enter registers player one's receiver only when the switch manager's entity is player one's hero
/// or the game world's hero, and player two's otherwise (TU3 0x824EC048..0x824EC060: `cmplw r29,r30;
/// beq; cmplw r29,r3; beq; lwz r4,0x74(r27)` else `lwz r4,0x6C(r27)`; disc 0x824CC9A8..0x824CC9C0, +0x70
/// / +0x68) — a map opened in the world, outside the Sanctuary's map room, is player two's in a solo
/// game. The owner stands; a map whose owner has no pad connected here is driven by player one's pad,
/// keys and mouse, posted as that owner, so no map is ever left with no hands.</para>
/// </summary>
/// <remarks>
/// The actions are `worldmap/input.lua`'s own compares (EGameAction): 120 WORLD_MAP_ACCEPT (A), 121
/// WORLD_MAP_CANCEL (B), 123 / 124 the context actions (Y / X), 14 PAUSE (START); held LB / RB are raw
/// buttons 9 / 10 (`ProcessButtonHeld`: the camera's tilt). The analog actions (cursor, scroll, orbit:
/// 3, 118, 119, 122) and the trigger's toggle (125) are still the guest's own pad's — their floats'
/// scale and sign and the trigger's threshold are the control scheme's, not read yet — so the sticks
/// and the trigger stay published.
/// </remarks>
public static class Fable3MapInput
{
	private const uint ButtonStart = 0x0010u;
	private const uint ButtonLeftBumper = 0x0100u;
	private const uint ButtonRightBumper = 0x0200u;
	private const uint ButtonA = 0x1000u;
	private const uint ButtonB = 0x2000u;
	private const uint ButtonX = 0x4000u;
	private const uint ButtonY = 0x8000u;

	/// <summary>The map's pad buttons and the action each makes, in the order they are posted.</summary>
	private static readonly (uint Button, int Action)[] Pressed =
	{
		(ButtonA, 120), (ButtonB, 121), (ButtonY, 123), (ButtonX, 124), (ButtonStart, 14),
	};

	private static readonly uint[] Was = new uint[4];
	private static readonly bool[] Connected = new bool[4];
	private static int _lent;

	/// <summary>
	/// The player guest pad <paramref name="pad"/> (0-based) acts as on a map of <paramref name="owner"/>
	/// (0 = no map mode): the owner for the owner's own pad, and for player one's (pad 0, the keys and
	/// mouse folded in) while no pad of the owner's is connected; else the pad's own player, which the
	/// map does not read.
	/// </summary>
	public static int ActsAs(int pad, int owner, bool ownersPadConnected)
	{
		if (owner == 0)
		{
			return 0;
		}

		int ownersPad = Fable3ActingPlayer.PadOf(owner);
		if (pad == ownersPad || (pad == 0 && !ownersPadConnected))
		{
			return owner;
		}

		return Fable3ActingPlayer.OfPad(pad);
	}

	/// <summary>
	/// The levers for one pad's buttons going from <paramref name="was"/> to <paramref name="now"/>
	/// while the map is <paramref name="owner"/>'s (0 = no map mode): nothing unless
	/// <paramref name="player"/> is the owner.
	/// </summary>
	public static List<string> Levers(uint was, uint now, int player, int owner)
	{
		var levers = new List<string>();
		if (owner == 0 || player != owner)
		{
			return levers;
		}

		string p = Fable3ActingPlayer.Field(player);
		uint down = now & ~was;
		foreach ((uint button, int action) in Pressed)
		{
			if ((down & button) != 0u)
			{
				levers.Add("map:" + p + ":" + action.ToString(System.Globalization.CultureInfo.InvariantCulture));
			}
		}

		foreach ((uint button, int raw) in new[] { (ButtonLeftBumper, 9), (ButtonRightBumper, 10) })
		{
			if (((now ^ was) & button) != 0u)
			{
				levers.Add("map-held:" + p + ":" + raw.ToString(System.Globalization.CultureInfo.InvariantCulture) +
					((now & button) != 0u ? ":down" : ":up"));
			}
		}

		return levers;
	}

	/// <summary>
	/// One published pad state of guest pad <paramref name="pad"/> (0-based) with <paramref name="buttons"/>
	/// down: posts the map's levers for it. When the mode leaves, a bumper still down is released.
	/// </summary>
	public static void Drive(int pad, bool connected, uint buttons, System.Action<string> post)
	{
		if (pad < 0 || pad >= Was.Length)
		{
			return;
		}

		Connected[pad] = connected;
		int owner = Fable3WorldMapMode.Owner;
		int ownersPad = Fable3ActingPlayer.PadOf(owner);
		bool ownersConnected = ownersPad >= 0 && ownersPad < Connected.Length && Connected[ownersPad];
		int player = ActsAs(pad, owner, ownersConnected);
		if (owner != 0 && player == owner && pad != ownersPad && _lent != owner)
		{
			_lent = owner;
			Fable3WorldMapMode.Log($"[map-table] the world map is player {owner}'s and no pad of that player's is connected: " +
				$"player one's pad, keys and mouse drive it as player {owner}");
		}
		else if (owner == 0)
		{
			_lent = 0;
		}

		foreach (string lever in Levers(owner == 0 ? buttons : Was[pad], buttons, player, owner))
		{
			post(lever);
		}

		Was[pad] = buttons;
	}
}
