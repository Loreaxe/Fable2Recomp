namespace Refii.Tests;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Text;
using Refii.Titles.Fable2;

/// <summary>
/// The co-op mod sync's words (Engine_UiCoopModWords): every refusal and notice is one of Fable 2's own
/// lines in every language, and the technical detail a refusal carries is passed through as given.
/// </summary>
[TestSuite]
public class CoopModWordsTests
{
	private static readonly string[] Checked = { "fr-fr", "de-de", "cs-cz", "ja-jp", "zh-cht" };

	[TestCase]
	public void EveryKeyIsStatedInFable2sTableAndDiffersFromEnglish()
	{
		var seen = new HashSet<string>();
		foreach (string key in Engine_UiCoopModWords.Keys)
		{
			AssertThat(seen.Add(key)).OverrideFailureMessage($"{key} listed twice").IsTrue();
			AssertThat(Engine_OwnWords.Fable2.Has(key)).OverrideFailureMessage(key).IsTrue();
			string english = Engine_OwnWords.Fable2.Get(key, "en-uk") ?? string.Empty;
			AssertThat(english.Length > 0).OverrideFailureMessage(key).IsTrue();
			foreach (string culture in Checked)
			{
				AssertThat(Engine_OwnWords.Fable2.Get(key, culture))
					.OverrideFailureMessage($"{key} [{culture}]").IsNotEqual(english);
			}
		}

		AssertThat(Engine_UiCoopModWords.Keys.Count).IsEqual(15);
	}

	[TestCase]
	public void TheEnglishLinesAreTheOnesTheSyncAlwaysSaid()
	{
		Engine_Title was = Engine_Title.Active;
		Engine_GameCulture.ResetForTests();
		try
		{
			Engine_Title.Active = Engine_Title.Fable2;
			AssertThat(Engine_UiCoopModWords.Unstated(otherIsHost: true)).IsEqual(
				"Co-op refused: the host's Refii does not state its mods, so it is an older build.\n\n" +
				"Both players need the same Refii build.");
			AssertThat(Engine_UiCoopModWords.Unstated(otherIsHost: false)).Contains("the joiner's Refii");
			AssertThat(Engine_UiCoopModWords.OldJoiner("the joiner lacks Rings")).IsEqual(
				"Co-op refused: your mods and the joiner's differ, and the joiner's Refii is an older build that " +
				"cannot take yours on.\n\nThe joiner lacks Rings.");
			AssertThat(Engine_UiCoopModWords.JoinerDeclined())
				.IsEqual("Co-op refused: the joiner declined to run your mods for this session.");
			AssertThat(Engine_UiCoopModWords.JoinerCannot("no data from the host for 30 seconds"))
				.IsEqual("Co-op refused: the joiner cannot run your mods.\n\nNo data from the host for 30 seconds.");
			AssertThat(Engine_UiCoopModWords.JoinerSlow(900.0))
				.IsEqual("Co-op refused: the joiner's mod sync did not finish in 15 minutes.");
			AssertThat(Engine_UiCoopModWords.HostBlocked("souls_combat is a hard mod")).IsEqual(
				"Co-op refused: the host's mods cannot be taken on for this session.\n\nSouls_combat is a hard mod.\n\n" +
				"A hard mod must be installed and enabled on both machines before the join.");
			AssertThat(Engine_UiCoopModWords.YouDeclined())
				.IsEqual("You declined the host's mods, so the session was left.\n\nJoin again to be asked again.");
			AssertThat(Engine_UiCoopModWords.SyncFailed("the host closed the download stream"))
				.IsEqual("Co-op refused: the host's mods could not be taken on.\n\nThe host closed the download stream.");
			AssertThat(Engine_UiCoopModWords.PartnerRunning()).IsEqual("Your partner is running your mods.");
			AssertThat(Engine_UiCoopModWords.PartnerReceiving()).IsEqual("Your partner is receiving your mods.");
			AssertThat(Engine_UiCoopModWords.SessionMounted(0, "Daring Pony"))
				.IsEqual("Your mods are suspended for this session.");
			AssertThat(Engine_UiCoopModWords.SessionMounted(2, "Daring Pony"))
				.IsEqual("Running Daring Pony's mods for this session.");
			AssertThat(Engine_UiCoopModWords.SessionMounted(2, string.Empty))
				.IsEqual("Running the host's mods for this session.");
		}
		finally
		{
			Engine_Title.Active = was;
			Engine_GameCulture.ResetForTests();
		}
	}

