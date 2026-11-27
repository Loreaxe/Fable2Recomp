namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// One page of a Fable 3 box-manager screen as Godot draws it: its title, its tabs (the PC layout's
/// tab strip, or none), its rows (each one line, label then value, a value bar or pips beside it or not),
/// the cursor row (or none), the cursor row's description and the button prompts. Pure data, so
/// the screens' models can be tested.
/// </summary>
public readonly record struct Fable3ScreenPage(
	string Title, IReadOnlyList<Fable3ScreenPage.Row> Rows, int Selected, string Description,
	IReadOnlyList<Fable3ScreenPage.Prompt> Prompts, bool Cursor, Fable3ScreenPage.QueryBox? Query = null,
	IReadOnlyList<string>? Tabs = null, int Tab = -1)
{
	/// <summary>
	/// A question the screen asks over its page (Options' New Game / overwrite confirmations): an
	/// optional title, the question and its prompts. The guest answers it from the pad.
	/// </summary>
	public sealed record QueryBox(string Title, string Text, IReadOnlyList<Prompt> Prompts);

	/// <summary>
	/// One row: a stable name, the shown line, ghosting, its description (a tag or text) and its
	/// MARK, drawn beside the line by the template, never spelt with characters (the title's fonts
	/// hold no box or tick glyphs): a value bar's 0..1 value, a slider the pointer drags, pips,
	/// <c>Filled</c> of <c>Of</c> squares, which take no pointer, or a CHOICE — the value the line
	/// ends with, which left / right step, drawn between the left and right arrows the box's file
	/// authors beside it (`bedmenubox` `Sleep.Arrows.Arrow_Left` / `Arrow_Right`). Never `&lt; x &gt;`:
	/// the title's text markup reads `&lt;…&gt;` as a tag and draws a name it does not know as nothing
	/// (Engine_UiTextMarkup: a miss is consumed), which is how the bed's "&lt; Midday &gt;" vanished.
	/// </summary>
	public readonly record struct Row(string Name, string Line, bool Ghosted = false, string Description = "", float? Bar = null,
		(int Filled, int Of)? Pips = null, string? Choice = null);

	/// <summary>A pad button and its label.</summary>
	public readonly record struct Prompt(string Button, string Label);

	public static readonly Fable3ScreenPage Closed = new(string.Empty, System.Array.Empty<Row>(), -1, string.Empty, System.Array.Empty<Prompt>(), false);
}

/// <summary>
/// THE ONE MENU TEMPLATE for Fable 3's box-manager screens: the list is the SHARED
/// <see cref="Engine_FrontEndMenuView"/> (layout, padding, carousel and cursor all live there),
/// its rows the page's lines, its highlight the guest's cursor row and its cursor Fable 3's own
/// list cursor (<see cref="Fable3ListCursor"/>) on every screen, the save menu's four rows
/// included (its own `SaveMenu.bgf` ships in no build); the title, the cursor row's
/// description and the button prompts are drawn over it at the canvas's 720-unit scale, so the
/// screen adapts to any window as every other shared surface does. A screen supplies its page
/// (<see cref="Compose"/>); the pad reaches the guest unchanged, so a screen draws and never decides.
/// </summary>
public abstract partial class Fable3ListScreen : Fable3Canvas
{
	private const float TitleY = 40f;
	private const float DescriptionBottom = 110f;
	private const float PromptsBottom = 56f;
	private const float PromptGap = 36f;
	private const float Padding = 16f;

	private static readonly Color TitleColour = new(1f, 0.86f, 0.55f);
	private static readonly Color PanelShade = new(0f, 0f, 0f, 0.55f);

	// The query box as options.bgf lays it out (canvas units about the screen centre, y up):
	// `QueryBox.Group.QueryBox.Title` y +88, gold 241/201/115 size 18; `.Text` y +3, box 0.55 of
	// the width, size 16; `.Buttons` at (+80, −79) with `A_Button` x −400 (label right of the
	// orb, +20) and `B_Button` x +240 (label left of the orb, −20).
	private const float QueryTitleY = 88f;
	private const float QueryTextY = 3f;
	private const float QueryTextWidth = 0.55f * 1280f;
	private const float QueryButtonsY = -79f;
	private const float QueryLeftButtonX = 80f - 400f;
	private const float QueryRightButtonX = 80f + 240f;
	private static readonly Color QueryTitleColour = new(241f / 255f, 201f / 255f, 115f / 255f);
	private static readonly Color QueryDim = new(0f, 0f, 0f, 0.6f);

	// THE TAB STRIP (the PC layout's Game Settings tabs): under the title, the tab labels centred
	// with the LT / RT keycaps at its ends, the open tab in the title's gold. THE VALUE BARS: a
	// slider beside each bar row on the row's line, its span the panel's (Fable3ListPanel.Slider).
	// THE PIPS: squares from the panel's value column (or past a longer label; Fable3ListPanel.PipLeft),
	// PipSide of the row's height a side, PipGap of a side apart.
	private const float TabsY = 84f;
	private const float TabGap = 40f;
	private const float SliderThickness = 0.28f;
	private const float PipSide = 0.5f;
	private const float PipGap = 0.4f;
	private static readonly Color TabDim = new(0.7f, 0.7f, 0.7f);
	private static readonly Color SliderTrack = new(1f, 1f, 1f, 0.25f);
	private static readonly Color SliderFill = new(241f / 255f, 201f / 255f, 115f / 255f);

