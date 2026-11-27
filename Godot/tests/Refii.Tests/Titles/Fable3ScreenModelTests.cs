namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Probe = Refii.Titles.Fable3.Fable3ScreenModel.Probe;

/// <summary>
/// Fable 3's box-manager screens from a stated set: the texts, slides and meters their writers
/// state, and the cursor rows, rows and signature their own calls state (Fable3ScreenState) — the
/// hero status, stats, promises, quest summary and king's schedule classes (Fable3ScreenModel's
/// citations).
/// </summary>
[TestSuite]
public class Fable3ScreenModelTests
{
	private static string Resolve(string tag) => "<" + tag + ">";

	private static Probe State(int state) => new(state, false, null, null);

	private static Probe Text(string text) => new(null, false, text, null);

	private static readonly Fable3ScreenState None = new();

	[TestCase]
	public void HeroStatusStatesPipsMetersAndWeapons()
	{
		var stated = new Dictionary<string, Probe>
		{
			["HeroStatus.Section1.Values.Strength"] = State(4),
			["HeroStatus.Section1.Values.Will"] = State(9),
			["HeroStatus.Section2.Values.Morality"] = new(null, false, null, 750f),
			["HeroStatus.Section3.Titles.Melee"] = Text("Hammer"),
			["HeroStatus.Section3.Values.Melee"] = new(2, false, null, null),
			["HeroStatus.Section3.Titles.Ranged"] = Text(string.Empty),
			["HeroStatus.Section3.Values.Ranged"] = new(null, true, null, null),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.HeroStatus, stated, None, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("<GUI_TEXT_HERO_STATUS_TITLE>");
		AssertThat(page.Rows.Count).IsEqual(4);
		AssertThat(page.Rows[0].Line).IsEqual("<TEXT_GUI_STRENGTH>");
		AssertThat(page.Rows[0].Pips == (4, 6)).IsTrue();
		AssertThat(page.Rows[1].Line).IsEqual("<TEXT_GUI_WILL>");
		AssertThat(page.Rows[1].Pips == (6, 6)).IsTrue();
		AssertThat(page.Rows[2].Line).IsEqual("<TEXT_GUI_MORALITY>");
		AssertThat(page.Rows[2].Bar == 0.75f).IsTrue();
		AssertThat(page.Rows[2].Pips.HasValue).IsFalse();
		AssertThat(page.Rows[3].Line).IsEqual("Hammer");
		AssertThat(page.Rows[3].Pips == (2, 2)).IsTrue();
		NoRowSpellsArtWithCharacters(page);
		AssertThat(page.Cursor).IsFalse();
		AssertThat(page.Prompts.Count).IsEqual(1);
		AssertThat(page.Prompts[0].Button).IsEqual("B");
	}

	[TestCase]
	public void StatsHighlightsTheStatedCursorInTheStatedWindow()
	{
		var stated = new Dictionary<string, Probe>
		{
			["StatsBox.Table.Categories.Details.CompStatisticCategories0.Categories.Category01"] = Text("Combat"),
			["StatsBox.Table.Categories.Details.CompStatisticCategories1.Categories.Category01"] = Text("Money"),
			["StatsBox.Table.Categories.Details.CompStatisticCategories2.Categories.Category01"] = Text("stale"),
		};
		var screen = new Fable3ScreenState { Cursor = 1 };
		screen.StatsWindows[2] = (0, 2, 8);

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Stats, stated, screen, Resolve)!.Value;
		// The guest's two categories, then the host's Achievements category (the list's end is in view).
		AssertThat(page.Rows.Count).IsEqual(3);
		AssertThat(page.Rows[0].Line).IsEqual("Combat");
		AssertThat(page.Rows[2].Name).IsEqual(Fable3ScreenModel.StatsAchievementsRow);
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Title).IsEqual("<TEXT_GUI_STATS_SCREEN_TITLE>");
		screen.StatsAchievements = true;
		AssertThat(Fable3ScreenModel.Compose(Fable3ScreenModel.Stats, stated, screen, Resolve)!.Value.Selected).IsEqual(2);
		screen.StatsAchievements = false;

		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails0.Statistics.Title.Statistic"] = Text("Gold earned");
		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails0.Statistics.Title.Score"] = Text("120");
		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails1.Statistics.Title.Statistic"] = Text("Gold spent");
		screen.StatsPage = 3;
		screen.Cursor = 7;
		screen.StatsWindows[3] = (6, 12, 6);
		page = Fable3ScreenModel.Compose(Fable3ScreenModel.Stats, stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("Gold earned    120");
		AssertThat(page.Selected).IsEqual(1);
	}

