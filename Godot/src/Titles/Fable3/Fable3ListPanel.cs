namespace Refii.Titles.Fable3;

/// <summary>
/// THE PANEL A BOX SCREEN'S ROWS STAND ON, as the screen's own .bgf authors it
/// (<see cref="Fable3ScreenFrames.PanelOf"/>), in canvas units about the screen centre, y up: the
/// box's centre line (<see cref="CentreX"/>, its frame art's x), where the file stands its first row
/// text (<see cref="LabelX"/>, <see cref="FirstRowY"/>) and how (<see cref="LabelAlign"/>: 0 left,
/// 1 centred, the .fac's), how far apart its rows stand (<see cref="Pitch"/>) and where its rows'
/// values begin (<see cref="ValueX"/>). The one list template (<see cref="Fable3ListScreen"/>) places
/// every screen by these rules; a screen with no panel keeps the template's own column.
/// </summary>
/// <remarks>
/// <para>Measured against the files (chain positions, sub_82818760's sum, the widescreen adapters'
/// writes taken): hero status's rows at x 5 (`HeroStatus` comp 275 + `Section1` −70 +
/// `Titles.Strength` −200), 30 apart, its meters' zero at x 257, its frame at x 275; the king's
/// schedule's tasks at x −140, 102 apart, the tick boxes at x −190, the paper at x 0. The template's
/// own column (the shared view's slot table under <see cref="Refii.Ui.Engine_FrontEndMenuView.CanvasAnchor"/>)
/// stands labels near x −390, off both.</para>
/// </remarks>
public readonly record struct Fable3ListPanel(
	float CentreX, float LabelX, int LabelAlign, float? FirstRowY = null, float? Pitch = null, float? ValueX = null)
{
	/// <summary>The value bars' span on a list with no panel (the Options settings page).</summary>
	public const float DefaultSliderLeft = 40f;
	public const float DefaultSliderRight = 360f;

	/// <summary>The room the percentage drawn after a bar takes, kept inside the panel.</summary>
	public const float PercentRoom = 48f;

	/// <summary>Whether the rows stand centred on <see cref="LabelX"/> (the choice boxes) rather than start there.</summary>
	public bool Centred => LabelAlign == 1;

	/// <summary>
	/// The panel's inner right edge: the row column mirrored about the box's centre line (the file
	/// insets its rows the same on both sides), or null for centred rows, whose inset the file does not
	/// say.
	/// </summary>
	public float? Right => Centred ? null : CentreX + System.Math.Abs(CentreX - LabelX);

	/// <summary>The inner width the panel's text may use: twice the row column's inset, or null for centred rows.</summary>
	public float? InnerWidth => Right is { } right ? 2f * (right - CentreX) : null;

	/// <summary>
	/// A value bar's span: from the panel's value column, the template's length, ended short of the
	/// panel's right edge by the percentage's room; the template's own span with no panel.
	/// </summary>
	public static (float Left, float Right) Slider(Fable3ListPanel? panel)
	{
		if (panel is not { } at)
		{
			return (DefaultSliderLeft, DefaultSliderRight);
		}

		float left = at.ValueX ?? DefaultSliderLeft;
		float right = left + (DefaultSliderRight - DefaultSliderLeft);
		if (at.Right is { } edge)
		{
			right = System.Math.Min(right, edge - PercentRoom);
		}

		return (left, System.Math.Max(right, left + 1f));
	}

	/// <summary>The 0..1 canvas x <paramref name="canvasX"/> stands for on a bar from <paramref name="left"/> to <paramref name="right"/>, clamped.</summary>
	public static float Fraction(float canvasX, float left, float right) =>
		right > left ? System.Math.Clamp((canvasX - left) / (right - left), 0f, 1f) : 0f;

	/// <summary>
	/// Where a row's pips start, <paramref name="width"/> wide, for a label drawn from
	/// <paramref name="labelLeft"/> to <paramref name="labelEnd"/> with pips <paramref name="side"/>
	/// a side: at the value column when they end before the label begins (the schedule's ticks,
	/// authored left of its tasks), else at the value column or one side past the label, whichever is
	/// further right.
	/// </summary>
	public static float PipLeft(Fable3ListPanel? panel, float labelLeft, float labelEnd, float side, float width)
	{
		float column = panel?.ValueX ?? DefaultSliderLeft;
		if (panel is not null && column + width + side <= labelLeft)
		{
			return column;
		}

		return System.Math.Max(column, labelEnd + side);
	}

	/// <summary>
	/// A CHOICE ROW'S ARROWS (<see cref="Fable3ScreenPage.Row.Choice"/>), canvas x: the left arrow ends
	/// <paramref name="side"/> × 0.5 before the choice and the right one starts as far after it. The
	/// choice is the tail of a label drawn from <paramref name="labelLeft"/> to <paramref name="labelEnd"/>;
	/// its share of that span is its measured width over the whole line's (<paramref name="choiceWidth"/>
	/// over <paramref name="lineWidth"/>, any one unit), so the arrows follow the label at whatever size
	/// it is drawn.
	/// </summary>
	public static (float LeftEnd, float RightStart) ChoiceArrows(float labelLeft, float labelEnd, float lineWidth, float choiceWidth, float side)
	{
		float span = System.Math.Max(0f, labelEnd - labelLeft);
		float choice = lineWidth > 0f ? span * System.Math.Clamp(choiceWidth / lineWidth, 0f, 1f) : 0f;
		float gap = side * 0.5f;
		return (labelEnd - choice - gap, labelEnd + gap);
	}

	/// <summary>
	/// Where a page's rows stand vertically on a panel that states its first row: the view's canvas y
	/// that puts the window's top row — which rests on the still list's top slot, at
	/// <paramref name="topSlotY"/>(pitch) in the view — on the authored first row, and the authored pitch
	/// every slot then stands from the next (<see cref="Refii.Ui.Engine_FrontEndMenuView.SlotPitch"/>; null
	/// keeps the slot table's spacing). The list's rows stand still (the view's <c>StillRows</c>), so a
	/// cursor moves over them and the rows stay on the panel. Null keeps the template's own column.
	/// </summary>
	public static (float ViewY, float? Pitch)? Rows(Fable3ListPanel? panel, System.Func<float?, float> topSlotY)
	{
		if (panel is not { FirstRowY: { } first } at)
		{
			return null;
		}

		float? pitch = at.Pitch is { } step && step > 0f ? step : null;
		return (first - topSlotY(pitch), pitch);
	}

	/// <summary>
	/// The description's centre line and widest line, canvas units: the panel's centre and inner
	/// width, never wider than <paramref name="widest"/>; the screen centre and <paramref name="widest"/>
	/// with no panel or centred rows.
	/// </summary>
	public static (float CentreX, float Width) Description(Fable3ListPanel? panel, float widest) =>
		panel is { } at
			? (at.CentreX, at.InnerWidth is { } inner ? System.Math.Min(inner, widest) : widest)
			: (0f, widest);
}
