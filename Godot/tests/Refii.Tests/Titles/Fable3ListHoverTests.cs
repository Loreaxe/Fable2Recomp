namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// The list template's hover (Fable3ListScreen.Hovers): a pointer that moved onto a row makes it the
/// cursor row, as the PC build's action 29 does; a resting pointer, a pointer off the rows or on the
/// cursor row already picks nothing (int7d D3: hover posted no `move` and moved no highlight because
/// the template never handed a hover to the screen).
/// </summary>
[TestSuite]
public class Fable3ListHoverTests
{
	[TestCase]
	public void AMovingPointerOnAnotherRowPicksIt()
	{
		AssertThat(Fable3ListScreen.Hovers(2, 0, 3, moved: true)).IsTrue();
	}

	[TestCase]
	public void ARestingPointerPicksNothing()
	{
		AssertThat(Fable3ListScreen.Hovers(2, 0, 3, moved: false)).IsFalse();
	}

	[TestCase]
	public void TheCursorRowOrNoRowPicksNothing()
	{
		AssertThat(Fable3ListScreen.Hovers(0, 0, 3, moved: true)).IsFalse();
		AssertThat(Fable3ListScreen.Hovers(-1, 0, 3, moved: true)).IsFalse();
		AssertThat(Fable3ListScreen.Hovers(3, 0, 3, moved: true)).IsFalse();
	}
}
