namespace Refii.Tests.Titles;

using System;
using System.IO;
using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Titles.Fable3;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 3's HUD on a split couch screen: in a half (950 canvas units wide) the money alerts hang off that half's
/// own edges, at the distance they have from the edge of a 16:9 window; the whole window places them as cut.
/// Pure layout arithmetic.
/// </summary>
[TestSuite]
public class Fable3SplitHudTests
{
	private static Engine_HudLayout.GroupSubtree Group(string name)
	{
		foreach (Engine_HudLayout.GroupSubtree group in Fable3Hud.Groups)
		{
			if (group.Name == name)
			{
				return group;
			}
		}

		throw new InvalidOperationException(name);
	}

	private static Engine_HudLayout.Piece Centred(string id, float offsetX) =>
		new(id, id, new Engine_HudLayout.Anchor(0.5f, 0.5f), new Vector2(offsetX, 1f));

	private static readonly Vector2 Wide = Engine_HudLayout.CanvasExtent(new Vector2(2560f, 1440f));

	private static readonly Vector2 Half = Engine_HudLayout.CanvasExtent(new Vector2(1280f, 1440f));

	[TestCase]
	public void TheWholeWindowPlacesTheAlertsAsCut()
	{
		// No fixed anchor: the cut stands as it did, so the whole-window picture is unchanged.
		AssertThat(Group("MoneyAlert").Anchor == null).IsTrue();
		AssertThat(Group("MoneyAlertSecondPlayer").Anchor == null).IsTrue();
		AssertThat(Fable3HudLayout.MoneyOne).IsEqual("group." + Group("MoneyAlert").Label + "." + Group("MoneyAlert").Name);
		AssertThat(Fable3HudLayout.MoneyTwo)
			.IsEqual("group." + Group("MoneyAlertSecondPlayer").Label + "." + Group("MoneyAlertSecondPlayer").Name);

		// Every other piece, a moved alert, and every title that states nothing: placed as it is in a half too.
		Engine_HudLayout.Piece other = Centred("group.QuestBox.TopBox", 0f);
		AssertThat(Fable3HudLayout.Instance.SeatedPiece(other)).IsEqual(other);
		var moved = new Engine_HudLayout.Piece(Fable3HudLayout.MoneyOne, "MoneyAlert", new Engine_HudLayout.Anchor(0f, 0.5f), new Vector2(40f, 1f));
		AssertThat(Fable3HudLayout.Instance.SeatedPiece(moved)).IsEqual(moved);
		Engine_HudLayout.Piece one = Centred(Fable3HudLayout.MoneyOne, 125f);
		AssertThat(Engine_HudTitleLayout.None.SeatedPiece(one)).IsEqual(one);
	}

	[TestCase]
	public void InAHalfEachAlertKeepsItsDistanceFromItsOwnEdge()
	{
		Engine_HudLayout.Piece one = Centred(Fable3HudLayout.MoneyOne, 125f);
		Engine_HudLayout.Piece two = Centred(Fable3HudLayout.MoneyTwo, -210f);
		Engine_HudLayout.Piece seatedOne = Fable3HudLayout.Instance.SeatedPiece(one);
		Engine_HudLayout.Piece seatedTwo = Fable3HudLayout.Instance.SeatedPiece(two);
		AssertThat(seatedOne.Anchor).IsEqual(new Engine_HudLayout.Anchor(0f, 0.5f));
		AssertThat(seatedTwo.Anchor).IsEqual(new Engine_HudLayout.Anchor(1f, 0.5f));
		AssertThat(seatedOne.Offset.Y).IsEqual(one.Offset.Y);

		// From the left edge (player one's) and from the right edge (player two's), as on a 16:9 window.
		float wideOne = Engine_HudLayout.Place(one, Wide).X + (Wide.X * 0.5f);
		float halfOne = Engine_HudLayout.Place(seatedOne, Half).X + (Half.X * 0.5f);
		AssertThat(Mathf.Abs(halfOne - wideOne) < 0.01f).IsTrue();
		float wideTwo = (Wide.X * 0.5f) - Engine_HudLayout.Place(two, Wide).X;
		float halfTwo = (Half.X * 0.5f) - Engine_HudLayout.Place(seatedTwo, Half).X;
		AssertThat(Mathf.Abs(halfTwo - wideTwo) < 0.01f).IsTrue();
	}

