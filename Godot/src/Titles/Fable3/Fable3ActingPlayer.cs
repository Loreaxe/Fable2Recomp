namespace Refii.Titles.Fable3;

/// <summary>
/// The local player a press acts for, as Fable 3's handlers take it in r5: 1 or 2.
/// </summary>
/// <remarks>
/// The guest's GUI input object answers it from its player record (slot 4: `lwz r11,8(r3)` /
/// `lwz r3,0x24(r11)`, TU3 0x82295BA0 / disc 0x822978E0) and hands it to the box router and the
/// dispatcher with every action (TU3 0x82601B9C..0x82601BB4, 0x8260115C..0x82601178). What the
/// handlers do with it: a box with an owner takes the pad only from that player (`+28` against r5:
/// the message box TU3 0x82878FA4..0x82878FB0 / disc 0x8281B3AC..0x8281B3B8, the question TU3
/// 0x82879A68..0x82879A78 / disc 0x8281BD04..0x8281BD14, the adjuster's `+200`); the adjuster
/// reads player record 1 for a 1 and record 2 for anything else (`cmpwi r5,1`, TU3 0x8287A1F0 /
/// disc 0x8281C488); the menu box selects for 1 alone (`cmpwi r4,1`, TU3 0x82B512D4).
/// Godot states the same from its own device map: guest pad 0 is player one's, pad 1 player two's.
/// A Godot-owned surface reads the pad of each local player and posts each press with that
/// player; the guest's own gate decides whose press counts.
/// </remarks>
public static class Fable3ActingPlayer
{
	public const int One = 1;
	public const int Two = 2;

	/// <summary>The guest pad the local player two plays on.</summary>
	public const int PadOfTwo = 1;

	/// <summary>
	/// The player the pointer acts for: the keyboard and mouse are folded into guest pad 0
	/// (GameInput.Publish0), player one's. Every pointer lever passes this, never a literal.
	/// </summary>
	public static int Pointer => One;

	/// <summary>The guest pad of local <paramref name="player"/>: 0 for player one, <see cref="PadOfTwo"/> for player two.</summary>
	public static int PadOf(int player) => player == Two ? PadOfTwo : 0;

	/// <summary>The player guest pad <paramref name="pad"/> carries for the mods and the host surfaces: 1, 2, or 0 for a pad no local player holds.</summary>
	public static int LocalOfPad(int pad) => pad == 0 ? One : pad == PadOfTwo ? Two : 0;

	/// <summary>
	/// The presses of the two local players' pads, each edge-detected on its own pad: a button one
	/// player holds never hides the other's press of it.
	/// </summary>
	public sealed class Edges
	{
		private readonly uint[] _was = new uint[2];

		/// <summary>The buttons <paramref name="player"/>'s pad pressed since the last call for that player.</summary>
		public uint Pressed(int player, uint down)
		{
			int seat = player == Two ? 1 : 0;
			uint pressed = down & ~_was[seat];
			_was[seat] = down;
			return pressed;
		}

		/// <summary>Takes what is down now as already pressed, for both players.</summary>
		public void Settle(uint downOne, uint downTwo)
		{
			_was[0] = downOne;
			_was[1] = downTwo;
		}
	}

	/// <summary>
	/// Which player a hold is for, 0 for none: player one's pad or the pointer first, else player two's pad.
	/// </summary>
	public static int Holder(bool oneHolds, bool pointerHolds, bool twoHolds) =>
		oneHolds ? One : pointerHolds ? Pointer : twoHolds ? Two : 0;

	/// <summary>The player of guest pad <paramref name="pad"/> (0-based): 1 for pad 0, 2 for any other.</summary>
	public static int OfPad(int pad) => pad == 0 ? One : Two;

	/// <summary>The lever field for <paramref name="player"/>: any value but 2 is player one.</summary>
	public static string Field(int player) => player == Two ? "2" : "1";
}
