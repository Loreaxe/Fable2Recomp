namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// FABLE 3'S LIST CURSOR: the selection bar of the box screens, for every Fable 3 list screen
/// (<see cref="Fable3ListScreen"/>).
/// </summary>
/// <remarks>
/// <para>WHAT THE ORIGINAL DRAWS. Every box with rows marks its cursor row with one group,
/// `Selection`, standing on the row text's origin (`options.bgf`
/// `Options.Sections.Options.Background.Selection` at (0, 25) with its row text `GameSettings` at
/// (0, 25); `bedmenubox.bgf` `BedMenu.Sleep.Selection`; eighteen of the 360's documents hold it),
/// four quads, all at file z 0, drawn in element order:
/// <list type="bullet">
/// <item>`SelectionBar_ArrowTop`: (0, +20), scale 1.28 × 0.32;</item>
/// <item>`SelectionBar_ArrowBottom`: (0, −20), scale 1.28 × 0.32;</item>
/// <item>`SelectionBar_Background`: (0, 0), scale 10.24 × 0.64, opacity 50;</item>
/// <item>`SelectionBar_Lines`: (0, 0), scale 10.24 × 0.64, opacity 100.</item>
/// </list>
/// The row texts are 16 point. The bar spans the box about its centre line, not the string. The
/// arrows' opacity is the box's to say (<see cref="Arrows"/>, <see cref="OptionsArrows"/>). The PC build's documents place the same four pieces the same way; the interaction
/// list is Godot's own drawing (<see cref="Fable3InteractionList"/>) and wears no glow.</para>
/// <para>WHERE THE ART COMES FROM. The bed menu's document (`bedmenubox.bgf`
/// `BedMenu.Sleep.Selection`), one of the box frames <see cref="Fable3Hud"/> already stands
/// (<see cref="Fable3ScreenFrames"/>), so no document is built for a cursor. Its four quads name
/// the bar's own art in their `.fac` records,
/// `art\gui_memmount\hudassests\version4\middleelements\options\selectionbar_{background,lines,arrowtop,arrowbottom}.tex`,
/// as 16 other 360 documents do for their bars (options, querybox, messageboxmulti, partners,
/// quest summary, the stats categories, treasury …) and the PC's options and bed menu. NOT the
/// interaction document: its `RulerInfoBox.Background.Selection` holds the same four transforms but
/// its `.fac` names `art\gui\frames\frames_04.tex` (hash 0xA1DC859D) with the whole-texture rect
/// (1, 1, 0, 0) for all four, and that atlas drawn whole is the magenta bands and white ellipses
/// int3 saw (`promises`' message boxes and `statsscreen`'s `GlobalStatistics` rows name it too).
/// No Fable 3 quad is detail-mapped (every `.fac` record of the 33 360 documents has mode 0), so
/// no detail map is involved.</para>
/// <para>WHERE IT STANDS, AGAINST THE ROW. In every box document the bar's `Selection` stands
/// on its row's line (valign 1 texts, the line through their middle) and on the box's centre line,
/// x 0, its four pieces 1024 × 64 canvas units about that point (scale 10.24 × 0.64 under parents
/// of scale 1) for 16-point rows: the bar is the box's width and the row's height. A choice list
/// centres its row texts on the bar (halign 1 at x 0): 360 `options.bgf` `Selection` (0, 25) on
/// `SelectionTitle.GameSettings` (0, 25), the PC's the same, `SaveManagement` and `Multiplayer`
/// alike. A settings list stands its texts left in the box under the same bar
/// (`GameSettings.Content.SelectionText` at x −332, halign 0; `bedmenubox` `Sleep.Title` at
/// (−330, −2) with its `Selection` at (0, 0)). So the shared view stands the bar on the
/// highlighted row's line and the box's centre line (<see cref="Engine_FrontEndMenuView.CursorCanvasX"/>),
/// keeps its authored 1024 width and grows only its height by the row's point size over 16, and
/// centres a choice list's rows on the same line (<see cref="Engine_FrontEndMenuView.LabelCanvasX"/>,
/// <see cref="RowsCentred"/>). The file's z tie is resolved as its element order draws it:
/// background, lines, arrows, each a unit nearer, all behind the label. It does not animate: no
/// track or slide of the documents moves the group; the list moves under it.</para>
/// </remarks>
public static class Fable3ListCursor
{
	/// <summary>The document the art is taken from: the bed menu's box, whose `Selection` names the bar's own art.</summary>
	public const string Document = "bedmenubox";

	public const string Background = "SelectionBar_Background";
	public const string Lines = "SelectionBar_Lines";
	public const string ArrowTop = "SelectionBar_ArrowTop";
	public const string ArrowBottom = "SelectionBar_ArrowBottom";

	/// <summary>The cursor's pieces, back to front.</summary>
	public static readonly string[] Pieces = { Background, Lines, ArrowTop, ArrowBottom };

	/// <summary>The point size of the row texts the bar is authored on.</summary>
	public const float LabelSize = 16f;

	/// <summary>The canvas x the bar is centred on: its box's centre line.</summary>
	public const float CentreLine = 0f;

	/// <summary>Canvas units the backmost piece stands behind the label; each later piece one nearer.</summary>
	public const float Depth = 4f;

	/// <summary>The cursor for one view: its template and that template's pose against the row label's origin.</summary>
	public readonly record struct Dressing(Node3D Template, Transform3D OnLabel);