	[TestCase]
	public void CentreAnchoredTheHalfsEdgeCameTooNear()
	{
		// Measured from the left edge: a centre-anchored alert lands 158 units nearer the edge of a half than
		// of a 16:9 window, past it once the alert's art reaches the window's edge.
		Engine_HudLayout.Piece centre = Centred(Fable3HudLayout.MoneyOne, 125f);
		Vector2 half = Half;
		float wideFromEdge = Engine_HudLayout.Place(centre, Wide).X + (Wide.X * 0.5f);
		float centreFromEdge = Engine_HudLayout.Place(centre, half).X + (half.X * 0.5f);
		AssertThat(centreFromEdge < wideFromEdge - 150f).IsTrue();
	}

	[TestCase]
	public void OnlyFable3sSeatedViewsKeepToTheirHalf()
	{
		AssertThat(Fable3HudLayout.Instance.KeepsToSeat).IsTrue();
		AssertThat(Engine_HudTitleLayout.None.KeepsToSeat).IsFalse();
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable2).HudLayout.KeepsToSeat).IsFalse();
	}

	[TestCase]
	public void TheUiShadersClipToAnUnboundedSeatByDefault()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null && !File.Exists(Path.Combine(at, "Godot", "src", "Engine", "ui", "ui_quad.gdshader")); i++)
		{
			at = Path.GetDirectoryName(at);
		}

		if (at is null)
		{
			return;
		}

		foreach (string shader in new[] { "ui_quad.gdshader", "ui_text.gdshader" })
		{
			string text = File.ReadAllText(Path.Combine(at, "Godot", "src", "Engine", "ui", shader));
			foreach (string line in new[]
			{
				"uniform vec4 seat_rect = vec4(-1e9, -1e9, 1e9, 1e9);",
				"seat_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xy;",
				"clipped = clipped || seat_world.x < seat_rect.x || seat_world.x > seat_rect.z ||",
			})
			{
				AssertThat(text.Contains(line, StringComparison.Ordinal)).OverrideFailureMessage(shader + ": " + line).IsTrue();
			}
		}

		string theme = File.ReadAllText(Path.Combine(at, "Godot", "src", "Engine", "ui", "Engine_UiTheme.cs"));
		AssertThat(CountOf(theme, "fresh.SetShaderParameter(SeatRectName, sr);")).IsEqual(2);
	}

	[TestCase]
	public void ALabelReMeshedInASeatedViewKeepsToTheSeat()
	{
		// The money digits are empty (no material) when the view is seated; their first string is meshed afresh,
		// so each re-mesh site of the view paints the seat again (int7g F1: player two's "7" in player one's half).
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null && !File.Exists(Path.Combine(at, "Godot", "src", "Engine", "ui", "Engine_HudView.cs")); i++)
		{
			at = Path.GetDirectoryName(at);
		}

		if (at is null)
		{
			return;
		}

		string view = File.ReadAllText(Path.Combine(at, "Godot", "src", "Engine", "ui", "Engine_HudView.cs"));
		AssertThat(CountOf(view, "Theme.SetMeshText(label, live);\n                        KeepReMeshedToSeat(label);")).IsEqual(1);
		AssertThat(CountOf(view, "wrapMode: record?.WrapMode ?? 0);\n\n        KeepReMeshedToSeat(label);")).IsEqual(1);
		AssertThat(CountOf(view, "Theme.SetMeshText(")).IsEqual(1);
		AssertThat(CountOf(view, "Theme.Paint(")).IsEqual(1);
	}

	private static int CountOf(string text, string what)
	{
		int count = 0;
		for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
		{
			++count;
		}

		return count;
	}
}
