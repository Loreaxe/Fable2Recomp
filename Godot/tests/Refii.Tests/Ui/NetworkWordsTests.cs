namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// The Multiplayer page's words (Engine_UiNetworkWords): every line is one of ours in every language, a
/// status keeps its English wire text and is translated where it is drawn, and no composed line shows a key.
/// </summary>
[TestSuite]
public class NetworkWordsTests
{
	/// <summary>The cultures a line must differ from English in, as CoopGamerCardTests asks of its rows.</summary>
	private static readonly string[] Checked = { "fr-fr", "de-de", "cs-cz", "ja-jp", "zh-cht" };

	/// <summary>
	/// Lines that ARE the English in a language: German and Czech say "online" / "offline" (as their console
	/// dashboards do), French and German spell "Version" as English does.
	/// </summary>
	private static readonly Dictionary<string, string[]> SameAsEnglish = new()
	{
		["REFII_MP_STATE_ONLINE"] = new[] { "de-de", "cs-cz" },
		["REFII_MP_STATE_OFFLINE"] = new[] { "de-de", "cs-cz" },
		["REFII_MP_MOD_VERSION"] = new[] { "fr-fr", "de-de" },
	};

	[TestCase]
	public void EveryKeyThePageSaysIsStatedInTheShellsTable()
	{
		var seen = new HashSet<string>();
		foreach (string key in Engine_UiNetworkWords.Keys)
		{
			AssertThat(seen.Add(key)).OverrideFailureMessage($"{key} listed twice").IsTrue();
			AssertThat(key.StartsWith("REFII_MP_", System.StringComparison.Ordinal)).OverrideFailureMessage(key).IsTrue();
			AssertThat(Engine_OwnWords.Shared.Has(key)).OverrideFailureMessage(key).IsTrue();
		}

		AssertThat(Engine_UiNetworkWords.Keys.Count).IsEqual(80);
	}

	[TestCase]
	public void EveryLineDiffersFromEnglishUnlessTheLanguageSaysTheSameWord()
	{
		foreach (string key in Engine_UiNetworkWords.Keys)
		{
			string english = Engine_OwnWords.Shared.Get(key, "en-uk") ?? string.Empty;
			AssertThat(english.Length > 0).OverrideFailureMessage(key).IsTrue();
			SameAsEnglish.TryGetValue(key, out string[]? same);
			foreach (string culture in Checked)
			{
				string? line = Engine_OwnWords.Shared.Get(key, culture);
				if (same is not null && System.Array.IndexOf(same, culture) >= 0)
				{
					AssertThat(line).OverrideFailureMessage($"{key} [{culture}]").IsEqual(english);
				}
				else
				{
					AssertThat(line).OverrideFailureMessage($"{key} [{culture}]").IsNotEqual(english);
				}
			}
		}
	}

	[TestCase]
	public void AStatusKeepsItsWireTextAndIsTranslatedWhereItIsDrawn()
	{
		// The wire text is what other builds read: English, in this order.
		AssertThat(Engine_UiNetworkWords.WireStatuses.Length).IsEqual(4);
		AssertThat(Engine_UiNetworkWords.WireStatuses[0]).IsEqual("Exploring");
		AssertThat(Engine_UiNetworkWords.WireStatuses[1]).IsEqual("Looking for co-op");
		AssertThat(Engine_UiNetworkWords.WireStatuses[2]).IsEqual("Trading");
		AssertThat(Engine_UiNetworkWords.WireStatuses[3]).IsEqual("Giving gifts");

		for (int i = 0; i < Engine_UiNetworkWords.WireStatuses.Length; ++i)
		{
			string wire = Engine_UiNetworkWords.WireStatuses[i];
			AssertThat(Engine_UiNetworkWords.WireStatusKey(wire)).IsEqual(Engine_UiNetworkWords.StatusKey(i));
			// In English the key's line IS the wire text, so an older build's friend reads the same words.
			AssertThat(Engine_OwnWords.Shared.Get(Engine_UiNetworkWords.StatusKey(i), "en-uk")).IsEqual(wire);
		}

		AssertThat(Engine_UiNetworkWords.WireStatusKey("Fishing")).IsNull();
		AssertThat(Engine_UiNetworkWords.WireStatusKey("exploring")).IsNull();
		AssertThat(Engine_UiNetworkWords.WireStatusKey(null)).IsNull();
		AssertThat(Engine_UiNetworkWords.StatusKey(-3)).IsEqual("REFII_MP_STATUS_EXPLORING");
		AssertThat(Engine_UiNetworkWords.StatusKey(99)).IsEqual("REFII_MP_STATUS_GIFTS");

		Engine_GameCulture.ResetForTests();
		try
		{
			Engine_GameCulture.Set("cs-cz");
			AssertThat(Engine_UiNetworkWords.DrawnStatus("Trading")).IsEqual("Obchoduji");
			AssertThat(Engine_UiNetworkWords.DrawnStatus("Fishing")).IsEqual("Fishing");
			AssertThat(Engine_UiNetworkWords.DrawnStatus(null)).IsEqual(string.Empty);
			AssertThat(Engine_UiNetworkWords.MyStatus(2)).IsEqual("Můj stav: Obchoduji");
			AssertThat(Engine_UiNetworkWords.StatusNow(0)).IsEqual("Tvůj stav je teď: Na průzkumu.");
			AssertThat(Engine_UiNetworkWords.FriendState(false, true, string.Empty, "Trading")).IsEqual("online — Obchoduji");
			AssertThat(Engine_UiNetworkWords.FriendState(true, true, "Fable II", "A mod's own status"))
				.IsEqual("ve hře Fable II — A mod's own status");
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
		}
	}