	/// <summary>
	/// The cursor from <paramref name="book"/> (<see cref="Document"/>'s pattern book), or null
	/// when the book is not up or does not hold the bar. The template is the caller's view's to free.
	/// </summary>
	public static Dressing? Build(Engine_UiTheme? book, string logTag)
	{
		if (book is null || Group(book) is not { } group)
		{
			return null;
		}

		var template = new Node3D { Name = "ListCursor" };
		for (int order = 0; order < Pieces.Length; ++order)
		{
			string piece = Pieces[order];
			if (In(book, piece, group) is not { } variant)
			{
				continue;
			}

			Node3D source = book.ArtSourceNodes(piece)[variant];
			if (book.Art(piece, variant, keepAuthoredBasis: true) is not MeshInstance3D clone)
			{
				continue;
			}

			// The built quad carries the opacity of everything above it; the piece's own is what
			// its box shows it at. An arrow's is the list's to say (Arrows): it starts hidden.
			bool arrow = piece is ArrowTop or ArrowBottom;
			float own = arrow ? 1f : source.HasMeta("opacity") ? source.GetMeta("opacity").AsSingle() : 1f;
			Engine_UiSceneBuilder.SetQuadOpacity(clone, own);
			clone.Name = piece;
			clone.Transform = OnLabel(source.Transform, order);
			clone.Visible = !arrow;
			template.AddChild(clone);
		}

		if (template.GetChildCount() == 0)
		{
			template.Free();
			return null;
		}

		GD.Print($"[{logTag}] list cursor: {template.GetChildCount()} piece(s) of '{Document}' " +
			$"{group.GetParent()?.GetParent()?.Name}.{group.GetParent()?.Name}.{group.Name}, art {Art(template)}");
		return new Dressing(template, Transform3D.Identity);
	}

	/// <summary>
	/// A piece's transform against the label's origin from its transform in `Selection` (which
	/// stands on the row text's origin): its authored place and basis, at the depth its place in
	/// the element order gives it behind the label.
	/// </summary>
	public static Transform3D OnLabel(Transform3D inSelection, int order) =>
		new(inSelection.Basis, new Vector3(inSelection.Origin.X, inSelection.Origin.Y, order - Depth));

	/// <summary>
	/// Which arrows a list shows with its cursor on the 1-based row <paramref name="highlight"/>
	/// of <paramref name="total"/>, AS THE GUEST'S ONE RUN-TIME WRITER HAS IT: the partners box
	/// (TU3 sub_82B92A38, the disc's sub_82B5DDE0 the same body) shows `SelectionBar_ArrowTop`
	/// while the cursor's index (+56 cursor plus +60 scroll) is above 0 and `SelectionBar_ArrowBottom`
	/// while it is below the last row (`count - 1`, the 64-byte rows of +36..+40), through
	/// sub_82877898(path, presentation, shown). So: a row above the cursor, a row below it.
	/// </summary>
	public static (bool Top, bool Bottom) Arrows(int highlight, int total) =>
		highlight < 1 || total < 1 ? (false, false) : (highlight > 1, highlight < total);

	/// <summary>
	/// The Options screen's arrows, which no code writes: each section of `options.bgf` authors
	/// its own pair (opacity 100 shown, 0 hidden) and they stand as authored whatever the cursor
	/// does — Options (page 1) neither, Save Management (2) and Multiplayer (5) the bottom one,
	/// Game Settings (3) and its front-end form (4) both.
	/// </summary>
	public static (bool Top, bool Bottom) OptionsArrows(int page) => page switch
	{
		2 or 5 => (false, true),
		3 or 4 => (true, true),
		_ => (false, false),
	};

	/// <summary>
	/// Whether an Options page's rows are centred, as `options.bgf` authors them on both 360 builds
	/// and the PC: `Options`, `SaveManagement` and `Multiplayer`'s `SelectionTitle` / `SelectionText`
	/// rows are halign 1 at x 0 (pages 1, 2, 5); `GameSettings.Content.SelectionText` stands at x −332
	/// with halign 0 rows (pages 3 and 4, its front-end form).
	/// </summary>
	public static bool RowsCentred(int optionsPage) => optionsPage is 1 or 2 or 5;

	/// <summary>Shows or hides the arrows of the cursor <paramref name="view"/> built from the template.</summary>
	public static void ShowArrows(Engine_FrontEndMenuView view, bool top, bool bottom)
	{
		if (view.GetNodeOrNull<Node3D>("Cursor/" + ArrowTop) is { } up && up.Visible != top)
		{
			up.Visible = top;
		}

		if (view.GetNodeOrNull<Node3D>("Cursor/" + ArrowBottom) is { } down && down.Visible != bottom)
		{
			down.Visible = bottom;
		}
	}

	/// <summary>Each piece's texture file name, back to front, for the build's log line: the run proves which art stands.</summary>
	private static string Art(Node3D template)
	{
		var names = new System.Collections.Generic.List<string>(template.GetChildCount());
		foreach (Node child in template.GetChildren())
		{
			string path = child.HasMeta("texture_path") ? child.GetMeta("texture_path").AsString() : "none";
			names.Add(path[(path.LastIndexOf('\\') + 1)..]);
		}

		return string.Join(" / ", names);
	}

	/// <summary>The `Selection` group that holds the whole bar: the parent of the first built background quad.</summary>
	private static Node3D? Group(Engine_UiTheme book)
	{
		foreach (Node3D bearer in book.ArtSourceNodes(Background))
		{
			if (bearer is MeshInstance3D && bearer.GetParent() is Node3D group)
			{
				return group;
			}
		}

		return null;
	}

	/// <summary>The index of the bearer of <paramref name="name"/> that is a built quad directly under <paramref name="group"/>, or null.</summary>
	private static int? In(Engine_UiTheme book, string name, Node3D group)
	{
		System.Collections.Generic.IReadOnlyList<Node3D> bearers = book.ArtSourceNodes(name);
		for (int i = 0; i < bearers.Count; ++i)
		{
			if (bearers[i] is MeshInstance3D && bearers[i].GetParent() == group)
			{
				return i;
			}
		}

		return null;
	}
}
