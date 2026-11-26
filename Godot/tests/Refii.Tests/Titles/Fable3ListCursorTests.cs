namespace Refii.Tests.Titles;

using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// Fable 3's list cursor: where the box screens' selection bar stands against a row's label.
/// Pure arithmetic on Godot's value types; no Godot object is built.
/// </summary>
[TestSuite]
public class Fable3ListCursorTests
{
	/// <summary>
	/// options.bgf / interactionmode.bgf `Selection`: `SelectionBar_Background` and `_Lines` at
	/// (0, 0) scale 10.24 × 0.64, `SelectionBar_ArrowTop` at (0, +20) and `_ArrowBottom` at
	/// (0, −20) scale 1.28 × 0.32, all at file z 0. Each keeps its place and size; the tie in z
	/// becomes the element order, every piece behind the label.
	/// </summary>
	[TestCase]
	public void ThePiecesKeepTheirAuthoredPlacesAndStandBehindTheLabelInOrder()
	{
		var bar = new Transform3D(Basis.Identity.Scaled(new Vector3(10.24f, 0.64f, 1f)), Vector3.Zero);
		var top = new Transform3D(Basis.Identity.Scaled(new Vector3(1.28f, 0.32f, 1f)), new Vector3(0f, 20f, 0f));
		Transform3D background = Fable3ListCursor.OnLabel(bar, System.Array.IndexOf(Fable3ListCursor.Pieces, Fable3ListCursor.Background));
		Transform3D lines = Fable3ListCursor.OnLabel(bar, System.Array.IndexOf(Fable3ListCursor.Pieces, Fable3ListCursor.Lines));
		Transform3D arrow = Fable3ListCursor.OnLabel(top, System.Array.IndexOf(Fable3ListCursor.Pieces, Fable3ListCursor.ArrowTop));
		AssertThat(background.Basis.Scale.X).IsEqualApprox(10.24f, 0.001f);
		AssertThat(background.Basis.Scale.Y).IsEqualApprox(0.64f, 0.001f);
		AssertThat(arrow.Origin.Y).IsEqualApprox(20f, 0.001f);
		AssertThat(background.Origin.Z < lines.Origin.Z).IsTrue();
		AssertThat(lines.Origin.Z < arrow.Origin.Z).IsTrue();
		AssertThat(arrow.Origin.Z < 0f).IsTrue();
	}

	/// <summary>
	/// On the view's highlight slot (56, −45) with scale 1.025 and the label at x −30, an 18-point
	/// row keeps the bar on the row's height and grows its HEIGHT by 1.125 and the row's scale; its
	/// width stays the authored 1024 units (the box's, as every box document has it), and it is
	/// centred on the canvas's centre line whatever the row's x, for a view anchored at −345.
	/// </summary>
	[TestCase]
	public void TheBarTakesTheRowsHeightAndTheBoxsWidthOnTheCanvasCentreLine()
	{
		var onLabel = new Transform3D(Basis.Identity.Scaled(new Vector3(10.24f, 0.64f, 1f)), new Vector3(0f, 0f, -4f));
		Transform3D onRow = Engine_FrontEndMenuView.CursorOnRow(56f, -45f, 1.025f, -30f, 0f, 18f / 16f, onLabel);
		Transform3D placed = Engine_FrontEndMenuView.CursorAtCanvasX(onRow, onLabel, Fable3ListCursor.CentreLine, -345f);
		AssertThat(placed.Origin.X).IsEqualApprox(345f, 0.001f);
		AssertThat(placed.Origin.Y).IsEqualApprox(-45f, 0.001f);
		AssertThat(placed.Origin.Z).IsEqualApprox(-4f, 0.001f);
		AssertThat(placed.Basis.Scale.X).IsEqualApprox(10.24f, 0.001f);
		AssertThat(placed.Basis.Scale.Y).IsEqualApprox(0.64f * 1.125f * 1.025f, 0.001f);
	}

	/// <summary>
	/// The template as Fable3ListCursor builds it: an identity root over the authored pieces, so the
	/// root's x scale is exactly 1 and the background's own 10.24 makes 1024 canvas units.
	/// </summary>
	[TestCase]
	public void TheTemplatesRootKeepsUnitWidth()
	{
		Transform3D onRow = Engine_FrontEndMenuView.CursorOnRow(56f, -45f, 1.025f, -30f, 0f, 18f / 16f, Transform3D.Identity);
		Transform3D placed = Engine_FrontEndMenuView.CursorAtCanvasX(onRow, Transform3D.Identity, 0f, -345f);
		AssertThat(placed.Basis.Scale.X).IsEqualApprox(1f, 0.0001f);
		AssertThat(placed.Basis.Scale.Y).IsEqualApprox(1.125f * 1.025f, 0.0001f);
	}

