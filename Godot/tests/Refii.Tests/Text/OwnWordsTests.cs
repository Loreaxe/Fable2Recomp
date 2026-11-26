namespace Refii.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Text;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>Our own words (Engine_OwnWords): every line in every language, never a game key, and the rows routed to them.</summary>
[TestSuite]
public class OwnWordsTests
{
	private static IEnumerable<(string Table, Engine_OwnWords.Table Words)> Tables() => new[]
	{
		("Shared", Engine_OwnWords.Shared), ("Fable2", Engine_OwnWords.Fable2), ("Fable3", Engine_OwnWords.Fable3),
	};

	private static readonly Regex Marker = new(@"\{\d+\}", RegexOptions.Compiled);

	[TestCase]
	public void EveryKeyHasALineInEveryLanguageWithTheSameMarkers()
	{
		var missing = new List<string>();
		foreach ((string name, Engine_OwnWords.Table table) in Tables())
		{
			foreach (string key in table.Keys)
			{
				string english = table.Get(key, "en-uk") ?? string.Empty;
				string[] wanted = Marker.Matches(english).Select(m => m.Value).OrderBy(v => v).ToArray();
				foreach (string culture in Engine_OwnWords.Cultures)
				{
					string? line = table.Get(key, culture);
					if (string.IsNullOrWhiteSpace(line))
					{
						missing.Add($"{name}.{key} [{culture}] empty");
						continue;
					}

					string[] said = Marker.Matches(line).Select(m => m.Value).OrderBy(v => v).ToArray();
					if (!said.SequenceEqual(wanted))
					{
						missing.Add($"{name}.{key} [{culture}] markers {string.Join(",", said)} vs {string.Join(",", wanted)}");
					}
				}
			}
		}

		AssertThat(string.Join("\n", missing)).IsEqual(string.Empty);
	}

	[TestCase]
	public void NoKeyIsTheGamesAndNoneIsStatedTwice()
	{
		var seen = new HashSet<string>();
		foreach ((string _, Engine_OwnWords.Table table) in Tables())
		{
			foreach (string key in table.Keys)
			{
				AssertThat(key.StartsWith(Engine_OwnWords.KeyPrefix, System.StringComparison.Ordinal)).IsTrue();
				AssertThat(seen.Add(key)).IsTrue();
			}
		}

		// A game key is never answered here, so the book's line always stands.
		AssertThat(Engine_OwnWords.Find("GUI_SCREEN_PAUSE_HENCHMAN_QUIT", "cs-cz")).IsNull();
	}

	[TestCase]
	public void ALanguageThisTableDoesNotStateReadsEnglish()
	{
		Engine_Title was = Engine_Title.Active;
		try
		{
			Engine_Title.Active = Engine_Title.Fable2;
			AssertThat(Engine_OwnWords.Find("REFII_PLAYER_2", "cs-cz")).IsEqual("Hráč 2");
			AssertThat(Engine_OwnWords.Find("REFII_PLAYER_2", "cz")).IsEqual("Hráč 2");
			AssertThat(Engine_OwnWords.Find("REFII_PLAYER_2", "sk-sk")).IsEqual("Hráč 2");
			AssertThat(Engine_OwnWords.Find("REFII_PLAYER_2", "de-at")).IsEqual("Spieler 2");
			AssertThat(Engine_OwnWords.Find("REFII_PLAYER_2", "zh-chs")).IsEqual("Player 2");
			AssertThat(Engine_OwnWords.Find("REFII_PLAYER_2", "en-us")).IsEqual("Player 2");
			AssertThat(Engine_OwnWords.Find("REFII_F2_SAVE_QUIT_DESKTOP", "pl-pl")).IsEqual("Zapisz i wyjdź do pulpitu");
			AssertThat(Engine_OwnWords.Find("REFII_NOT_A_KEY", "pl-pl")).IsNull();

			// Fable 2's own words are its own: Fable 3 does not answer them.
			Engine_Title.Active = Engine_Title.Fable3;
			AssertThat(Engine_OwnWords.Find("REFII_F2_SAVE_QUIT_DESKTOP", "pl-pl")).IsNull();
			AssertThat(Engine_OwnWords.Find("REFII_BACK", "pl-pl")).IsEqual("Wstecz");
		}
		finally
		{
			Engine_Title.Active = was;
		}
	}