	[TestCase]
	public void TheRoleIsSaidThroughItsOwnKeyAndTheDetailIsKeptAsGiven()
	{
		AssertThat(Engine_UiCoopModWords.RoleKey(host: true)).IsEqual("REFII_F2_COOP_ROLE_HOST");
		AssertThat(Engine_UiCoopModWords.RoleKey(host: false)).IsEqual("REFII_F2_COOP_ROLE_JOINER");
		AssertThat(Engine_UiCoopModWords.Capitalised("the host")).IsEqual("The host");
		AssertThat(Engine_UiCoopModWords.Capitalised(string.Empty)).IsEqual(string.Empty);

		Engine_Title was = Engine_Title.Active;
		Engine_GameCulture.ResetForTests();
		try
		{
			Engine_Title.Active = Engine_Title.Fable2;
			Engine_GameCulture.Set("cs-cz");
			AssertThat(Engine_UiCoopModWords.Unstated(otherIsHost: true)).Contains("(hostitel)");
			AssertThat(Engine_UiCoopModWords.Unstated(otherIsHost: false)).Contains("(připojený hráč)");
			// A detail with braces of its own is an argument, never a format.
			AssertThat(Engine_UiCoopModWords.SyncFailed("the host refused {Rings}: {0}"))
				.IsEqual("Spolupráce odmítnuta: módy hostitele se nepodařilo převzít.\n\nThe host refused {Rings}: {0}.");
			AssertThat(Engine_UiCoopModWords.JoinerSlow(900.0)).Contains("15 min");
			AssertThat(Engine_UiCoopModWords.SessionMounted(1, "Daring Pony"))
				.IsEqual("V této relaci běží módy hráče Daring Pony.");
		}
		finally
		{
			Engine_Title.Active = was;
			Engine_GameCulture.ResetForTests();
		}
	}

	[TestCase]
	public void NoLineShowsAKeyOrAnUnfilledMarkerInAnyLanguage()
	{
		Engine_Title was = Engine_Title.Active;
		Engine_GameCulture.ResetForTests();
		try
		{
			Engine_Title.Active = Engine_Title.Fable2;
			foreach (string culture in Engine_OwnWords.Cultures)
			{
				Engine_GameCulture.Set(culture);
				string[] lines =
				{
					Engine_UiCoopModWords.Unstated(true), Engine_UiCoopModWords.Unstated(false),
					Engine_UiCoopModWords.OldJoiner("the joiner lacks Rings"), Engine_UiCoopModWords.JoinerDeclined(),
					Engine_UiCoopModWords.JoinerCannot("blocked"), Engine_UiCoopModWords.JoinerSlow(900.0),
					Engine_UiCoopModWords.HostBlocked("a hard mod"), Engine_UiCoopModWords.YouDeclined(),
					Engine_UiCoopModWords.SyncFailed("stalled"), Engine_UiCoopModWords.PartnerRunning(),
					Engine_UiCoopModWords.PartnerReceiving(), Engine_UiCoopModWords.SessionMounted(0, string.Empty),
					Engine_UiCoopModWords.SessionMounted(1, string.Empty),
					Engine_UiCoopModWords.SessionMounted(1, "Daring Pony"),
				};
				foreach (string line in lines)
				{
					AssertThat(line).OverrideFailureMessage($"[{culture}] {line}").NotContains("REFII_");
					AssertThat(line.Contains("{0}")).OverrideFailureMessage($"[{culture}] {line}").IsFalse();
				}
			}
		}
		finally
		{
			Engine_Title.Active = was;
			Engine_GameCulture.ResetForTests();
		}
	}
}
