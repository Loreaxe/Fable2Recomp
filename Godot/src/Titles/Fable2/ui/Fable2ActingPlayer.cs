namespace Refii.Titles.Fable2;

/// <summary>
/// The player whose pad drove a press, as the guest's handlers take it in r5: the GUI input object's
/// vf+16. Player one's object (TU1 vtable 0x82002390, `sub_822A4E38`) answers 2 on an online joiner
/// (`game+0x11C == 1` and not both of `net+0x34` / `net+0x35`) and 1 otherwise; local player two's
/// (0x820F0694, `sub_828EDC88`) always answers 2. Godot states the same from its own device map: an
/// online client's pad is player two's, and in local co-op the host menus read the pad of the stated
/// menu owner (<see cref="Fable2Input.MenuOwner"/>).
/// </summary>
public static class Fable2ActingPlayer
{
	/// <summary>1 or 2. <paramref name="menuOwner"/> is the stated owner, 0 when none is stated.</summary>
	public static int Of(bool client, bool localCoop, int menuOwner) =>
		client || (localCoop && menuOwner == 2) ? 2 : 1;

	/// <summary>The co-op screen, TU1 numbering (the stated screen is normalised to it).</summary>
	public const uint CoopScreen = 24;

	/// <summary>
	/// The player a modal box over <paramref name="screenId"/> belongs to in local co-op, 0 when the
	/// screen names none. The co-op screen's boxes (39, 40, 41, 43) are queued by player two's
	/// handler alone (`sub_82438CF8`, disc `sub_82479F18`) and its OnModalAnswer takes player two's
	/// answer only: TU1 `8243A838 cmplwi cr6, r5, 2` / `bne`, disc `8247BA30`.
	/// </summary>
	public static int BoxOwner(bool localCoop, uint screenId, bool modalUp) =>
		localCoop && modalUp && screenId == CoopScreen ? 2 : 0;

	/// <summary>The player a pad's own press acts for: <paramref name="padPlayer"/> (1 or 2, the pad's
	/// local player), or 2 for any pad of an online client.</summary>
	public static int OfPad(bool client, int padPlayer) => client ? 2 : padPlayer;
}