	[TestCase]
	public void TheRowsRoutedToOurWordsHoldNoEnglish()
	{
		Engine_Title was = Engine_Title.Active;
		try
		{
			Engine_Title.Active = Engine_Title.Fable2;
			Engine_UiTemplateSet game = Engine_UiTemplates.ComposeGame();
			var routed = new HashSet<string>
			{
				"SaveAndQuitToDesktop", "QuitToDesktopDontSave", "Achievements", "Multiplayer", "KickConfirm", "Hosting",
				"AddFriend", "MyCode", "Continue", "Back", "ModSettings", "QuitToDesktop", "DynamicSkyAndWeather",
				Engine_UiAchievementPage.FolderRowName, "Friends", "Friend Requests", "Invitations", "Games",
			};

			var english = new List<string>();
			int found = 0;
			void Walk(string menu, IEnumerable<Engine_UiMenuEntry> entries)
			{
				foreach (Engine_UiMenuEntry entry in entries)
				{
					if (entry.HostOnly && routed.Contains(entry.Name) && entry.Tag.Length != 0)
					{
						++found;
						if (Engine_OwnWords.Find(entry.Tag, "en-uk") is null)
						{
							english.Add($"{menu}/{entry.Name} '{entry.Tag}'");
						}
					}

					Walk(menu, entry.Children);
				}
			}

			foreach ((string name, Engine_UiMenuTemplate menu) in game.Menus)
			{
				Walk(name, menu.Entries);
				if (name == "mods-root" || menu.Title == "REFII_ACTIVE_MODS")
				{
					AssertThat(Engine_OwnWords.Find(menu.Title, "en-uk")).IsNotNull();
				}
			}

			AssertThat(string.Join("\n", english)).IsEqual(string.Empty);
			AssertThat(found).IsGreater(12);

			// The multiplayer folders keep their English names as identities; only their labels are ours.
			foreach ((string _, string title, string tag) in Engine_UiNetworkSource.Pages)
			{
				AssertThat(Engine_UiNetworkSource.IsGroupName(title)).IsTrue();
				AssertThat(Engine_OwnWords.Find(tag, "pl-pl")).IsNotNull();
			}

			// The mods-in-this-save page reads the language in force.
			AssertThat(Engine_OwnWords.Find("REFII_MODS_IN_SAVE", "cs-cz")).IsEqual("Módy v této uložené pozici");
			AssertThat(Engine_OwnWords.Find("REFII_MP_NO_GAMES", "de-de")).IsEqual("Keine Spiele");

			// The co-op notices and the mode row are keys (the mode row and the owner tag are Godot nodes, which
			// the test host does not load: their keys are named here as their files state them).
			foreach (string key in new[]
			{
				Engine_UiCoopNotices.LocalJoined, Engine_UiCoopNotices.LocalLeft, Engine_UiCoopNotices.Progression,
				Engine_UiCoopNotices.PressStart, Engine_UiCoopModeRow.PendingNote, "REFII_F2_COOP_MODE_SHARED",
				"REFII_F2_COOP_MODE_SIDE", "REFII_F2_COOP_MODE_STACKED", "REFII_PLAYER_1", "REFII_PLAYER_2",
			})
			{
				AssertThat(Engine_OwnWords.Find(key, "en-uk")).IsNotNull();
				AssertThat(Engine_UiCoopNotices.IsTextKey(key)).IsTrue();
			}

			AssertThat(Engine_UiCoopNotices.HostJoined("Sparrow")).IsEqual(Engine_OwnWords.Say("REFII_F2_COOP_HOST_JOINED", "Sparrow"));
		}
		finally
		{
			Engine_Title.Active = was;
		}
	}
}