	[TestCase]
	public void QuestRowsAreTheWritersCallsUpToTheLast()
	{
		var stated = new Dictionary<string, Probe>
		{
			["QuestSummary.Details.QuestSummaryDetails0.Text.QuestName"] = Text("The Gathering"),
			["QuestSummary.Details.QuestSummaryDetails0.Text.QuestLocation"] = Text("Bowerstone"),
			["QuestSummary.Details.QuestSummaryDetails1.Text.QuestName"] = Text("Rebel"),
			["QuestSummary.Details.QuestSummaryDetails2.Text.QuestName"] = Text("stale"),
			["QuestSummary.ObjectiveTitle"] = Text("Find Walter"),
		};
		var screen = new Fable3ScreenState();
		screen.QuestRows[0] = (0x1000u, false, false);
		screen.QuestRows[1] = (0x2000u, true, true);
		screen.QuestRows[2] = (0x3000u, false, true);

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Quests, stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("The Gathering    Bowerstone");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Description).IsEqual("Find Walter");
		AssertThat(page.Cursor).IsTrue();
	}

	[TestCase]
	public void ScheduleIsItsShowsStatementsWithNoElementWritten()
	{
		System.Func<uint, string?> before = Fable3Elements.TagText;
		Fable3Elements.TagText = tag => tag switch
		{
			0x11u => "Hold court",
			0x22u => "Feast",
			0xA1u => "The Queen's Schedule",
			0xD1u => "Day %1",
			_ => null,
		};
		try
		{
			var screen = new Fable3ScreenState { Schedule = (0xA1u, 0xD1u, 3) };
			screen.ScheduleRows[1] = (2, 0x11u);
			screen.ScheduleRows[2] = (1, 0x22u);
			screen.ScheduleRows[3] = (4, 0u);
			screen.ScheduleRows[4] = (4, 0u);

			Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Schedule, new Dictionary<string, Probe>(), screen, Resolve)!.Value;
			AssertThat(page.Title).IsEqual("The Queen's Schedule");
			AssertThat(page.Description).IsEqual("Day 3");
			AssertThat(page.Rows.Count).IsEqual(2);
			AssertThat(page.Rows[0].Line).IsEqual("Hold court");
			AssertThat(page.Rows[0].Pips == (1, 1)).IsTrue();
			AssertThat(page.Rows[1].Line).IsEqual("Feast");
			AssertThat(page.Rows[1].Pips == (0, 1)).IsTrue();
			AssertThat(page.Prompts[0].Button).IsEqual("A");
			NoRowSpellsArtWithCharacters(page);

			// Day -1: the guest shows no day line. Before the Show's statement the title is the file's tag.
			screen.Schedule = (0xA1u, 0xD1u, -1);
			page = Fable3ScreenModel.Compose(Fable3ScreenModel.Schedule, new Dictionary<string, Probe>(), screen, Resolve)!.Value;
			AssertThat(page.Description).IsEqual(string.Empty);
			page = Fable3ScreenModel.Compose(Fable3ScreenModel.Schedule, new Dictionary<string, Probe>(), new Fable3ScreenState(), Resolve)!.Value;
			AssertThat(page.Title).IsEqual("<TEXT_GUI_KINGS_SCHEDULE_TITLE>");
		}
		finally
		{
			Fable3Elements.TagText = before;
		}
	}

	[TestCase]
	public void PromiseOffersAUntilSignedThenB()
	{
		var stated = new Dictionary<string, Probe>
		{
			["Promises.Player.GamerTag"] = Text("Hero"),
			["Promises.Promise.Promise"] = Text("I promise."),
			["Promises.Buttons.Buttons.A_Button.Group.Text"] = Text("Accept"),
		};
		var screen = new Fable3ScreenState { Promise = (0x1234u, false) };

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Promises, stated, screen, Resolve)!.Value;
		AssertThat(page.Prompts[0].Button).IsEqual("A");
		AssertThat(page.Prompts[0].Label).IsEqual("Accept");
		AssertThat(page.Description).IsEqual("I promise.");
		AssertThat(page.Rows[0].Line).IsEqual("Hero");
		AssertThat(page.Rows[0].Pips.HasValue).IsFalse();

		// A signs: the handler states `Promises.Buttons` 1 (TU3 0x827DAC34 / disc 0x827A3704).
		stated["Promises.Buttons"] = State(1);
		page = Fable3ScreenModel.Compose(Fable3ScreenModel.Promises, stated, screen, Resolve)!.Value;
		AssertThat(page.Prompts[0].Button).IsEqual("B");
		AssertThat(page.Rows[0].Line).IsEqual("Hero");
		AssertThat(page.Rows[0].Pips == (1, 1)).IsTrue();
		NoRowSpellsArtWithCharacters(page);
	}

	[TestCase]
	public void PromiseOpenedSignedOffersBBeforeAnySlide()
	{
		var stated = new Dictionary<string, Probe> { ["Promises.Player.GamerTag"] = Text("Hero") };
		var screen = new Fable3ScreenState { Promise = (0x1234u, true) };
		AssertThat(Fable3ScreenModel.Signed(stated, screen)).IsTrue();

		// The Show's own slide for an unsigned promise (2) wins over nothing stated.
		stated["Promises.Buttons"] = State(2);
		AssertThat(Fable3ScreenModel.Signed(stated, new Fable3ScreenState { Promise = (0x1234u, false) })).IsFalse();
	}

	/// <summary>
	/// A row's mark is drawn by the template (Row.Bar, Row.Pips), never spelt in its line: the
	/// title's fonts hold no box-drawing, block, shape or dingbat glyph (U+2500 and up), so one in
	/// a line is drawn as the fallback glyph.
	/// </summary>
	public static void NoRowSpellsArtWithCharacters(Fable3ScreenPage page)
	{
		foreach (Fable3ScreenPage.Row row in page.Rows)
		{
			foreach (char c in row.Line)
			{
				AssertThat(c >= '\u2500' && c <= '\u27BF').OverrideFailureMessage($"row '{row.Name}' spells art with U+{(int)c:X4}: '{row.Line}'").IsFalse();
			}
		}
	}

	[TestCase]
	public void UnknownRootHasNoPage()
	{
		AssertThat(Fable3ScreenModel.Compose("Options", new Dictionary<string, Probe>(), None, Resolve) is null).IsTrue();
	}
}
