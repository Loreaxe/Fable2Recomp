namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using Godot;
using Refii.Titles.Fable3;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 3's HUD per half on a split couch screen: `hud.bgf`'s seat rules (each player's widget pair, the
/// shared world widgets, everything else the screen's), the seat taken from the drawn views, and player two's
/// half fed from player one's document. Pure; the drawing is the game lane's (NOTES.md).
/// </summary>
[TestSuite]
public class CouchHudSeatsTests
{
	private static Engine_HudLayout.Piece Piece(string id) =>
		new(id, id, new Engine_HudLayout.Anchor(0f, 0f), Vector2.Zero);

	[TestCase]
	public void EachPlayersWidgetsAreTheirsAndKeyedToPlayerTwosCopy()
	{
		Fable3HudLayout rules = Fable3HudLayout.Instance;
		foreach ((string one, string two) in Fable3HudLayout.Pairs)
		{
			AssertThat(rules.OwnerOf(Piece(one))).IsEqual(Engine_UiSeats.Seat.PlayerOne);
			AssertThat(rules.OwnerOf(Piece(two))).IsEqual(Engine_UiSeats.Seat.PlayerTwo);
			AssertThat(rules.PlayerTwoSourceId(one)).IsEqual(two);
			AssertThat(rules.PlayerOneIdOf(two)).IsEqual(one);
			AssertThat(rules.PlayerTwoSourceId(two)).IsNull();
			AssertThat(rules.SharedOnSplit(Piece(one))).IsFalse();
		}

		var layout = new Engine_HudLayout().Add(Piece("group.Dpad.dpad")).Add(Piece("group.Dpad2.dpad2"));
		AssertThat(rules.PlayerTwoSourceId(layout.Pieces[0].Id)).IsEqual(layout.Pieces[1].Id);
	}

	[TestCase]
	public void TheSharedWorldWidgetsAreInBothHalvesAndTheRestIsTheScreens()
	{
		Fable3HudLayout rules = Fable3HudLayout.Instance;
		foreach (string id in new[] { "group.QuestBox.TopBox", "group.Subtitles.Subtitles", "group.Counters.CornerBox", "group.ObjectiveList.ObjectiveList" })
		{
			AssertThat(rules.SharedOnSplit(Piece(id))).OverrideFailureMessage(id).IsTrue();
			AssertThat(rules.OwnerOf(Piece(id))).IsEqual(Engine_UiSeats.Seat.Whole);
		}

		foreach (string id in new[] { "group.ReceivedItems.ReceivedItems", "group.CrimeTimer.CrimeTimer", "group.DamageTaken.DamageTaken" })
		{
			AssertThat(rules.SharedOnSplit(Piece(id))).IsFalse();
			AssertThat(rules.OwnerOf(Piece(id))).IsEqual(Engine_UiSeats.Seat.Whole);
			AssertThat(rules.PlayerTwoSourceId(id)).IsNull();
		}
	}

	[TestCase]
	public void EveryPairAndSharedWidgetIsACutGroup()
	{
		// The ids are the cut groups' own (`group.{label}.{name}`, Engine_HudLayout.FromBindings).
		var cut = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
		foreach (Engine_HudLayout.GroupSubtree group in Fable3Hud.Groups)
		{
			cut.Add("group." + group.Label + "." + group.Name);
		}

		foreach ((string one, string two) in Fable3HudLayout.Pairs)
		{
			AssertThat(cut.Contains(one)).OverrideFailureMessage(one).IsTrue();
			AssertThat(cut.Contains(two)).OverrideFailureMessage(two).IsTrue();
		}

		foreach (string id in Fable3HudLayout.Shared)
		{
			AssertThat(cut.Contains(id)).OverrideFailureMessage(id).IsTrue();
		}
	}

	[TestCase]
	public void TheHudIsSeatedByTheDrawnViews()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null && !File.Exists(Path.Combine(at, "Godot", "src", "Titles", "Fable3", "Fable3HudDocument.cs")); i++)
		{
			at = Path.GetDirectoryName(at);
		}

		if (at is null)
		{
			return;
		}

		string document = File.ReadAllText(Path.Combine(at, "Godot", "src", "Titles", "Fable3", "Fable3HudDocument.cs"));
		foreach (string line in new[]
		{
			"bool split = Refii.Core.Engine_Views.Split;",
			"View.Seat = split ? Engine_UiSeats.Seat.PlayerOne : Engine_UiSeats.Seat.Whole;",
			"Seat = Engine_UiSeats.Seat.PlayerTwo,",
			"Values = new Engine_HudValues { Under = View.Values },",
			"ViewTwo.Suppressed = !split;",
			"ViewTwo.LiveSlides = View.LiveSlides;",
			"if (View is null || Document != Fable3Elements.HudDocument)",
		})
		{
			AssertThat(document.Contains(line, StringComparison.Ordinal)).OverrideFailureMessage(line).IsTrue();
		}

		string title = File.ReadAllText(Path.Combine(at, "Godot", "src", "Titles", "Fable3", "Fable3Title.cs"));
		AssertThat(title.Contains("public override Refii.Ui.Engine_HudTitleLayout HudLayout => Fable3HudLayout.Instance;", StringComparison.Ordinal)).IsTrue();
	}
}
