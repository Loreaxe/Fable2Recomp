namespace Refii.Ui;

/// <summary>
/// THE HUD TOPBOX'S ROWS, laid out from the text Godot draws — the one place every TopBox
/// channel (the quest banner, job/property/shop signs, info boxes) gets its geometry.
/// </summary>
/// <remarks>
/// The shipped box fit measures the record's lines in the guest's own font and stacks them
/// (`sub_823FC510` → `sub_823FC8F0`, disc `sub_8257B4F0` → `sub_8257B8D0`): each line bound in
/// TextName registry 6 is placed at the box's left margin and the cursor drops by that line's
/// measured height (`v13 -= line[+12]`, TU1 0x823fcabc), then TopBoxX/TopBoxY are set from the
/// widest line and the summed heights (`sub_823FE638`) and the button row rides TopBoxY. None of
/// that reaches Godot as geometry: the placing writer `sub_824251E0` (disc `sub_823F7678`) is
/// existence-checked — it asks the service's vf +12 for `position.x` (0x8242524c, disc
/// 0x823f76e4) and writes nothing on a headless element, whose numeric attributes are absent —
/// and the guest's font metrics are not the ones Godot draws with anyway. So the rows are placed
/// here, in the order hud.bgf authors them under `TopBox` (`Title`/`TitleText` bound TopTitle,
/// `Main`/`Text` bound TopMain, then `Buttons`), stacked top-down like the fit stacks them, from
/// the extents of the meshes actually drawn. The layout meters TopBoxX/TopBoxY are left to the
/// guest's own consumers and never move these rows (Engine_UiBindings.IsLayoutMeter).
///
/// Coordinates are the HUD canvas's: +Y up, an extent's <see cref="Extent.Bottom"/> its lowest
/// edge. Every answer is a translation from where the row stands now to where it belongs, so
/// applying it again the next frame moves nothing.
/// </remarks>
public static class Engine_UiTopBoxLayout
{
	/// <summary>
	/// The gap between rows as a share of the body's point size. The fit's pitch is the font's
	/// line height; Godot's text meshes are measured to the glyphs, so the leading the font
	/// would have carried is put back between rows.
	/// </summary>
	public const float LeadingOfBodySize = 0.3f;

	/// <summary>One drawn row's rectangle in canvas units, +Y up.</summary>
	public readonly record struct Extent(float Left, float Bottom, float Width, float Height)
	{
		public float Right => Left + Width;

		public float Top => Bottom + Height;

		public float CentreX => Left + (Width * 0.5f);

		public Extent Moved(Offset by) => this with { Left = Left + by.X, Bottom = Bottom + by.Y };

		public Extent Union(Extent other)
		{
			float left = System.Math.Min(Left, other.Left);
			float bottom = System.Math.Min(Bottom, other.Bottom);
			return new Extent(left, bottom,
				System.Math.Max(Right, other.Right) - left,
				System.Math.Max(Top, other.Top) - bottom);
		}
	}

	/// <summary>A translation in canvas units.</summary>
	public readonly record struct Offset(float X, float Y)
	{
		public static readonly Offset None = new(0f, 0f);
	}

	/// <summary>Each row's move and the rectangle the arranged rows occupy (null when none is drawn).</summary>
	/// <param name="Text">The title and body alone — what a panel authored over the lines covers.</param>
	public readonly record struct Arrangement(Offset Title, Offset Body, Offset Buttons, Extent? Bounds,
		Extent? Text = null);

	/// <summary>
	/// Stacks the drawn rows under <paramref name="anchorTop"/>: the title row (title text with
	/// any inline icons, the job sign's stars among them), then the body, both centred on
	/// <paramref name="anchorX"/>; then the button row, right-aligned to the text above it — the
	/// side hud.bgf authors `Buttons` on — or centred when the box carries buttons only.
	/// </summary>
	/// <param name="title">The TopTitle text as drawn, or null when it is not drawn.</param>
	/// <param name="body">The TopMain text as drawn, or null.</param>
	/// <param name="buttons">The drawn content of the button row, or null.</param>
	/// <param name="anchorX">The box's centre line: where TopBox authors its lines' origin.</param>
	/// <param name="anchorTop">The top of the first row.</param>
	/// <param name="leading">The gap between rows.</param>
	public static Arrangement Arrange(Extent? title, Extent? body, Extent? buttons,
		float anchorX, float anchorTop, float leading)
	{
		float cursor = anchorTop;
		Extent? bounds = null;
		Extent? text = null;

		Offset Place(Extent row, float centre)
		{
			var by = new Offset(centre - row.CentreX, cursor - row.Top);
			Extent placed = row.Moved(by);
			cursor = placed.Bottom - leading;
			bounds = bounds is { } b ? b.Union(placed) : placed;
			return by;
		}

		Offset titleBy = Offset.None;
		if (title is { } t)
		{
			titleBy = Place(t, anchorX);
			text = t.Moved(titleBy);
		}

		Offset bodyBy = Offset.None;
		if (body is { } m)
		{
			bodyBy = Place(m, anchorX);
			Extent placed = m.Moved(bodyBy);
			text = text is { } x ? x.Union(placed) : placed;
		}

		Offset buttonsBy = Offset.None;
		if (buttons is { } r)
		{
			float centre = text is { } above ? above.Right - (r.Width * 0.5f) : anchorX;
			buttonsBy = Place(r, centre);
		}

		return new Arrangement(titleBy, bodyBy, buttonsBy, bounds, text);
	}

	/// <summary>The gap between rows for a body drawn at <paramref name="bodySize"/>.</summary>
	public static float Leading(int bodySize) => System.Math.Max(1, bodySize) * LeadingOfBodySize;

	/// <summary>
	/// THE WIDTH THE LINES WRAP AT IN THIS VIEW: the title's stated width (<paramref name="statedWidth"/>,
	/// Fable 2's Engine_UiHudTopBox.WrapWidth — the guest's own `max.x`, measured with its widescreen
	/// adapter on a 16:9 screen) while the view shows at least a 16:9 canvas
	/// (<paramref name="wideVisibleWidth"/>), and the same share of a narrower view's visible width
	/// (<paramref name="visibleWidth"/>): a split-screen half (949.5 canvas units side by side against
	/// 1265.9) wraps at three quarters of the stated width instead of running its line across the other
	/// player's half. 0 or less states no width and is passed through.
	/// </summary>
	public static float WrapWidthIn(float statedWidth, float visibleWidth, float wideVisibleWidth)
	{
		if (statedWidth <= 0f || visibleWidth <= 0f || wideVisibleWidth <= 0f || visibleWidth >= wideVisibleWidth)
		{
			return statedWidth;
		}

		return statedWidth * (visibleWidth / wideVisibleWidth);
	}
}
