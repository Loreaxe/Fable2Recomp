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

	/// <summary>
	/// The player who answers what the GUI shows now, 0 for none stated: the stated owner on a screen
	/// with no box, and player two through any box while they own the GUI. The GUI tick picks its
	/// input object by the owner alone, whatever box stands — TU1 `lhGuiManager_TickUpdate`
	/// `8229ac48 bl sub_8236E400`, then `8229ac5c lwz r31,0x18(r11)` (player two's) or
	/// `8229ac64 lwz r31,0x14(r11)`; disc `8229a808`, `8229a81c` / `8229a824` — so player two's
	/// henchman Quit box (26 / 27) is player two's to answer.
	/// </summary>
	public static int Answerer(int statedOwner, uint modalMode) =>
		statedOwner == 2 || modalMode == 1 ? statedOwner : 0;

	/// <summary>
	/// The player the menu-owner tag names, 0 for none: while a menu is up, the player whose pad drives
	/// it (<paramref name="inputOwner"/>, <see cref="Fable2Input.MenuOwner"/>) whenever one is stated;
	/// through a box that states none (player one's menu under a box, <see cref="Answerer"/> 0) the
	/// player it named last; with nothing named yet, the GUI's stated owner. The owner a world screen's
	/// opener states reaches the host one guest tick after the screen does (the payload samples it at
	/// the tick's start), so the first frame cannot be latched: the crime screen opened by player two's
	/// arrest read "Player 1" for its whole life.
	/// </summary>
	public static int TagOwner(int shown, bool menuUp, int inputOwner, int statedOwner) =>
		!menuUp ? 0 : inputOwner != 0 ? inputOwner : shown != 0 ? shown : statedOwner;

	/// <summary>The player a pad's own press acts for: <paramref name="padPlayer"/> (1 or 2, the pad's
	/// local player), or 2 for any pad of an online client.</summary>
	public static int OfPad(bool client, int padPlayer) => client ? 2 : padPlayer;
}