	[TestCase]
	public void TheStateOptionAndCreditKeysFollowWhatIsStated()
	{
		AssertThat(Engine_UiNetworkWords.FriendStateKey(true, true, string.Empty)).IsEqual("REFII_MP_STATE_IN_A_GAME");
		AssertThat(Engine_UiNetworkWords.FriendStateKey(true, true, "Fable II")).IsEqual("REFII_MP_STATE_IN_TITLE");
		AssertThat(Engine_UiNetworkWords.FriendStateKey(false, true, "Fable II")).IsEqual("REFII_MP_STATE_ONLINE");
		AssertThat(Engine_UiNetworkWords.FriendStateKey(false, false, string.Empty)).IsEqual("REFII_MP_STATE_OFFLINE");

		AssertThat(Engine_UiNetworkWords.JoinModeKey(0)).IsEqual("REFII_MP_JOIN_ANYONE");
		AssertThat(Engine_UiNetworkWords.JoinModeKey(1)).IsEqual("REFII_MP_JOIN_INVITES");
		AssertThat(Engine_UiNetworkWords.JoinModeKey(2)).IsEqual("REFII_MP_JOIN_NO_ONE");

		AssertThat(Engine_UiNetworkWords.ModCreditKey("1.2", "Miki")).IsEqual("REFII_MP_MOD_VERSION_BY");
		AssertThat(Engine_UiNetworkWords.ModCreditKey("1.2", string.Empty)).IsEqual("REFII_MP_MOD_VERSION");
		AssertThat(Engine_UiNetworkWords.ModCreditKey(string.Empty, "Miki")).IsEqual("REFII_MP_MOD_BY");
		AssertThat(Engine_UiNetworkWords.ModCreditKey(string.Empty, string.Empty)).IsNull();

		AssertThat(Engine_UiNetworkWords.GreetingCount).IsEqual(4);
		AssertThat(Engine_UiNetworkWords.GreetingKey(0)).IsEqual("REFII_MP_GREET_WAVE");
		AssertThat(Engine_UiNetworkWords.HeardKey(3)).IsEqual("REFII_MP_HEARD_WELL_MET");
	}

	[TestCase]
	public void TheEnglishLinesAreTheOnesThePageAlwaysSaid()
	{
		Engine_GameCulture.ResetForTests();
		try
		{
			AssertThat(Engine_UiNetworkWords.FriendState(true, true, string.Empty, string.Empty)).IsEqual("in a game");
			AssertThat(Engine_UiNetworkWords.FriendState(true, true, "Fable II", "Trading")).IsEqual("in Fable II — Trading");
			AssertThat(Engine_UiNetworkWords.FriendState(false, false, string.Empty, "Trading")).IsEqual("offline");
			AssertThat(Engine_UiNetworkWords.WhoCanJoin(1, hosting: true)).IsEqual("Who can join: Invites only");
			AssertThat(Engine_UiNetworkWords.WhoCanJoin(2, hosting: false))
				.IsEqual("Who can join: No one — no game is hosted yet");
			AssertThat(Engine_UiNetworkWords.MyStatus(1)).IsEqual("My status: Looking for co-op");
			AssertThat(Engine_UiNetworkWords.SendGreeting(0)).IsEqual("Send: Wave");
			AssertThat(Engine_UiNetworkWords.Heard(0, "Daring Pony")).IsEqual("Daring Pony waves to you.");
			AssertThat(Engine_UiNetworkWords.Heard(3, string.Empty)).IsEqual("A friend says well met, hero.");
			AssertThat(Engine_UiNetworkWords.DiscordName(string.Empty)).IsEqual("A Discord player");
			AssertThat(Engine_UiNetworkWords.DiscordName("Daring Pony")).IsEqual("Daring Pony");
			AssertThat(Engine_UiNetworkWords.RequestLabel("Daring Pony", selfNamed: true, "hello"))
				.IsEqual("\"Daring Pony\" (self-named) — \"hello\"");
			AssertThat(Engine_UiNetworkWords.RequestLabel("Brave Otter", selfNamed: false, string.Empty)).IsEqual("Brave Otter");
			AssertThat(Engine_UiNetworkWords.RequestPage(string.Empty))
				.IsEqual("Wants to be your friend.\n\nA accepts; the row below dismisses.");
			AssertThat(Engine_UiNetworkWords.RequestPage("hello")).IsEqual("\"hello\"\n\nA accepts; the row below dismisses.");
			AssertThat(Engine_UiNetworkWords.PublicGameRow("Brave Otter", 1, 2)).IsEqual("Brave Otter — 1/2 open");
			AssertThat(Engine_UiNetworkWords.PublicGamePage(0, 2, false)).IsEqual("A public game, full (2 of 2 taken).");
			AssertThat(Engine_UiNetworkWords.PublicGamePage(1, 2, true))
				.IsEqual("A public game, 1 of 2 open.\n\nIt needs an invitation.");
			AssertThat(Engine_UiNetworkWords.PublicGamePage(1, 2, false)).IsEqual("A public game, 1 of 2 open.\n\nA joins it.");
			AssertThat(Engine_UiNetworkWords.FriendPage(true, true, string.Empty, "Trading"))
				.IsEqual("\"Trading\"\n\nIn a game, and hosting.\n\nA asks them for an invitation.");
			AssertThat(Engine_UiNetworkWords.FriendPage(true, true, "Fable II", string.Empty))
				.IsEqual("In Fable II, and hosting.\n\nA asks them for an invitation.");
			AssertThat(Engine_UiNetworkWords.FriendPage(false, true, string.Empty, string.Empty))
				.IsEqual("Online, not in a game.\n\nA asks them for an invitation.");
			AssertThat(Engine_UiNetworkWords.FriendPage(false, false, string.Empty, string.Empty))
				.IsEqual("Offline.\n\nThey appear here again when they next play.");
			AssertThat(Engine_UiNetworkWords.ModPage("Adds rings.", "1.2", "Miki")).IsEqual("Adds rings.\n\nVersion 1.2 by Miki.");
			AssertThat(Engine_UiNetworkWords.ModPage(string.Empty, string.Empty, "Miki")).IsEqual("By Miki.");
			AssertThat(Engine_UiNetworkWords.ModPage("Adds rings.", string.Empty, string.Empty)).IsEqual("Adds rings.");
			AssertThat(Engine_UiNetworkWords.ModPage(string.Empty, "1.2", string.Empty)).IsEqual("Version 1.2.");
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
		}
	}

