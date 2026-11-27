namespace Refii.Tests.Titles;

using System;
using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Achievements;
using Refii.Titles.Fable3;

/// <summary>
/// Fable 3's achievements page as its rows read: the tally first, then each achievement in the title's
/// order with its gamerscore, ghosted while the hero shown has not earned it, its text and whether and when
/// it was unlocked under it; the tab strip only when a partner's view exists.
/// </summary>
[TestSuite]
public class Fable3AchievementsPageTests
{
	private static readonly AchievementDefinition[] Definitions =
	{
		new(10, 1, 20, 0, "Hero of Bowerstone", "Won the day.", "Win the day."),
		new(23, 2, 15, 0, "Secret", "Found it.", string.Empty),
		new(30, 3, 5, 0, string.Empty, string.Empty, string.Empty),
	};

	private static string Say(string key, object[] args) => args.Length == 0 ? "<" + key + ">" : "<" + key + ":" + string.Join(",", args) + ">";

	[TestCase]
	public void TheTallyComesFirstAndEachAchievementAfterIt()
	{
		var unlocked = new HashSet<uint> { 10 };
		var at = new DateTime(2026, 11, 2, 9, 0, 0);
		List<Fable3AchievementsModel.Entry> rows = Fable3AchievementsModel.Rows(Definitions, unlocked.Contains,
			id => id == 10 ? at : null, new[] { "This hero: 1 of 3", "20 of 40", "On this profile: 2 of 3" }, Say);

		AssertThat(rows.Count).IsEqual(4);
		AssertThat(rows[0].Name).IsEqual(Fable3AchievementsModel.SummaryRow);
		AssertThat(rows[0].Line).IsEqual("This hero: 1 of 3");
		AssertThat(rows[0].Description).IsEqual("20 of 40\nOn this profile: 2 of 3");
		AssertThat(rows[0].Ghosted).IsFalse();

		AssertThat(rows[1].Name).IsEqual("Achievement:10");
		AssertThat(rows[1].Line).IsEqual("Hero of Bowerstone    20 G");
		AssertThat(rows[1].Ghosted).IsFalse();
		AssertThat(rows[1].Description).IsEqual("Won the day.\n<REFII_ACH_UNLOCKED_ON:2026-11-02>");

		// Locked: the locked text when the title gives one, else the unlocked text; ghosted.
		AssertThat(rows[2].Ghosted).IsTrue();
		AssertThat(rows[2].Description).IsEqual("Found it.\n<REFII_ACH_LOCKED>");
		AssertThat(rows[3].Line).IsEqual("#30    5 G");
		AssertThat(rows[3].Description).IsEqual("<REFII_ACH_LOCKED>");
	}

	[TestCase]
	public void AnotherPlayersHeroHasNoDates()
	{
		var theirs = new HashSet<uint> { 10, 23 };
		List<Fable3AchievementsModel.Entry> rows = Fable3AchievementsModel.Rows(Definitions, theirs.Contains, _ => null,
			new[] { "Their hero" }, Say);
		AssertThat(rows[1].Description).IsEqual("Won the day.\n<REFII_ACH_UNLOCKED>");
		AssertThat(rows[2].Ghosted).IsFalse();
		AssertThat(rows[0].Description).IsEqual(string.Empty);
	}

	[TestCase]
	public void TheTabsAreThereOnlyWithAPartner()
	{
		AssertThat(Fable3AchievementsModel.Tabs(false, key => key)).IsNull();
		IReadOnlyList<string> tabs = Fable3AchievementsModel.Tabs(true, key => key)!;
		AssertThat(string.Join(",", tabs)).IsEqual("REFII_ACH_SHOW_OWN,REFII_ACH_SHOW_PARTNER");
	}
}
