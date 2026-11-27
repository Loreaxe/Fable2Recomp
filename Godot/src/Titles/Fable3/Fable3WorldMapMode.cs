namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// The world map's GAME MODE as the guest states it (Fable3/src/hooks/src/map_input.cpp): which
/// player's receiver it registered when it took the input (`@WorldMap.Entered(player)`: the opener's —
/// the map reads that one pad), and that it gave the input up (`@WorldMap.Left`). The scene the map
/// draws is <see cref="Fable3MapTable"/>'s, stated apart; the two together say whether a presented
/// map still reads input. Pure, so the tests run it.
/// </summary>
/// <remarks>
/// The mode's leave (TU3 sub_824EC590 / disc sub_824CCB58) unregisters its receivers and does NOT
/// un-present the scene: that is the close's (TU3 sub_824ED850 / disc sub_824CDBB8), which runs only
/// from the mode's own update after WorldMap.Exit. GUI.ToggleWorldMap on an open map pops the mode
/// bare (TU3 0x8254D100 / disc 0x8251D310), leaving the scene presented with no reader.
/// </remarks>
public static class Fable3WorldMapMode
{
	private static bool _leftPresented;

	/// <summary>The player (1 or 2) the mode reads while it has the input, 0 otherwise.</summary>
	public static int Owner { get; private set; }

	public static System.Action<string> Log { get; set; } = message => GD.Print(message);

	public static void Register(Engine_UiGuiNatives router)
	{
		router.Register("@WorldMap.Entered", call => Entered((int)call.Argument(0).Number));
		router.Register("@WorldMap.Left", _ => Left());
	}

	/// <summary>The mode took the input for <paramref name="player"/>.</summary>
	public static void Entered(int player)
	{
		Owner = player is 1 or 2 ? player : 0;
		_leftPresented = false;
		Log($"[map-table] the world map has the input: player {Owner}'s");
	}

	/// <summary>The mode gave the input up.</summary>
	public static void Left()
	{
		_leftPresented = Owner != 0;
		Owner = 0;
	}

	/// <summary>The map scene was presented or un-presented (the close un-presents it; so does a later present).</summary>
	public static void ScenePresented(bool on)
	{
		if (!on)
		{
			_leftPresented = false;
		}
	}

	/// <summary>
	/// Whether a scene that is <paramref name="presented"/> now was left standing by a mode that gave
	/// the input up without its close.
	/// </summary>
	public static bool Stranded(bool presented)
	{
		if (presented && _leftPresented && !_said)
		{
			_said = true;
			Log("[map-table] the world map's mode left with its scene still presented (popped without its close): " +
				"the map reads no input now; the keys return to the game under it");
		}
		else if (!presented || !_leftPresented)
		{
			_said = false;
		}

		return presented && _leftPresented;
	}

	private static bool _said;

	/// <summary>Forgets everything (a new game session; the tests).</summary>
	public static void Reset()
	{
		Owner = 0;
		_leftPresented = false;
		_said = false;
	}
}
