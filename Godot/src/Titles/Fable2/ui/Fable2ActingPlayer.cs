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
}