	private Engine_FrontEndMenuView _view = null!;
	private readonly Engine_FrontEndMenu _menu = new();
	private Fable3ScreenPage _page = Fable3ScreenPage.Closed;
	private string _shown = string.Empty;
	private string _description = string.Empty;
	private int _selected = -1;
	private bool _open;
	private Fable3ScreenPage.QueryBox? _query;
	private string _querySignature = string.Empty;
	private bool _probed;
	private bool _cursorTried;
	private readonly List<(Rect2 Rect, int Tab)> _tabSpots = new();
	private string? _dragging;
	private string _markSignature = string.Empty;
	private bool _hasMarks;
	private bool _wasSliding;
	private (Vector2 Size, string Description, bool Tabs, bool Prompts, bool Font, Fable3ListPanel? Panel, float? Pitch)? _bandFor;
	private readonly Engine_UiPointerLatch _latch = new();
	private string _rowNames = string.Empty;
	private Fable3ListPanel? _panel;
	private bool _cursorDressed;

	/// <summary>The log tag of the screen, `[tag] …`.</summary>
	protected abstract string LogTag { get; }

	/// <summary>What the log line names the page by, besides its title (Options: its page number).</summary>
	protected virtual string LogContext => string.Empty;

	/// <summary>The page this frame, or null when the screen is not up in the guest.</summary>
	protected abstract Fable3ScreenPage? Compose(double delta);

	/// <summary>
	/// Whether the screen stands in its own .bgf frame (<see cref="Fable3ScreenFrames"/>), whose
	/// description band replaces the template's plain panel behind the description.
	/// </summary>
	protected virtual bool Framed => false;

	/// <summary>The selection bar's arrows for the cursor on 1-based <paramref name="highlight"/> of <paramref name="total"/> rows: the guest's run-time rule unless the screen has its own.</summary>
	protected virtual (bool Top, bool Bottom) Arrows(int highlight, int total) => Fable3ListCursor.Arrows(highlight, total);

	/// <summary>
	/// Whether this page's rows are choices its box centres on its centre line — the selection
	/// bar's line — as 360 `options.bgf` and the PC's author `Options`, `SaveManagement` and
	/// `Multiplayer` (every row text halign 1 at the box's x 0, the bar's `Selection` on it). A
	/// settings page's rows stand left in its box (`GameSettings.Content.SelectionText` at x −332,
	/// halign 0; `bedmenubox` `Sleep.Title` at −330), under the same centred bar. Left by default:
	/// a screen says which of its pages centre.
	/// </summary>
	protected virtual bool RowsOnCentreLine => false;

	/// <summary>
	/// The panel this page's box stands its rows on (<see cref="Fable3ScreenFrames.PanelOf"/>), or null
	/// for the template's own column: the rows' column and alignment, the cursor's centre line, the
	/// marks' column, the description's width and, on a page with no cursor, the rows' place and pitch
	/// all follow it (<see cref="Fable3ListPanel"/>). The screen states it; the template places by it.
	/// </summary>
	protected virtual Fable3ListPanel? Panel => null;

	/// <summary>Whether the screen is up in the guest this frame.</summary>
	public bool IsOpen => _open;

	/// <summary>A tag through the title's strings; text that is not a tag is returned as is.</summary>
	protected static string Resolve(string tag) => Engine_UiGuiText.Resolve(tag, Fable3Strings.Current);

	protected override void Build()
	{
		_view = Engine_FrontEndMenuView.Dressed(Name + "List", null, false, null, Theme);
		_view.GuestCursor = true;
		_view.OneArrangement = true;
		_view.StillRows = true;
		_view.Visible = false;
		_view.Bind(_menu);
		Root.AddChild(_view);

		// A closed screen neither shows nor renders its canvas: the container hides and the
		// viewport is switched off with it (Frame). NOT `WhenParentVisible` — that mode means the
		// parent VIEWPORT (the window) and the renderer draws children before parents, so the
		// canvas never rendered at all: r6/r7 logged `view drew 3` with no row on screen while the
		// title and description, drawn on this container, stood.
		CanvasViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
		Visible = false;
	}

	/// <summary>
	/// The view's rows are glyph meshes built from the theme's fonts when the rows are built, so
	/// a theme arriving after `_Ready` (the strings load on a later frame) must REDRESS the view —
	/// setting `Theme` alone left every label without glyphs (r4).
	/// </summary>
	protected override void ThemeChanged()
	{
		Dress();
		QueueRedraw();
	}

	/// <summary>
	/// Rebuilds the view's rows and cursor: the rows text-only in the theme's fonts, the cursor
	/// the box screens' selection bar on the row's label, once the document it is taken from
	/// stands (the HUD host indexes the banks in the background; until then the view keeps its
	/// stand-in).
	/// </summary>
	private void Dress()
	{
		Engine_UiTheme? book = Fable3Hud.Book(Fable3ListCursor.Document);
		_cursorTried = book is not null;
		Fable3ListCursor.Dressing? cursor = Fable3ListCursor.Build(book, LogTag);
		_view.CursorOnLabel = cursor?.OnLabel;
		_view.CursorLabelSize = Fable3ListCursor.LabelSize;
		_cursorDressed = cursor is not null;
		_view.CursorCanvasX = cursor is null ? null : _panel?.CentreX ?? Fable3ListCursor.CentreLine;
		_view.Redress(Theme, null, cursor?.Template, null);
		_shown = string.Empty;
	}

