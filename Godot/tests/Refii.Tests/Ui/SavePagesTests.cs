namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

/// <summary>
/// The save fans' pages (<see cref="Engine_FrontEndSavePages"/>): five to a page, each page laid out by
/// the guest's own first-card rule, sideways steps carrying over a page edge, page turns landing on the
/// first card (or, back, the last), and the page buttons folded from the bumpers and the triggers.
/// </summary>
[TestSuite]
public class SavePagesTests
{
	[TestCase]
	public void TenSavesAreTwoPagesOfFive()
	{
		AssertThat(Engine_FrontEndSavePages.PageCount(0)).IsEqual(1);
		AssertThat(Engine_FrontEndSavePages.PageCount(5)).IsEqual(1);
		AssertThat(Engine_FrontEndSavePages.PageCount(6)).IsEqual(2);
		AssertThat(Engine_FrontEndSavePages.PageCount(8)).IsEqual(2);
		AssertThat(Engine_FrontEndSavePages.PageCount(10)).IsEqual(2);
		AssertThat(Engine_FrontEndSavePages.PageCount(11)).IsEqual(3);

		AssertThat(Engine_FrontEndSavePages.CountOn(8, 0)).IsEqual(5);
		AssertThat(Engine_FrontEndSavePages.CountOn(8, 1)).IsEqual(3);
		AssertThat(Engine_FrontEndSavePages.CountOn(8, 2)).IsEqual(0);
		AssertThat(Engine_FrontEndSavePages.PageOf(4)).IsEqual(0);
		AssertThat(Engine_FrontEndSavePages.PageOf(5)).IsEqual(1);
		AssertThat(Engine_FrontEndSavePages.FirstOn(1)).IsEqual(5);
	}

	[TestCase]
	public void EachPageTakesTheGuestsFirstCardForItsCount()
	{
		// Load fan 0x826E0670..0x826E0698: 2 -> card 3, 3..4 -> card 2, 5 -> card 1; co-op picker: 1 -> card 3.
		AssertThat(Engine_FrontEndSavePages.BaseSlot(1)).IsEqual(3);
		AssertThat(Engine_FrontEndSavePages.BaseSlot(2)).IsEqual(3);
		AssertThat(Engine_FrontEndSavePages.BaseSlot(3)).IsEqual(2);
		AssertThat(Engine_FrontEndSavePages.BaseSlot(4)).IsEqual(2);
		AssertThat(Engine_FrontEndSavePages.BaseSlot(5)).IsEqual(1);

		// Every page fits the six authored cards.
		for (int shown = 1; shown <= Engine_FrontEndSavePages.PageSize; shown++)
		{
			AssertThat(Engine_FrontEndSavePages.BaseSlot(shown) - 1 + shown <= Engine_FrontEndSaveCards.MaxCards).IsTrue();
		}
	}

	[TestCase]
	public void SidewaysStepsCarryOverThePageEdgeAndStopAtTheEnds()
	{
		AssertThat(Engine_FrontEndSavePages.Step(4, 1, 8)).IsEqual(5);
		AssertThat(Engine_FrontEndSavePages.Step(5, -1, 8)).IsEqual(4);
		AssertThat(Engine_FrontEndSavePages.Step(7, 1, 8)).IsEqual(-1);
		AssertThat(Engine_FrontEndSavePages.Step(0, -1, 8)).IsEqual(-1);
		AssertThat(Engine_FrontEndSavePages.Step(2, 0, 8)).IsEqual(-1);
	}

	[TestCase]
	public void PageTurnsLandOnTheFirstCardOrPagingBackTheLast()
	{
		AssertThat(Engine_FrontEndSavePages.Turn(2, 1, 8)).IsEqual(5);
		AssertThat(Engine_FrontEndSavePages.Turn(6, -1, 8)).IsEqual(4);
		AssertThat(Engine_FrontEndSavePages.Turn(6, 1, 8)).IsEqual(-1);
		AssertThat(Engine_FrontEndSavePages.Turn(1, -1, 8)).IsEqual(-1);
		AssertThat(Engine_FrontEndSavePages.Turn(0, 1, 5)).IsEqual(-1);
		AssertThat(Engine_FrontEndSavePages.Turn(10, -1, 12)).IsEqual(9);
	}

	[TestCase]
	public void TheIndicatorShowsOnlyWithMoreThanOnePage()
	{
		AssertThat(Engine_FrontEndSavePages.Indicator("Page {0} / {1}", 0, 1)).IsEqual(string.Empty);
		AssertThat(Engine_FrontEndSavePages.Indicator("Page {0} / {1}", 0, 2)).IsEqual("Page 1 / 2");
		AssertThat(Engine_FrontEndSavePages.Indicator("Seite {0} von {1}", 1, 2)).IsEqual("Seite 2 von 2");
	}

	[TestCase]
	public void BumpersAndTriggersTurnPagesOncePerPress()
	{
		var buttons = new Engine_FrontEndSavePages.PageButtons();

		// RB: next, once.
		AssertThat(buttons.Feed(false, true, 0, 0)).IsEqual((true, false));
		AssertThat(buttons.Feed(false, true, 0, 0)).IsEqual((false, false));
		AssertThat(buttons.Feed(false, false, 0, 0)).IsEqual((false, false));

		// RT past the XInput threshold of 30: next; at 30 it is not pressed.
		AssertThat(buttons.Feed(false, false, 0, 30)).IsEqual((false, false));
		AssertThat(buttons.Feed(false, false, 0, 31)).IsEqual((true, false));
		AssertThat(buttons.Feed(false, true, 0, 200)).IsEqual((false, false));
		AssertThat(buttons.Feed(false, false, 0, 0)).IsEqual((false, false));

		// LB and LT: previous.
		AssertThat(buttons.Feed(true, false, 0, 0)).IsEqual((false, true));
		AssertThat(buttons.Feed(false, false, 0, 0)).IsEqual((false, false));
		AssertThat(buttons.Feed(false, false, 255, 0)).IsEqual((false, true));

		// A new source takes what is already down as held.
		buttons.Hold(false, true, 0, 0);
		AssertThat(buttons.Feed(false, true, 0, 0)).IsEqual((false, false));
	}
}
