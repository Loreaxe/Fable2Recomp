namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>
/// The display boxes as their classes' own Open and Close state them (display_box_hooks.cpp):
/// the registry the owned surfaces and the box pages stand on, the menu box's populate, the
/// router opener's stated text, and the screen each box stands on. Pure model code.
/// </summary>
[TestSuite]
public class DisplayBoxStatementsTests
{
	private const uint Query = 0x4C001000u;
	private const uint Adjuster = 0x4C002000u;
	private const uint Message = 0x4C003000u;

	[BeforeTest]
	public void Reset() => Engine_UiDisplayBoxes.Clear();

	[TestCase]
	public void TheNewestOpenBoxIsTheOneShowing()
	{
		Engine_UiDisplayBoxes.Opened(Message, Engine_UiDisplayBoxes.Message, "Unnamed", "Saved.", System.Array.Empty<string>());
		Engine_UiDisplayBoxes.Opened(Query, Engine_UiDisplayBoxes.Query, "PubGameQuery", "Play Keystone?", System.Array.Empty<string>());

		AssertThat(Engine_UiDisplayBoxes.Showing!.Address).IsEqual(Query);
		AssertThat(Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.Query)!.Body).IsEqual("Play Keystone?");
		AssertThat(Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.Message)).IsNull();

		AssertThat(Engine_UiDisplayBoxes.Closed(Query)).IsTrue();
		AssertThat(Engine_UiDisplayBoxes.Showing!.Address).IsEqual(Message);
		AssertThat(Engine_UiDisplayBoxes.Closed(Query)).IsFalse();
	}

	/// <summary>The two queues each show their own box: a strip on the +0x20 queue does not hide
	/// the question on the +4 queue, and input goes to the +4 queue's.</summary>
	[TestCase]
	public void EachQueueShowsItsOwnBox()
	{
		const uint strip = 0x4C008000u;
		const uint card = 0x4C009000u;
		Engine_UiDisplayBoxes.Opened(Query, Engine_UiDisplayBoxes.Query, "a", "q", System.Array.Empty<string>());
		Engine_UiDisplayBoxes.Opened(strip, Engine_UiDisplayBoxes.Intermission, "b", "Chapter", System.Array.Empty<string>());

		AssertThat(Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.Query)!.Address).IsEqual(Query);
		AssertThat(Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.Intermission)!.Address).IsEqual(strip);
		AssertThat(Engine_UiDisplayBoxes.Showing!.Address).IsEqual(Query);

		Engine_UiDisplayBoxes.Closed(Query);
		Engine_UiDisplayBoxes.Opened(card, Engine_UiDisplayBoxes.AcceptHold, "c", "Paper", System.Array.Empty<string>());
		AssertThat(Engine_UiDisplayBoxes.Showing!.Address).IsEqual(card);
		AssertThat(Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.Intermission)).IsNull();
	}

	[TestCase]
	public void AReopenedBoxReplacesItsOldStatement()
	{
		Engine_UiDisplayBoxes.Opened(Query, Engine_UiDisplayBoxes.Query, "a", "first", System.Array.Empty<string>());
		Engine_UiDisplayBoxes.Opened(Query, Engine_UiDisplayBoxes.Query, "a", "second", System.Array.Empty<string>());

		AssertThat(Engine_UiDisplayBoxes.Open.Count).IsEqual(1);
		AssertThat(Engine_UiDisplayBoxes.Showing!.Body).IsEqual("second");
	}

	/// <summary>The adjuster's fields are its widget: value, min, max, step, flags (1 money, 2
	/// percent, 4 sign) — the temple donation's 100 gold in steps of 100 up to 1000.</summary>
	[TestCase]
	public void TheAdjusterStandsOnItsOwnWidget()
	{
		Engine_UiStatedBoxes.Box box = Engine_UiDisplayBoxes.Opened(Adjuster, Engine_UiDisplayBoxes.Adjuster,
			"Unnamed", "Donate?", new[] { "100", "100", "1000", "100", "1" });

		AssertThat(box.Adjuster).IsNotNull();
		AssertThat(box.Adjuster!.Value).IsEqual(100);
		AssertThat(box.Adjuster.CanDecrease).IsFalse();
		AssertThat(box.Adjuster.CanIncrease).IsTrue();
		AssertThat(box.Adjuster.Display).IsEqual("100");

		box.Adjuster.Value += box.Adjuster.Increment;
		AssertThat(Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.Adjuster)!.Adjuster!.Value).IsEqual(200);
	}

	[TestCase]
	public void APercentAdjusterWithASignShowsBoth()
	{
		Engine_UiStatedBoxes.Box box = Engine_UiDisplayBoxes.Opened(Adjuster, Engine_UiDisplayBoxes.Adjuster,
			"Unnamed", "Tax", new[] { "10", "-50", "50", "5", "6" });

		AssertThat(box.Adjuster!.Display).IsEqual("+10%");
	}

	/// <summary>The menu box's rows come as the populate states them: the tag prefixed `+` enabled
	/// or `-` disabled; the close forgets the box.</summary>
	[TestCase]
	public void MenuRowsCarryTheirEnabledByte()
	{
		AssertThat(Engine_UiDisplayBoxes.Row("+GUI_YES")).IsEqual(("GUI_YES", true));
		AssertThat(Engine_UiDisplayBoxes.Row("-GUI_NO")).IsEqual(("GUI_NO", false));
		AssertThat(Engine_UiDisplayBoxes.Row(string.Empty)).IsNull();
	}

	[TestCase]
	public void TheMenuBoxLivesFromItsPopulateToItsClose()
	{
		const uint box = 0x4C005000u;
		var model = new Engine_UiGuiBoxes.MenuBoxModel("Archon's Toolbox",
			new[] { new Engine_UiGuiBoxes.MenuOption("Spawn", true), new Engine_UiGuiBoxes.MenuOption("Teleport", false) },
			Cancelable: true);
		Engine_UiDisplayBoxes.MenuOpened(box, model, card: false);

		AssertThat(Engine_UiDisplayBoxes.Menu!.Model.Options.Count).IsEqual(2);
		AssertThat(Engine_UiDisplayBoxes.Menu.Model.Text).IsEqual("Archon's Toolbox");

		Engine_UiDisplayBoxes.MenuClosed(0x4C006000u);
		AssertThat(Engine_UiDisplayBoxes.Menu).IsNotNull();

		Engine_UiDisplayBoxes.MenuClosed(box);
		AssertThat(Engine_UiDisplayBoxes.Menu).IsNull();
	}

	/// <summary>The Orb card's name is the one its build stated (`sub_823DB850` r5): kept across a
	/// row's repopulate, dropped by the close.</summary>
	[TestCase]
	public void TheCardsNameIsWhatItsBuildStated()
	{
		const uint box = 0x4C005000u;
		var model = new Engine_UiGuiBoxes.MenuBoxModel("Orb",
			new[] { new Engine_UiGuiBoxes.MenuOption("Join", true) }, Cancelable: true);
		Engine_UiDisplayBoxes.MenuOpened(box, model, card: true);
		AssertThat(Engine_UiDisplayBoxes.Menu!.GamerName).IsNull();

		Engine_UiDisplayBoxes.GamerCardNamed(0x4C006000u, "Other");
		AssertThat(Engine_UiDisplayBoxes.Menu!.GamerName).IsNull();

		Engine_UiDisplayBoxes.GamerCardNamed(box, string.Empty);
		AssertThat(Engine_UiDisplayBoxes.Menu!.GamerName).IsEqual(string.Empty);

		Engine_UiDisplayBoxes.MenuOpened(box, model, card: true);
		AssertThat(Engine_UiDisplayBoxes.Menu!.GamerName).IsEqual(string.Empty);

		Engine_UiDisplayBoxes.MenuClosed(box);
		Engine_UiDisplayBoxes.MenuOpened(box, model, card: true);
		AssertThat(Engine_UiDisplayBoxes.Menu!.GamerName).IsNull();
		Engine_UiDisplayBoxes.MenuClosed(box);
	}

	/// <summary>The quest box's page comes from its own fields; with no strings a tag stands as it
	/// is, and a condition shows only up to the stated count.</summary>
	[TestCase]
	public void TheQuestBoxPageIsTheBoxsFields()
	{
		Engine_UiStatedBoxes.Box box = Engine_UiDisplayBoxes.Opened(0x4C007000u, Engine_UiDisplayBoxes.Quest,
			"Unnamed Quest", "Find the bandit.", new[]
			{
				"2", "3", "1", "COND_ONE", "COND_TWO", "OCC", "APP", "FEAT", "NAME", "WHERE", "WHY",
			});

		Engine_UiQuestBoxPage.Statement? page = Engine_UiQuestBoxPage.Of(box, null);
		AssertThat(page).IsNotNull();
		AssertThat(page!.Type).IsEqual(2);
		AssertThat(page.RiskLevel).IsEqual(3);
		AssertThat(page.Description).IsEqual("Find the bandit.");
		AssertThat(page.Occupation).IsEqual("OCC");
		AssertThat(page.Reason).IsEqual("WHY");
		AssertThat(page.Condition1).IsEqual("COND_ONE");
		AssertThat(page.Condition2).IsEqual(string.Empty);

		AssertThat(Engine_UiQuestBoxPage.Slides(page)["elements.Stars"]).IsEqual(8);
		AssertThat(Engine_UiQuestBoxPage.Active(page)["elements.Condition2"]).IsFalse();
		AssertThat(Engine_UiQuestBoxPage.Of(Engine_UiDisplayBoxes.Opened(Query, Engine_UiDisplayBoxes.Query,
			"a", "q", System.Array.Empty<string>()), null)).IsNull();
	}

	/// <summary>Screen 7 (the `infocard` book) stands only for the hold-to-accept card; the
	/// acceptance and button boxes are the display-box panel's.</summary>
	[TestCase]
	public void OnlyTheHoldCardStandsTheInfoCardBook()
	{
		AssertThat(HostUiSource.ScreenOfBox(Engine_UiDisplayBoxes.AcceptHold)).IsEqual(7u);
		AssertThat(HostUiSource.ScreenOfBox(Engine_UiDisplayBoxes.Acceptance)).IsEqual(uint.MaxValue);
		AssertThat(HostUiSource.ScreenOfBox(Engine_UiDisplayBoxes.Button)).IsEqual(uint.MaxValue);
		AssertThat(HostUiSource.ScreenOfBox(Engine_UiDisplayBoxes.Quest)).IsEqual(5u);
		AssertThat(HostUiSource.ScreenOfBox(Engine_UiDisplayBoxes.Query)).IsEqual(19u);
		AssertThat(HostUiSource.ScreenOfBox(-1)).IsEqual(uint.MaxValue);
	}

	/// <summary>An opener that passed an empty text stated one; a box never stated has none.</summary>
	[TestCase]
	public void AnEmptyStatedTextIsStillStated()
	{
		Engine_UiModalText.Clear();
		AssertThat(Engine_UiModalText.For(2u, 21u)).IsNull();

		Engine_UiModalText.Stated(2u, 21u, string.Empty);
		AssertThat(Engine_UiModalText.For(2u, 21u)).IsEqual(string.Empty);
		AssertThat(Engine_UiModalText.For(3u, 21u)).IsNull();

		Engine_UiModalText.Stated(2u, 21u, "Ragdoll purchased.");
		AssertThat(Engine_UiModalText.For(2u, 21u)).IsEqual("Ragdoll purchased.");
		Engine_UiModalText.Clear();
	}
}
