namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Ask = Refii.Titles.Fable3.Fable3InteractionList.Ask;

/// <summary>
/// The interaction list's rules: when it shows, its window and arrows, the PC arrangement's boxes,
/// the step lever's text, and the pointer's walk of the guest's cursor.
/// </summary>
[TestSuite]
public class Fable3InteractionListTests
{
	[TestCase]
	public void TheListShowsOnItsOwnSlidesUnderALiveRoot()
	{
		AssertThat(Fable3InteractionList.ShownAt(2, 1, true)).IsTrue();
		AssertThat(Fable3InteractionList.ShownAt(3, null, null)).IsTrue();
		AssertThat(Fable3InteractionList.ShownAt(4, 1, null)).IsTrue();
		AssertThat(Fable3InteractionList.ShownAt(1, 1, true)).IsFalse();
		AssertThat(Fable3InteractionList.ShownAt(null, 1, true)).IsFalse();
		// The mode's root faded (a screen over it) or switched off.
		AssertThat(Fable3InteractionList.ShownAt(2, 2, true)).IsFalse();
		AssertThat(Fable3InteractionList.ShownAt(2, 1, false)).IsFalse();
	}

	[TestCase]
	public void TheWindowIsThreeRowsFromTheCursorAndTheArrowsFollowTheGuestsBounds()
	{
		AssertThat(Fable3InteractionList.RowsShown(0, 5)).IsEqual(3);
		AssertThat(Fable3InteractionList.RowsShown(3, 5)).IsEqual(2);
		AssertThat(Fable3InteractionList.RowsShown(4, 5)).IsEqual(1);
		AssertThat(Fable3InteractionList.RowsShown(0, 0)).IsEqual(0);
		AssertThat(Fable3InteractionList.Arrows(0, 5)).IsEqual((false, true));
		AssertThat(Fable3InteractionList.Arrows(2, 5)).IsEqual((true, true));
		// The down step refuses on the last row (cursor under count - 1).
		AssertThat(Fable3InteractionList.Arrows(4, 5)).IsEqual((true, false));
		AssertThat(Fable3InteractionList.Arrows(0, 1)).IsEqual((false, false));
	}

	[TestCase]
	public void TheRowsStandTightUnderTheTabOnOneLeftEdge()
	{
		(float x0, float y0, float w0, float h0) = Fable3InteractionList.RowBox(0);
		(float x1, float y1, float w1, float _) = Fable3InteractionList.RowBox(1);
		(float _, float y2, float _, float _) = Fable3InteractionList.RowBox(2);
		AssertThat(y0 - y1).IsEqual(Fable3InteractionList.RowPitch);
		AssertThat(y1 - y2).IsEqual(Fable3InteractionList.RowPitch);
		AssertThat(w0).IsEqual(Fable3InteractionList.CursorBarWidth);
		AssertThat(w1).IsEqual(Fable3InteractionList.BarWidth);
		AssertThat(h0).IsEqual(Fable3InteractionList.BarHeight);
		// The wider cursor bar grows to the right: every bar keeps the same left edge.
		AssertThat(x0 - (w0 * 0.5f)).IsEqual(x1 - (w1 * 0.5f));
		(float tx, float ty, float tw, float th) = Fable3InteractionList.TabBox;
		AssertThat(tx - (tw * 0.5f)).IsEqual(x1 - (w1 * 0.5f));
		AssertThat(ty - (th * 0.5f) >= y0 + (h0 * 0.5f)).IsTrue();
		// The arrows clear the tab above and the last row below.
		(float _, float uy, float _, float uh) = Fable3InteractionList.ArrowBox(up: true, 3);
		(float _, float dy, float _, float dh) = Fable3InteractionList.ArrowBox(up: false, 3);
		AssertThat(uy - (uh * 0.5f) >= ty + (th * 0.5f)).IsTrue();
		AssertThat(dy + (dh * 0.5f) <= y2 - (h0 * 0.5f)).IsTrue();
	}

	[TestCase]
	public void AStepNamesItsPlayerAndDirection()
	{
		AssertThat(Fable3InteractionList.Step(Fable3ActingPlayer.One, down: true)).IsEqual("interaction-list:1:down");
		AssertThat(Fable3InteractionList.Step(Fable3ActingPlayer.Two, down: false)).IsEqual("interaction-list:2:up");
	}

	/// <summary>A click two rows down: a step, a wait for the stated cursor, a step, then the accept on the row.</summary>
	[TestCase]
	public void AWalkStepsOnlyOnTheStatedCursorAndAcceptsOnTheRow()
	{
		var walk = new Fable3InteractionList.Walk();
		walk.To(4, accept: true);
		AssertThat(walk.Next(2, 6, 0.0)).IsEqual(Ask.StepDown);
		// The guest has not stated the move yet: nothing is asked twice.
		AssertThat(walk.Next(2, 6, 0.1)).IsEqual(Ask.Nothing);
		AssertThat(walk.Next(3, 6, 0.15)).IsEqual(Ask.StepDown);
		AssertThat(walk.Next(4, 6, 0.2)).IsEqual(Ask.Accept);
		AssertThat(walk.Active).IsFalse();
		AssertThat(walk.Next(4, 6, 0.3)).IsEqual(Ask.Nothing);
	}

	[TestCase]
	public void AWalkAsksAgainAfterTheWaitAndGivesUp()
	{
		var walk = new Fable3InteractionList.Walk();
		walk.To(0, accept: false);
		double now = 0.0;
		AssertThat(walk.Next(1, 3, now)).IsEqual(Ask.StepUp);
		for (int again = 1; again < Fable3InteractionList.StepTries; ++again)
		{
			now += Fable3InteractionList.StepWait + 0.01;
			AssertThat(walk.Next(1, 3, now)).IsEqual(Ask.StepUp);
		}

		// The list's gate never let the step through: the walk ends, and never accepts.
		now += Fable3InteractionList.StepWait + 0.01;
		AssertThat(walk.Next(1, 3, now)).IsEqual(Ask.Nothing);
		AssertThat(walk.Active).IsFalse();
	}

	[TestCase]
	public void AWalkWithoutAcceptOnlyMovesAndARowPastTheEndIsDropped()
	{
		var walk = new Fable3InteractionList.Walk();
		walk.To(1, accept: false);
		AssertThat(walk.Next(0, 3, 0.0)).IsEqual(Ask.StepDown);
		AssertThat(walk.Next(1, 3, 0.1)).IsEqual(Ask.Nothing);
		AssertThat(walk.Active).IsFalse();
		walk.To(5, accept: true);
		AssertThat(walk.Next(0, 3, 0.0)).IsEqual(Ask.Nothing);
		AssertThat(walk.Active).IsFalse();
	}
}