	[TestCase]
	public void NoComposedLineShowsAKeyOrAnUnfilledMarkerInAnyLanguage()
	{
		Engine_GameCulture.ResetForTests();
		try
		{
			foreach (string culture in Engine_OwnWords.Cultures)
			{
				Engine_GameCulture.Set(culture);
				var lines = new List<string>();
				foreach (bool joinable in new[] { false, true })
				{
					foreach (bool online in new[] { false, true })
					{
						foreach (string title in new[] { string.Empty, "Fable II" })
						{
							foreach (string status in new[] { string.Empty, "Trading", "Fishing" })
							{
								lines.Add(Engine_UiNetworkWords.FriendState(joinable, online, title, status));
								lines.Add(Engine_UiNetworkWords.FriendPage(joinable, online, title, status));
							}
						}
					}
				}

				for (int i = 0; i < 4; ++i)
				{
					lines.Add(Engine_UiNetworkWords.MyStatus(i));
					lines.Add(Engine_UiNetworkWords.StatusNow(i));
					lines.Add(Engine_UiNetworkWords.SendGreeting(i));
					lines.Add(Engine_UiNetworkWords.Heard(i, "Daring Pony"));
					lines.Add(Engine_UiNetworkWords.Heard(i, string.Empty));
					lines.Add(Engine_UiNetworkWords.WhoCanJoin(i, hosting: true));
					lines.Add(Engine_UiNetworkWords.WhoCanJoin(i, hosting: false));
				}

				lines.Add(Engine_UiNetworkWords.DiscordName(string.Empty));
				lines.Add(Engine_UiNetworkWords.RequestLabel("Daring Pony", selfNamed: true, "hello"));
				lines.Add(Engine_UiNetworkWords.RequestPage(string.Empty));
				lines.Add(Engine_UiNetworkWords.RequestPage("hello"));
				lines.Add(Engine_UiNetworkWords.PublicGameRow("Brave Otter", 1, 2));
				lines.Add(Engine_UiNetworkWords.PublicGamePage(0, 2, false));
				lines.Add(Engine_UiNetworkWords.PublicGamePage(1, 2, true));
				lines.Add(Engine_UiNetworkWords.PublicGamePage(1, 2, false));
				lines.Add(Engine_UiNetworkWords.ModPage("Adds rings.", "1.2", "Miki"));
				lines.Add(Engine_UiNetworkWords.ModPage(string.Empty, "1.2", string.Empty));
				lines.Add(Engine_UiNetworkWords.ModPage(string.Empty, string.Empty, "Miki"));

				// Every key alone, filled as the page fills it (at most three parts).
				foreach (string key in Engine_UiNetworkWords.Keys)
				{
					lines.Add(Engine_OwnWords.Say(key, "Daring Pony", "1", "2"));
				}

				foreach (string line in lines)
				{
					AssertThat(line).OverrideFailureMessage($"[{culture}] {line}").NotContains("REFII_");
					AssertThat(line.Contains("{0}") || line.Contains("{1}") || line.Contains("{2}"))
						.OverrideFailureMessage($"[{culture}] {line}").IsFalse();
				}
			}
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
		}
	}
}