	protected override void Frame(double delta)
	{
		// Once, when the cursor's document first stands: the cursor's art comes from its book.
		if (!_cursorTried && Fable3Hud.Book(Fable3ListCursor.Document) is not null)
		{
			Dress();
		}

		Fable3ScreenPage? composed = Compose(delta);
		bool open = composed is not null;
		if (open != _open)
		{
			_open = open;
			// Clicks made before the screen stood are not picks on it.
			Engine_UiPointer.EndFrame();
			Fable3PointerSteps.Cancel();
			_latch.Clear();
			Visible = open;
			_view.Visible = open;
			CanvasViewport.RenderTargetUpdateMode = open ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
			_shown = string.Empty;
			QueueRedraw();
			if (!open)
			{
				_menu.Clear();
				_view.Refresh();
				_page = Fable3ScreenPage.Closed;
				_query = null;
				_querySignature = string.Empty;
				_probed = false;
				_dragging = null;
				_markSignature = string.Empty;
				_bandFor = null;
				_panel = null;
			}
		}

		if (composed is not { } page)
		{
			return;
		}

		// The rows stand where this page's box puts them — on its panel when the screen states one,
		// else the template's column — and a change rebuilds them (the signature).
		bool centred = RowsOnCentreLine;
		Fable3ListPanel? panel = Panel;
		_panel = panel;
		_view.LabelCanvasX = panel?.LabelX ?? (centred ? Fable3ListCursor.CentreLine : (float?)null);
		_view.LabelsCentred = panel?.Centred ?? true;
		_view.CursorCanvasX = _cursorDressed ? panel?.CentreX ?? Fable3ListCursor.CentreLine : null;
		(float ViewY, float? Pitch)? placed = Fable3ListPanel.Rows(panel,
			pitch => Engine_FrontEndMenuView.SlotY(Engine_UiExpandableMenu.StillTopSlot, pitch));
		_view.SlotPitch = placed?.Pitch;
		_view.Position = new Vector3(_view.Anchor.X, placed?.ViewY ?? _view.Anchor.Y, _view.Position.Z);

		// The list is rebuilt only when a line changed; the highlight follows every frame.
		var signature = new System.Text.StringBuilder();
		signature.Append(centred ? "centred|" : "left|").Append(panel?.ToString() ?? "no panel").Append('|')
			.Append(placed is null ? "carousel|" : "placed|").Append(page.Title).Append('|');
		foreach (Fable3ScreenPage.Row row in page.Rows)
		{
			signature.Append(row.Name).Append('=').Append(row.Line).Append(row.Ghosted ? "-|" : "|");
		}

		foreach (Fable3ScreenPage.Prompt prompt in page.Prompts)
		{
			signature.Append(prompt.Button).Append(prompt.Label).Append('|');
		}

		bool rebuilt = false;
		if (signature.ToString() != _shown)
		{
			_shown = signature.ToString();
			_menu.Clear();
			foreach (Fable3ScreenPage.Row row in page.Rows)
			{
				_menu.Add(new Engine_UiExpandableMenu.Item(row.Name, row.Name) { StatedLabel = row.Line, Ghosted = row.Ghosted });
			}

			_selected = -1;
			rebuilt = true;
			QueueRedraw();

			// A pick is on a row of THIS set of rows; a changed value alone keeps it.
			var names = new System.Text.StringBuilder();
			foreach (Fable3ScreenPage.Row row in page.Rows)
			{
				names.Append(row.Name).Append('|');
			}

			if (names.ToString() != _rowNames)
			{
				_rowNames = names.ToString();
				_latch.Clear();
			}
		}

		_page = page;
		_view.CursorShown = page.Cursor;
		var marks = new System.Text.StringBuilder();
		marks.Append(page.Tab).Append('|');
		_hasMarks = false;
		foreach (Fable3ScreenPage.Row row in page.Rows)
		{
			if (row.Bar is { } bar)
			{
				marks.Append(row.Name).Append('=').Append(bar.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
				_hasMarks = true;
			}

			if (row.Pips is { } pips)
			{
				marks.Append(row.Name).Append(':').Append(pips.Filled).Append('/').Append(pips.Of).Append('|');
				_hasMarks = true;
			}

			if (row.Choice is { } choice)
			{
				marks.Append(row.Name).Append('<').Append(choice).Append('|');
				_hasMarks = true;
			}
		}

		if (marks.ToString() != _markSignature)
		{
			_markSignature = marks.ToString();
			QueueRedraw();
		}

		// The marks stand on their rows' drawn bounds, so they are redrawn while the rows slide and
		// once more when the slide ends (the rest pose).
		bool sliding = _view.Sliding;
		if (_hasMarks && (sliding || _wasSliding))
		{
			QueueRedraw();
		}

		_wasSliding = sliding;

		Point(page, delta);

		// A query stands over the page: the list steps back while the guest waits for the answer.
		bool listShown = page.Query is null;
		if (_view.Visible != listShown)
		{
			_view.Visible = listShown;
			QueueRedraw();
		}

		// Compared by content: the page is composed afresh every frame.
		string querySignature = page.Query is { } query
			? $"'{query.Title}' '{query.Text}' [{string.Join(" | ", query.Prompts)}]"
			: string.Empty;
		if (querySignature != _querySignature)
		{
			_querySignature = querySignature;
			_query = page.Query;
			QueueRedraw();
			GD.Print(querySignature.Length > 0 ? $"[{LogTag}] query up: {querySignature}" : $"[{LogTag}] query down");
		}

		// The view lays its rows out only in Refresh — the model has no change event, so a list
		// repopulated or re-highlighted under a bound view stays undrawn until the owner says so
		// (the F2 pause list does the same after SetAvailable). r6 bound 3 Options rows and drew 0.
		int selected = page.Selected >= 0 && page.Selected < page.Rows.Count ? page.Selected : -1;
		bool moved = selected != _selected || rebuilt;
		if (moved)
		{
			_selected = selected;
			if (selected >= 0)
			{
				_menu.Highlight(page.Rows[selected].Name);
			}

			string description = selected >= 0 ? page.Rows[selected].Description : page.Description;
			description = description.Length == 0 ? page.Description : description;
			description = description.StartsWith("GUI_", System.StringComparison.Ordinal) ||
				description.StartsWith("TEXT_", System.StringComparison.Ordinal) ? Resolve(description) : description;
			if (description != _description)
			{
				_description = description;
				QueueRedraw();
			}
		}

		// The band is stated before the rows are laid out: the view's poses read it.
		if (Band(page) || moved)
		{
			_view.Refresh();
			(bool above, bool below) = Arrows(selected + 1, page.Rows.Count);
			Fable3ListCursor.ShowArrows(_view, above, below);
		}

		if (rebuilt)
		{
			var lines = new List<string>(page.Rows.Count);
			foreach (Fable3ScreenPage.Row row in page.Rows)
			{
				lines.Add(row.Line);
			}

			// The rows stand still (Engine_UiExpandableMenu.StillTop): a window of the rows the band holds,
			// from the first slot inside it (Engine_UiExpandableMenu.StillSlots), moved only to keep the
			// cursor in it. The window is logged so the count is read against it.
			int first = _view.WindowTop;
			int last = first + _view.StillRowsShown - 1;
			GD.Print($"[{LogTag}] {LogContext}'{page.Title}' row {page.Selected + 1}: {page.Rows.Count} row(s) bound, " +
				$"window rows {first}..{System.Math.Min(last, page.Rows.Count)} from slot {_view.StillTopSlotShown}, view drew {_view.RowsDrawn} " +
				$"(labels without glyphs {_view.LabelsWithoutGlyphs}, chars without a glyph {CharsWithoutGlyph(lines)}, " +
				$"theme {(Theme?.Fonts is null ? "MISSING" : "bound")}, " +
				$"view {(_view.IsVisibleInTree() ? "shown" : "HIDDEN")}): {string.Join(" | ", lines)}");

			// "drew" counts rows made visible, not pixels: r7 and r8 logged `view drew 3` with no
			// row on screen. The probe reads the canvas back once per opening.
			if (!_probed)
			{
				_probed = true;
				Fable3CanvasProbe.Report(LogTag, this, CanvasViewport, CanvasCamera, _view);
			}
		}
	}

	/// <summary>
	/// How many characters of <paramref name="lines"/> the list's font holds no glyph for (each is
	/// drawn as the font's fallback glyph); 0 while the font is not up.
	/// </summary>
	private int CharsWithoutGlyph(IReadOnlyList<string> lines)
	{
		if (Theme?.Fonts?.Get(UiFontBucket.Normal) is not { } font)
		{
			return 0;
		}

		int missing = 0;
		foreach (string line in lines)
		{
			foreach (char c in line)
			{
				if (c != '\n' && c != '\r' && !font.Font.HasGlyph(c))
				{
					++missing;
				}
			}
		}

		return missing;
	}

	/// <summary>
	/// THE LIST'S BAND: the canvas band (Y up, origin the screen centre) the list's rows may stand
	/// in, under what the template draws over the list — the tab strip above, the description panel
	/// (<paramref name="descriptionLines"/> lines of <paramref name="lineHeight"/>) below, or with no
	/// description the button prompts (<paramref name="prompts"/>) below. A row is cut whole, so the
	/// band ends half a row pitch short of each; the highlight slot's row, at canvas
	/// <paramref name="cursorY"/>, is always inside. Null: nothing is drawn over the list. A still list
	/// fits its window to the band (Engine_UiExpandableMenu.StillSlots), so its cursor is never under
	/// the strip, the description or the prompts.
	/// </summary>
	public static Rect2? ListBand(bool tabs, int descriptionLines, float lineHeight, float cursorY, float? pitch = null, bool prompts = false)
	{
		// The carousel's row pitch from the slot table itself (the view's RowPitch is the same two
		// slots; the table is the part the pure tests can load), or the pitch the view's slots stand at.
		float half = (pitch ?? (Engine_UiMenuConstants.Slots[4].Position.Y - Engine_UiMenuConstants.Slots[5].Position.Y)) * 0.5f;
		float top = tabs ? TabsY + lineHeight + 4f + half : 0f;
		float bottom = descriptionLines > 0
			? CanvasHeight - DescriptionBottom - ((descriptionLines * lineHeight) + (2f * Padding)) - half
			: prompts ? CanvasHeight - PromptsBottom - half : CanvasHeight;
		if (top <= 0f && bottom >= CanvasHeight)
		{
			return null;
		}

		// 720-scale y is down from the top, canvas y up from the centre.
		float cursor = (CanvasHeight * 0.5f) - cursorY;
		top = System.Math.Min(top, cursor);
		bottom = System.Math.Max(bottom, cursor);
		return new Rect2(-100000f, (CanvasHeight * 0.5f) - bottom, 200000f, bottom - top);
	}

	/// <summary>The lines the description wraps to in a window of <paramref name="size"/>, as <see cref="_Draw"/> lays it.</summary>
	private int DescriptionLines(UiFont font, Vector2 size, float scale) =>
		Engine_UiCanvasText.MeasureWrappedLines(font, _description, DescriptionWidth(size, scale), scale);

	/// <summary>The description's widest line: the panel's inner width (<see cref="Fable3ListPanel.Description"/>), at most 720 units and the window.</summary>
	private float DescriptionWidth(Vector2 size, float scale) =>
		Mathf.Min(Fable3ListPanel.Description(_panel, 720f).Width * scale, size.X - (2f * Padding * scale));

	/// <summary>
	/// States the list's band (<see cref="ListBand"/>) to the shared view when what it is measured
	/// from changed — the window, the description, the tab strip, the font arriving. True when the
	/// band moved, so the rows are laid out again.
	/// </summary>
	private bool Band(Fable3ScreenPage page)
	{
		Vector2 size = GetViewportRect().Size;
		bool tabs = page.Tabs is { Count: > 0 };
		bool prompts = page.Prompts.Count > 0;
		UiFont? font = Theme?.Fonts?.Get(UiFontBucket.Normal);
		var measured = (size, _description, tabs, prompts, font is not null, _panel, _view.SlotPitch);
		if (_bandFor is { } was && was == measured)
		{
			return false;
		}

		_bandFor = measured;
		Rect2? band = font is null || size.Y <= 0f
			? null
			: ListBand(tabs, _description.Length > 0 ? DescriptionLines(font, size, size.Y / CanvasHeight) : 0, font.Font.LineHeight,
				_view.Position.Y + Engine_UiMenuConstants.Slots[Engine_UiExpandableMenu.HighlightSlot - 1].Position.Y, _view.SlotPitch, prompts);
		if (band == _view.ClipRect)
		{
			return false;
		}

		_view.ClipRect = band;
		return true;
	}

	/// <summary>
	/// THE MOUSE ON THE LIST: the pointer resolved through this canvas's camera (the open screen's),
	/// a click picking and accepting the row under it, the wheel stepping the cursor. Where the guest
	/// owns the cursor the pick is walked (<see cref="PointAt"/>).
	/// </summary>
	/// <remarks>
	/// A MOVING POINTER ON A ROW MAKES IT THE CURSOR ROW (<see cref="Hovers"/>), as the PC build's hover
	/// does: its cursor handler posts the row under the cursor to the screen as action 29, "the cursor row
	/// is this one" (PC sub_B9FBD0: `push 1Dh` / `call sub_B9D0C0` 0xBA0322..0xBA032B, handed to the GUI's
	/// receiver 0xBA0333..0xBA0335). The rows stand still (the view's StillRows,
	/// <see cref="Engine_UiExpandableMenu.StillTop"/>): a click, a hover or a cursor move inside the window
	/// moves no row, so a hovered row stays under the pointer, and the window moves one row at a time
	/// only when the cursor leaves it. A resting pointer picks nothing (only movement re-picks,
	/// <see cref="Engine_UiPointer.Moved"/>), and neither does a pointer while the pad is the device
	/// (<see cref="Engine_UiPointer.Canvas"/> null). A CLICK'S ROW STAYS THE POINTER'S
	/// (<see cref="Engine_UiPointerLatch"/>): the wheel and the next click of a still pointer act on the
	/// row it picked, should the window have moved under it.
	/// </remarks>
	private void Point(Fable3ScreenPage page, double delta)
	{
		Engine_UiPointer.Resolve(CanvasCamera, CanvasViewport, GetViewportRect().Size);

		// A drag holds its bar while the left button is down, wherever the pointer goes.
		if (_dragging is { } dragged)
		{
			if (page.Query is not null || !Input.IsMouseButtonPressed(MouseButton.Left) || RowIndex(page, dragged) is not int held)
			{
				_dragging = null;
			}
			else if (Engine_UiPointer.Canvas is { } pointer)
			{
				PointBar(held, SliderAt(pointer.X));
			}
		}

		if (page.Query is null && _view.Visible && Engine_UiPointer.Free && Engine_UiInputMode.KeyboardMouse &&
			Engine_UiPointer.LeftPressed)
		{
			Vector2 local = Engine_UiPointer.LocalTo(this);
			foreach ((Rect2 rect, int tab) in _tabSpots)
			{
				if (rect.HasPoint(local))
				{
					Engine_UiPointer.TakeLeft();
					_latch.Clear();
					PointTab(tab);
					break;
				}
			}
		}

		// The pointer's pick stands only while the list is what it acts on.
		if (page.Query is not null || !_view.Visible || Engine_UiPointer.Canvas is null)
		{
			_latch.Clear();
		}

		if (page.Query is null && _view.Visible && Engine_UiPointer.Canvas is { } on && Engine_UiPointer.LeftPressed)
		{
			// A still pointer's press belongs to the row it picked (the latch): its slider when it
			// has one, else its click, below.
			int picked = _latch.Row(-1, on.X, on.Y, page.Selected);
			for (int i = 0; i < page.Rows.Count; ++i)
			{
				if (page.Rows[i].Bar is not null && SliderRect(page.Rows[i].Name) is { } slider && slider.HasPoint(on))
				{
					int target = picked >= 0 && picked < page.Rows.Count ? picked : i;
					if (page.Rows[target].Bar is null)
					{
						break;
					}

					Engine_UiPointer.TakeLeft();
					_dragging = page.Rows[target].Name;
					PointBar(target, SliderAt(on.X));
					_latch.Pick(target, on.X, on.Y);
					break;
				}
			}
		}

		if (page.Query is null && _view.Visible && Engine_UiPointer.Canvas is { } at)
		{
			int row = -1;
			if (_view.EntryAt(at) is { } item)
			{
				for (int i = 0; i < page.Rows.Count; ++i)
				{
					if (page.Rows[i].Name == item.Name)
					{
						row = i;
						break;
					}
				}
			}

			int pointed = row;

			// The row a still pointer picked stays its row while the list slides under it.
			row = _latch.Row(row, at.X, at.Y, page.Selected);
			if (row >= page.Rows.Count)
			{
				_latch.Clear();
				row = -1;
			}

			// A click is the row's only when it lands on one: elsewhere it stays for the prompts.
			if (row >= 0 && Engine_UiPointer.TakeLeft())
			{
				PointAt(row, true);
				_latch.Pick(row, at.X, at.Y);
			}
			else if (Hovers(pointed, page.Selected, page.Rows.Count, Engine_UiPointer.Moved))
			{
				PointAt(pointed, false);
			}

			int wheel = Engine_UiPointer.TakeWheel();
			if (wheel != 0 && row >= 0 && PointWheel(wheel, row))
			{
				wheel = 0;
			}

			if (wheel != 0 && page.Rows.Count > 0)
			{
				int from = page.Selected >= 0 ? page.Selected : 0;
				PointAt(System.Math.Clamp(from + wheel, 0, page.Rows.Count - 1), false);
			}
		}

		Fable3PointerSteps.Tick(page.Selected, delta);
	}

	/// <summary>
	/// Whether the pointer on row <paramref name="pointed"/> (-1 for none) of <paramref name="count"/> makes it
	/// the cursor row: it moved this frame and the row is not the cursor's already. The screen walks or
	/// states its cursor there (<see cref="PointAt"/> with no click).
	/// </summary>
	public static bool Hovers(int pointed, int selected, int count, bool moved) =>
		moved && pointed >= 0 && pointed < count && pointed != selected;

	/// <summary>The index of the row named <paramref name="name"/> on <paramref name="page"/>, or null.</summary>
	private static int? RowIndex(Fable3ScreenPage page, string name)
	{
		for (int i = 0; i < page.Rows.Count; ++i)
		{
			if (page.Rows[i].Name == name)
			{
				return i;
			}
		}

		return null;
	}

	/// <summary>A bar row's slider on the canvas: the panel's bar span (<see cref="Fable3ListPanel.Slider"/>) on the row's drawn line, or null while the row is not drawn.</summary>
	private Rect2? SliderRect(string name)
	{
		if (_view.BoundsOf(name) is not { } bounds)
		{
			return null;
		}

		(float left, float right) = Fable3ListPanel.Slider(_panel);
		return new Rect2(left, bounds.Position.Y, right - left, bounds.Size.Y);
	}

	/// <summary>The 0..1 a canvas X stands for on this page's sliders, clamped to their ends.</summary>
	private float SliderAt(float canvasX)
	{
		(float left, float right) = Fable3ListPanel.Slider(_panel);
		return Fable3ListPanel.Fraction(canvasX, left, right);
	}

	/// <summary>The 0..1 a canvas X stands for on a slider of a list with no panel (the Options settings page), clamped to its ends.</summary>
	public static float SliderFraction(float canvasX) =>
		Fable3ListPanel.Fraction(canvasX, Fable3ListPanel.DefaultSliderLeft, Fable3ListPanel.DefaultSliderRight);

	/// <summary>The pointer pressed or dragged bar row <paramref name="row"/> to <paramref name="fraction"/> (0..1).</summary>
	protected virtual void PointBar(int row, float fraction)
	{
	}

	/// <summary>The pointer clicked tab <paramref name="tab"/> of the strip.</summary>
	protected virtual void PointTab(int tab)
	{
	}

	/// <summary>
	/// The wheel's <paramref name="steps"/> over <paramref name="row"/>: true when the screen took them
	/// itself (a settings page stepping its cursor row's value), false to step the cursor as every
	/// list does.
	/// </summary>
	protected virtual bool PointWheel(int steps, int row) => false;

	/// <summary>The pointer picked <paramref name="row"/>; <paramref name="clicked"/> when it was a click. The guest's screens walk their own cursor to it.</summary>
	protected virtual void PointAt(int row, bool clicked) => Fable3PointerSteps.Toward(row, clicked);

	public override void _Draw()
	{
		Fable3Prompts.Begin(this);
		if (!_open || Theme?.Fonts is null || Theme.Fonts.Get(UiFontBucket.Normal) is not { } font)
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / CanvasHeight;

		if (_page.Title.Length > 0)
		{
			// Over the panel's centre line (the screen's centre with no panel).
			float width = Engine_UiCanvasText.Measure(font, _page.Title) * scale * 1.25f;
			float centre = (size.X * 0.5f) + ((_panel?.CentreX ?? 0f) * scale);
			Engine_UiCanvasText.DrawLine(this, font, _page.Title, new Vector2(centre - (width * 0.5f), TitleY * scale), scale * 1.25f, TitleColour);
		}

		_tabSpots.Clear();
		if (_query is { } query)
		{
			DrawQuery(query, font, size, scale);
			return;
		}

		if (_page.Tabs is { Count: > 0 } tabs)
		{
			DrawTabs(tabs, _page.Tab, font, size, scale);
		}

		DrawMarks(font, size);

		if (_description.Length > 0)
		{
			float inner = DescriptionWidth(size, scale);
			int lines = DescriptionLines(font, size, scale);
			float height = (lines * font.Font.LineHeight * scale) + (2f * Padding * scale);
			// Centred on the panel's centre line (the canvas is centred on the window, at its height's scale).
			float centre = (size.X * 0.5f) + (Fable3ListPanel.Description(_panel, 720f).CentreX * scale);
			var rect = new Rect2(new Vector2(centre - (inner * 0.5f) - (Padding * scale), size.Y - (DescriptionBottom * scale) - height), new Vector2(inner + (2f * Padding * scale), height));
			if (!Framed)
			{
				DrawRect(rect, PanelShade);
			}

			Engine_UiCanvasText.DrawWrapped(this, font, _description, rect.Position + new Vector2(Padding * scale, Padding * scale), inner, scale, Colors.White);
		}

		if (_page.Prompts.Count > 0)
		{
			float total = 0f;
			foreach (Fable3ScreenPage.Prompt prompt in _page.Prompts)
			{
				total += Fable3Prompts.Measure(font, prompt.Label, scale) + (PromptGap * scale);
			}

			var pen = new Vector2((size.X - total + (PromptGap * scale)) * 0.5f, size.Y - (PromptsBottom * scale));
			foreach (Fable3ScreenPage.Prompt prompt in _page.Prompts)
			{
				pen.X += Fable3Prompts.Draw(this, font, prompt.Button, prompt.Label, pen, scale, Colors.White) + (PromptGap * scale);
			}
		}
	}

	/// <summary>
	/// The tab strip: the labels in a row centred under the title, LT before and RT after (the PC
	/// layout's TabIcons), the open tab gold. Each label is its tab for the pointer.
	/// </summary>
	private void DrawTabs(IReadOnlyList<string> tabs, int open, UiFont font, Vector2 size, float scale)
	{
		float gap = TabGap * scale;
		float total = Fable3Prompts.Measure(font, string.Empty, scale) * 2f + gap * 2f;
		foreach (string tab in tabs)
		{
			total += Engine_UiCanvasText.Measure(font, tab) * scale + gap;
		}

		var pen = new Vector2((size.X - total + gap) * 0.5f, TabsY * scale);
		pen.X += Fable3Prompts.Draw(this, font, "LT", string.Empty, pen, scale, Colors.White) + gap;
		float line = font.Font.LineHeight * scale;
		for (int i = 0; i < tabs.Count; ++i)
		{
			float width = Engine_UiCanvasText.Measure(font, tabs[i]) * scale;
			Engine_UiCanvasText.DrawLine(this, font, tabs[i], pen, scale, i == open ? TitleColour : TabDim);
			if (i == open)
			{
				DrawRect(new Rect2(pen + new Vector2(0f, line + 2f * scale), new Vector2(width, 2f * scale)), TitleColour);
			}

			_tabSpots.Add((new Rect2(pen - new Vector2(gap * 0.25f, 0f), new Vector2(width + gap * 0.5f, line)), i));
			pen.X += width + gap;
		}

		Fable3Prompts.Draw(this, font, "RT", string.Empty, pen, scale, Colors.White);
	}

	/// <summary>
	/// Each drawn row's mark: a bar's slider (the track, the fill to its value, the percentage after
	/// it), a pip row's squares (the filled ones in the fill's colour, the rest in the track's).
	/// </summary>
	private void DrawMarks(UiFont font, Vector2 size)
	{
		if (Engine_UiPointer.VisibleCanvas is not { } visible || visible.Size.X <= 0f || visible.Size.Y <= 0f)
		{
			return;
		}

		Vector2 ToScreen(Vector2 canvas) => new(
			(canvas.X - visible.Position.X) / visible.Size.X * size.X,
			(visible.Position.Y + visible.Size.Y - canvas.Y) / visible.Size.Y * size.Y);
		float scale = size.Y / visible.Size.Y;
		foreach (Fable3ScreenPage.Row row in _page.Rows)
		{
			if (row.Pips is { } pips && _view.BoundsOf(row.Name) is { } line)
			{
				float side = line.Size.Y * PipSide;
				float width = (pips.Of * side) + (System.Math.Max(pips.Of - 1, 0) * side * PipGap);
				float left = Fable3ListPanel.PipLeft(_panel, line.Position.X, line.End.X, side, width);
				float low = line.Position.Y + ((line.Size.Y - side) * 0.5f);
				for (int i = 0; i < pips.Of; ++i)
				{
					float x = left + (i * side * (1f + PipGap));
					Vector2 corner = ToScreen(new Vector2(x, low + side));
					DrawRect(new Rect2(corner, ToScreen(new Vector2(x + side, low)) - corner), i < pips.Filled ? SliderFill : SliderTrack);
				}
			}

			if (row.Choice is { } choice && _view.BoundsOf(row.Name) is { } chosen)
			{
				DrawChoiceArrows(font, row, choice, chosen, ToScreen);
			}

			if (row.Bar is not { } bar || SliderRect(row.Name) is not { } slider)
			{
				continue;
			}

			float middle = slider.Position.Y + slider.Size.Y * 0.5f;
			float half = slider.Size.Y * SliderThickness * 0.5f;
			Vector2 topLeft = ToScreen(new Vector2(slider.Position.X, middle + half));
			Vector2 bottomRight = ToScreen(new Vector2(slider.End.X, middle - half));
			var track = new Rect2(topLeft, bottomRight - topLeft);
			DrawRect(track, SliderTrack);
			DrawRect(new Rect2(track.Position, new Vector2(track.Size.X * System.Math.Clamp(bar, 0f, 1f), track.Size.Y)), SliderFill);
			string percent = (int)System.MathF.Round(System.Math.Clamp(bar, 0f, 1f) * 100f) + "%";
			float lineHeight = font.Font.LineHeight * scale;
			Engine_UiCanvasText.DrawLine(this, font, percent,
				new Vector2(track.End.X + 12f * scale, track.Position.Y + track.Size.Y * 0.5f - lineHeight * 0.5f), scale, Colors.White);
		}
	}

	/// <summary>
	/// A choice row's two arrows (<see cref="Fable3ListPanel.ChoiceArrows"/>): filled triangles, pointing
	/// out, either side of the choice that ends the row's drawn <paramref name="line"/>, a pip's side
	/// high, in the label's colour (dimmed with a ghosted row).
	/// </summary>
	private void DrawChoiceArrows(UiFont font, Fable3ScreenPage.Row row, string choice, Rect2 line, System.Func<Vector2, Vector2> toScreen)
	{
		float side = line.Size.Y * PipSide;
		(float leftEnd, float rightStart) = Fable3ListPanel.ChoiceArrows(line.Position.X, line.End.X,
			Engine_UiCanvasText.Measure(font, row.Line), Engine_UiCanvasText.Measure(font, choice), side);
		float middle = line.Position.Y + (line.Size.Y * 0.5f);
		float half = side * 0.5f;
		float reach = side * 0.6f;
		Color colour = row.Ghosted ? TabDim : Colors.White;
		DrawColoredPolygon(new[]
		{
			toScreen(new Vector2(leftEnd, middle + half)), toScreen(new Vector2(leftEnd, middle - half)),
			toScreen(new Vector2(leftEnd - reach, middle)),
		}, colour);
		DrawColoredPolygon(new[]
		{
			toScreen(new Vector2(rightStart, middle + half)), toScreen(new Vector2(rightStart + reach, middle)),
			toScreen(new Vector2(rightStart, middle - half)),
		}, colour);
	}

	/// <summary>The query over a dimmed page, placed where options.bgf authors its parts.</summary>
	private void DrawQuery(Fable3ScreenPage.QueryBox query, UiFont font, Vector2 size, float scale)
	{
		DrawRect(new Rect2(Vector2.Zero, size), QueryDim);
		Vector2 centre = size * 0.5f;
		float lineHeight = font.Font.LineHeight * scale;

		if (query.Title.Length > 0)
		{
			float titleScale = scale * (18f / 16f);
			float width = Engine_UiCanvasText.Measure(font, query.Title) * titleScale;
			Engine_UiCanvasText.DrawLine(this, font, query.Title,
				new Vector2(centre.X - (width * 0.5f), centre.Y - (QueryTitleY * scale) - (lineHeight * 0.5f)), titleScale, QueryTitleColour);
		}

		float inner = Mathf.Min(QueryTextWidth * scale, size.X - (2f * Padding * scale));
		int lines = Engine_UiCanvasText.MeasureWrappedLines(font, query.Text, inner, scale);
		float height = lines * lineHeight;
		var panel = new Rect2(
			new Vector2(centre.X - (inner * 0.5f) - (Padding * scale), centre.Y - (QueryTextY * scale) - (height * 0.5f) - (Padding * scale)),
			new Vector2(inner + (2f * Padding * scale), height + (2f * Padding * scale)));
		DrawRect(panel, PanelShade);
		Engine_UiCanvasText.DrawWrapped(this, font, query.Text, panel.Position + new Vector2(Padding * scale, Padding * scale), inner, scale, Colors.White);

		// The first prompt stands on the left button's place, the second on the right's (its
		// label ends at the orb, as the B button's `-20` label offset authors it).
		float y = centre.Y - (QueryButtonsY * scale) - (lineHeight * 0.5f);
		for (int i = 0; i < query.Prompts.Count && i < 2; ++i)
		{
			Fable3ScreenPage.Prompt prompt = query.Prompts[i];
			float x = centre.X + ((i == 0 ? QueryLeftButtonX : QueryRightButtonX) * scale);
			if (i == 1)
			{
				x -= Fable3Prompts.Measure(font, prompt.Label, scale);
			}

			Fable3Prompts.Draw(this, font, prompt.Button, prompt.Label, new Vector2(x, y), scale, Colors.White);
		}
	}
}
