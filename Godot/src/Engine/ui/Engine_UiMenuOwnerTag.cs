using Godot;

namespace Refii.Ui;

/// <summary>
/// Whose menu is open, in local co-op: "Player 1" or "Player 2" in that player's colour, on the
/// pause banner's line beside its title (centred at the top of the window when a screen opened from
/// the world has no banner) — in the banner's band, so it never covers a list's row.
/// </summary>
/// <remarks>
/// <para>THE MENUS FILL THE WINDOW AS IN SINGLE PLAYER, on a shared screen and on split screen
/// alike: the original draws one screen, so its book is whole for both players, and only the
/// owner's pad drives it (the GUI tick runs the owner's input handler alone — TU1
/// `lhGuiManager_TickUpdate` 0x8229AC48 `bl sub_8236E400`, then App+0x18 or App+0x14; disc
/// 0x8229A808). The guest states no cue for the owner — player two's pause root differs from the
/// hero's only by its rows — so this tag is the remaster's, drawn by the one shared view every
/// local co-op menu goes through.</para>
/// <para>The colours are ours: the guest keeps no per-player colour for local co-op.</para>
/// </remarks>
public partial class Engine_UiMenuOwnerTag : Node3D
{
	/// <summary>Player one's colour: the book's gold.</summary>
	public static readonly Color PlayerOneColour = new(0.96f, 0.80f, 0.42f);

	/// <summary>Player two's colour: a clear blue against the parchment and the dark chrome.</summary>
	public static readonly Color PlayerTwoColour = new(0.52f, 0.76f, 1f);

	/// <summary>A player's colour (1 or 2).</summary>
	public static Color ColourOf(int player) => player == 2 ? PlayerTwoColour : PlayerOneColour;

	/// <summary>What the tag reads for a player (1 or 2).</summary>
	public static string TextOf(int player) =>
		Refii.Text.Engine_OwnWords.Say(player == 2 ? "REFII_PLAYER_2" : "REFII_PLAYER_1");

	/// <summary>The theme that paints the text.</summary>
	public Engine_UiTheme? Theme { get; set; }

	private readonly MeshInstance3D _label = new() { Name = "text" };
	private int _painted;
	private int _paintedSize;
	private bool _paintedLeft;

	public override void _Ready()
	{
		if (_label.GetParent() is null)
		{
			AddChild(_label);
		}
	}

	/// <summary>
	/// Shows <paramref name="player"/>'s tag (0 hides it) at <paramref name="at"/> on its parent's
	/// canvas, in text of <paramref name="pointSize"/>, starting there when <paramref name="leftAligned"/>
	/// and centred on it otherwise; free when it already reads so.
	/// </summary>
	public void Show(int player, Vector3 at, int pointSize, bool leftAligned = false)
	{
		Visible = player != 0;
		if (player == 0)
		{
			return;
		}

		Position = at;
		if ((player == _painted && pointSize == _paintedSize && leftAligned == _paintedLeft) || Theme is null)
		{
			return;
		}

		_painted = player;
		_paintedSize = pointSize;
		_paintedLeft = leftAligned;
		if (_label.GetParent() is null)
		{
			AddChild(_label);
		}

		Theme.Paint(_label, TextOf(player), pointSize, ColourOf(player), horizontalAlign: leftAligned ? 0 : 1, verticalAlign: 1);
	}
}
