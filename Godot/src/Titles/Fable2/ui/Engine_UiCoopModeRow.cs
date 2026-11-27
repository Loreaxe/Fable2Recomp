using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Player one's screen row on the co-op screen in local co-op: shared screen, split side by side or top
/// and bottom, or hybrid side by side or top and bottom (Refii.Camera.Engine_CameraScreens.Row). The guest
/// has no such row — the choice is the remaster's co-op screen (<c>coop_screen_mode</c> and the orientation)
/// — so it is drawn in the screen's own pickbox art, cloned from the pattern book's
/// `RightPage.Pickbox.pickbox1`, on player one's page where the shipped gold and experience sliders stood.
/// </summary>
public partial class Engine_UiCoopModeRow : Node3D
{
	/// <summary>What each row reads as, in Engine_CameraScreens.Row order.</summary>
	public static readonly string[] Labels =
	{
		"REFII_F2_COOP_MODE_SHARED",
		"REFII_F2_COOP_MODE_SIDE",
		"REFII_F2_COOP_MODE_STACKED",
		"REFII_F2_COOP_MODE_HYBRID_SIDE",
		"REFII_F2_COOP_MODE_HYBRID_STACKED",
	};

	/// <summary>The pickbox the row is cloned from, by its art name.</summary>
	public const string PatternName = "pickbox1";

	/// <summary>The theme that paints the label.</summary>
	public Engine_UiTheme? Theme { get; set; }

	private MeshInstance3D? _label;
	private Node3D? _left;
	private Node3D? _right;
	private string _painted = string.Empty;

	/// <summary>
	/// The row, cloned from the pattern book's pickbox — the file's art, not the live one, which
	/// carries whatever the guest last wrote into it. Null when the pattern has no pickbox.
	/// </summary>
	public static Engine_UiCoopModeRow? FromPattern(Node3D? pickbox, Engine_UiTheme? theme)
	{
		if (pickbox is null)
		{
			return null;
		}

		var row = new Engine_UiCoopModeRow { Name = "CoopModeRow", Theme = theme };
		var art = (Node3D)pickbox.Duplicate();
		art.Transform = new Transform3D(pickbox.Transform.Basis, Vector3.Zero);
		art.Visible = true;
		row.AddChild(art);
		row._label = Engine_UiTheme.FindByName(art, "text") as MeshInstance3D;
		row._left = Engine_UiTheme.FindByName(art, "arrowleft") as Node3D;
		row._right = Engine_UiTheme.FindByName(art, "arrowright") as Node3D;
		return row;
	}

	/// <summary>Paints the label for <paramref name="row"/>; free when it already reads so.</summary>
	public void Show(int row)
	{
		string display = Refii.Text.Engine_OwnWords.Say(Labels[Mathf.Clamp(row, 0, Labels.Length - 1)]);
		if (display == _painted || _label is null || Theme is null)
		{
			return;
		}

		_painted = display;
		int size = _label.HasMeta("text_size") ? (int)_label.GetMeta("text_size") : 20;
		int horizontal = _label.HasMeta("text_halign") ? (int)_label.GetMeta("text_halign") : 1;
		int vertical = _label.HasMeta("text_valign") ? (int)_label.GetMeta("text_valign") : 1;
		Vector2 box = _label.HasMeta("text_box") ? (Vector2)_label.GetMeta("text_box") : Vector2.Zero;
		Color colour = _label.HasMeta("text_colour") ? (Color)_label.GetMeta("text_colour") : Colors.White;

		// Full strength: the pickbox's text is authored at 50 and lit when its row has the focus,
		// and this is the only row on player one's page.
		Theme.Paint(
			_label, display, size, new Color(colour.R, colour.G, colour.B, 1f),
			horizontalAlign: horizontal, verticalAlign: vertical, boxWidth: box.X, boxHeight: box.Y);
	}

	/// <summary>−1 over the left arrow, +1 over the right, 0 elsewhere — for a canvas point.</summary>
	public int ArrowAt(Vector2 at)
	{
		if (_left is not null && Engine_UiPointer.CanvasBounds(_left) is { } left && left.Grow(8f).HasPoint(at))
		{
			return -1;
		}

		if (_right is not null && Engine_UiPointer.CanvasBounds(_right) is { } right && right.Grow(8f).HasPoint(at))
		{
			return 1;
		}

		return 0;
	}
}
