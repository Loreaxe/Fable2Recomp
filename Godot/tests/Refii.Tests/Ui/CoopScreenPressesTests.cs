namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

/// <summary>
/// The co-op screen's presses as the guest's own codes (`CoopAction:&lt;code&gt;:&lt;player&gt;`): edges of
/// the D-pad and the stick with their releases, the face buttons, and what each player forwards.
/// </summary>
[TestSuite]
public class CoopScreenPressesTests
{
	private static List<int> Feed(ref Fable2CoopScreenPresses.PadEdges edges, bool up = false, bool down = false,
		bool left = false, bool right = false, bool a = false, bool b = false, bool x = false, bool y = false,
		int leftX = 0, int leftY = 0) =>
		edges.Feed(up, down, left, right, a, b, x, y, leftX, leftY);

	[TestCase]
	public void ADirectionIsAPressThenItsRelease()
	{
		var edges = new Fable2CoopScreenPresses.PadEdges();
		AssertThat(Feed(ref edges, down: true)).ContainsExactly(29);
		AssertThat(Feed(ref edges, down: true)).IsEmpty();
		AssertThat(Feed(ref edges)).ContainsExactly(33);
		AssertThat(Feed(ref edges, left: true)).ContainsExactly(30);
		AssertThat(Feed(ref edges)).ContainsExactly(34);
	}

	[TestCase]
	public void TheStickIsADirectionPastThreeQuartersUntilBelowTwoThirds()
	{
		var edges = new Fable2CoopScreenPresses.PadEdges();
		AssertThat(Feed(ref edges, leftX: (int)(0.7 * 32767))).IsEmpty();
		AssertThat(Feed(ref edges, leftX: (int)(0.8 * 32767))).ContainsExactly(31);
		AssertThat(Feed(ref edges, leftX: (int)(0.7 * 32767))).IsEmpty();
		AssertThat(Feed(ref edges, leftX: (int)(0.6 * 32767))).ContainsExactly(35);
		AssertThat(Feed(ref edges, leftY: (int)(0.9 * 32767))).ContainsExactly(28);
	}

	[TestCase]
	public void FaceButtonsArePressesOnly()
	{
		var edges = new Fable2CoopScreenPresses.PadEdges();
		AssertThat(Feed(ref edges, a: true, y: true)).ContainsExactly(36, 39);
		AssertThat(Feed(ref edges)).IsEmpty();
		AssertThat(Feed(ref edges, b: true, x: true)).ContainsExactly(37, 38);
	}

	[TestCase]
	public void EachPlayerForwardsWhatTheGuestAnswers()
	{
		var none = Engine_CoopSavePicker.Press.None;
		AssertThat(Fable2CoopScreenPresses.Forwarded(36, 1, none)).IsTrue();
		AssertThat(Fable2CoopScreenPresses.Forwarded(38, 1, none)).IsTrue();
		AssertThat(Fable2CoopScreenPresses.Forwarded(30, 1, none)).IsFalse();
		AssertThat(Fable2CoopScreenPresses.Forwarded(39, 1, none)).IsFalse();
		AssertThat(Fable2CoopScreenPresses.Forwarded(29, 2, none)).IsTrue();
		AssertThat(Fable2CoopScreenPresses.Forwarded(39, 2, none)).IsTrue();
		AssertThat(Fable2CoopScreenPresses.Forwarded(38, 2, none)).IsFalse();
		AssertThat(Fable2CoopScreenPresses.Forwarded(31, 2, Engine_CoopSavePicker.Press.Right)).IsFalse();
		AssertThat(Fable2CoopScreenPresses.Forwarded(36, 2, Engine_CoopSavePicker.Press.Accept)).IsFalse();
		AssertThat(Fable2CoopScreenPresses.Forwarded(35, 2, Engine_CoopSavePicker.Press.Right)).IsTrue();
		AssertThat(Fable2CoopScreenPresses.Lever(36, 2)).IsEqual("CoopAction:36:2");
	}
}