	/// <summary>A row at another x (a child row, a bowed slot) leaves the bar where it was.</summary>
	[TestCase]
	public void TheBarDoesNotFollowTheRowsIndent()
	{
		var onLabel = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -4f));
		Transform3D a = Engine_FrontEndMenuView.CursorAtCanvasX(
			Engine_FrontEndMenuView.CursorOnRow(56f, -45f, 1f, -30f, 0f, 1f, onLabel), onLabel, 0f, -345f);
		Transform3D b = Engine_FrontEndMenuView.CursorAtCanvasX(
			Engine_FrontEndMenuView.CursorOnRow(22f, -45f, 1f, 10f, 0f, 1f, onLabel), onLabel, 0f, -345f);
		AssertThat(a.Origin.X).IsEqualApprox(b.Origin.X, 0.001f);
	}

	/// <summary>
	/// A centred row's label stands on the bar's line: in a row resting at x 56 with scale 1.025,
	/// for a view anchored at −345, the label's x is (0 + 345 − 56) / 1.025, so the row's origin
	/// plus the scaled label lands on canvas x 0 — the bar's centre — on every slot of the bow.
	/// </summary>
	[TestCase]
	public void ACentredLabelStandsOnTheBarsLine()
	{
		foreach ((float rowX, float rowScale) in new[] { (56f, 1.025f), (42f, 0.85f + 0.025f), (22f, 0.775f), (0f, 1f) })
		{
			float labelX = Engine_FrontEndMenuView.LabelAtCanvasX(Fable3ListCursor.CentreLine, -345f, rowX, rowScale);
			AssertThat(-345f + rowX + rowScale * labelX).IsEqualApprox(Fable3ListCursor.CentreLine, 0.001f);
		}

		AssertThat(Engine_FrontEndMenuView.LabelAtCanvasX(0f, -345f, 56f, 0f)).IsEqualApprox(289f, 0.001f);
	}

	/// <summary>
	/// The bar's art comes from a box the HUD already stands (a frame document), never from the
	/// interaction document, whose `RulerInfoBox.Background.Selection` names `frames_04.tex`.
	/// </summary>
	[TestCase]
	public void TheBarsArtComesFromAStandingBoxNotTheInteractionDocument()
	{
		AssertThat(Fable3ListCursor.Document).IsNotEqual(Fable3Elements.InteractionDocument);
		AssertThat(System.Array.Exists(Fable3ScreenFrames.Frames, f => f.Document == Fable3ListCursor.Document)).IsTrue();
	}

	/// <summary>
	/// The partners box's rule (TU3 sub_82B92A38): the top arrow while a row stands above the
	/// cursor, the bottom one while a row stands below it.
	/// </summary>
	[TestCase]
	public void TheArrowsMarkRowsAboveAndBelowTheCursor()
	{
		AssertThat(Fable3ListCursor.Arrows(1, 1)).IsEqual((false, false));
		AssertThat(Fable3ListCursor.Arrows(1, 9)).IsEqual((false, true));
		AssertThat(Fable3ListCursor.Arrows(2, 3)).IsEqual((true, true));
		AssertThat(Fable3ListCursor.Arrows(9, 9)).IsEqual((true, false));
		AssertThat(Fable3ListCursor.Arrows(0, 0)).IsEqual((false, false));
	}

	/// <summary>
	/// options.bgf centres the choice sections' rows (Options 1, Save Management 2, Multiplayer 5)
	/// and stands Game Settings' (3, and its front-end form 4) left at x −332.
	/// </summary>
	[TestCase]
	public void OnlyTheOptionsChoicePagesCentreTheirRows()
	{
		AssertThat(Fable3ListCursor.RowsCentred(1)).IsTrue();
		AssertThat(Fable3ListCursor.RowsCentred(2)).IsTrue();
		AssertThat(Fable3ListCursor.RowsCentred(5)).IsTrue();
		AssertThat(Fable3ListCursor.RowsCentred(3)).IsFalse();
		AssertThat(Fable3ListCursor.RowsCentred(4)).IsFalse();
		AssertThat(Fable3ListCursor.RowsCentred(0)).IsFalse();
	}

	/// <summary>options.bgf authors each section's arrows and no code writes them.</summary>
	[TestCase]
	public void TheOptionsSectionsKeepTheirAuthoredArrows()
	{
		AssertThat(Fable3ListCursor.OptionsArrows(1)).IsEqual((false, false));
		AssertThat(Fable3ListCursor.OptionsArrows(2)).IsEqual((false, true));
		AssertThat(Fable3ListCursor.OptionsArrows(3)).IsEqual((true, true));
		AssertThat(Fable3ListCursor.OptionsArrows(4)).IsEqual((true, true));
		AssertThat(Fable3ListCursor.OptionsArrows(5)).IsEqual((false, true));
	}
}
