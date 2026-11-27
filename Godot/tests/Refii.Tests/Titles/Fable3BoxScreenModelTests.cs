namespace Refii.Tests.Titles;

using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;
using Probe = Refii.Titles.Fable3.Fable3ScreenModel.Probe;

/// <summary>
/// The box-manager screens past the first five, from a stated set: the Shows' and ctors' statements,
/// Godot's cursors and working values, opener arguments, row records and queries of the
/// join/visibility, chat, bed, wedding, partners, treasury, divorce and save-menu classes
/// (Fable3BoxScreenModel's citations), the screen statements' per-box record, and the kind-to-root
/// routing.
/// </summary>
[TestSuite]
public class Fable3BoxScreenModelTests
{
	private static string Resolve(string tag) => "<" + tag + ">";

	private static Probe Text(string text) => new(null, false, text, null);

	[TestCase]
	public void KindsRouteToTheirElementRoots()
	{
		AssertThat(Fable3ScreenModel.RootOf("screen:join")).IsEqual("OrbEmote");
		AssertThat(Fable3ScreenModel.RootOf("screen:visibility")).IsEqual("Visibilities");
		AssertThat(Fable3ScreenModel.RootOf("screen:proposal")).IsEqual("InfoBox.MarriageProposal");
		AssertThat(Fable3ScreenModel.RootOf("screen:herostatus")).IsEqual("HeroStatus");
		AssertThat(Fable3ScreenModel.RootOf("screen:options")).IsNull();
	}

	[TestCase]
	public void JoinOptionsStartOnTheStatedSettingThenFollowGodotsCursor()
	{
		System.Func<uint, string?> before = Fable3Elements.TagText;
		Fable3Elements.TagText = tag => tag == 0x71u ? "Invite Only" : null;
		try
		{
			var screen = new Fable3ScreenState { Online = (18, 2, 0x71u) };

			Fable3ScreenPage page = Fable3ScreenModel.Compose("OrbEmote", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
			AssertThat(page.Title).IsEqual("<TEXT_GUI_JOIN_OPTIONS_TITLE>");
			AssertThat(page.Rows.Count).IsEqual(3);
			AssertThat(page.Selected).IsEqual(1);
			AssertThat(page.Rows[1].Line).IsEqual("<TEXT_GUI_JOIN_OPTIONS_INVITE_TITLE>");
			AssertThat(page.Rows[1].Pips == (1, 1)).IsTrue();
			AssertThat(page.Rows[0].Pips.HasValue).IsFalse();
			Fable3ScreenModelTests.NoRowSpellsArtWithCharacters(page);
			AssertThat(page.Rows[2].Description).IsEqual("TEXT_GUI_JOIN_OPTIONS_OFF_DESC");
			AssertThat(page.Description).IsEqual("Invite Only");
			AssertThat(page.Cursor).IsTrue();

			screen.Cursor = 2;
			page = Fable3ScreenModel.Compose("OrbEmote", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
			AssertThat(page.Selected).IsEqual(2);
			AssertThat(page.Rows[1].Pips == (1, 1)).IsTrue();
		}
		finally
		{
			Fable3Elements.TagText = before;
		}
	}

	[TestCase]
	public void ChatChannelStartsOnTheStatedSavedMuteThenFollowsGodotsValue()
	{
		System.Func<uint, string?> before = Fable3Elements.TagText;
		Fable3Elements.TagText = tag => tag switch { 0x51u => "Muted", 0x61u => "Nobody hears you.", 0x62u => "Everyone hears you.", _ => null };
		try
		{
			var screen = new Fable3ScreenState { Chat = (true, 0x51u, 0x61u, 0x62u) };
			var none = new Dictionary<string, Probe>();

			Fable3ScreenPage page = Fable3ScreenModel.Compose("ChatChannel", none, screen, Resolve)!.Value;
			AssertThat(page.Rows[0].Line).Contains("<TEXT_GUI_AUDIO_CHAT_MUTED>");
			AssertThat(page.Rows[0].Line).StartsWith("<REFII_F3_CHAT_MUTE>");
			AssertThat(page.Title).IsEqual("<REFII_F3_CHAT_CHANNEL>");
			AssertThat(page.Description).IsEqual("<REFII_F3_CHAT_CURRENT> Muted\nNobody hears you.");

			// The player's own toggle wins over the saved mute, and the description follows it.
			screen.Muted = false;
			page = Fable3ScreenModel.Compose("ChatChannel", none, screen, Resolve)!.Value;
			AssertThat(page.Rows[0].Line).Contains("<TEXT_GUI_AUDIO_CHAT_AUDIBLE>");
			AssertThat(page.Description).IsEqual("<REFII_F3_CHAT_CURRENT> Muted\nEveryone hears you.");
			AssertThat(Fable3BoxScreenModel.ChatMuted(screen)).IsFalse();
		}
		finally
		{
			Fable3Elements.TagText = before;
		}
	}

	[TestCase]
	public void FriendsChannelSaysOurWordsThroughTheResolver()
	{
		Fable3ScreenPage page = Fable3ScreenModel.Compose("FriendsChannel", new Dictionary<string, Probe>(),
			new Fable3ScreenState { Cursor = 1, InviteAllowed = false }, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("<REFII_F3_FRIENDS_CHANNEL>");
		AssertThat(page.Rows[0].Line).IsEqual("<REFII_MP_FRIENDS>");
		AssertThat(page.Rows[1].Line).IsEqual("<REFII_F3_JOIN_PARTY>");
		AssertThat(page.Rows[2].Line).IsEqual("<REFII_F3_INVITE_PARTY>");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Rows[0].Ghosted).IsTrue();
		AssertThat(page.Rows[1].Ghosted).IsFalse();
	}

	[TestCase]
	public void BedMenuIsItsShowsTagsWithGodotsRowAndOptions()
	{
		System.Func<uint, string?> before = Fable3Elements.TagText;
		Fable3Elements.TagText = tag => tag switch
		{
			0x10u => "Sleep", 0x11u => "Sex", 0x20u => "Until morning", 0x21u => "Until midday", 0x22u => "Until evening",
			0x31u => "Sleep until midday", 0x40u => "Protected", 0x50u => "Safe", _ => null,
		};
		try
		{
			var screen = new Fable3ScreenState
			{
				Opened = new[] { 0, 0, 0 },
				Bed = new Fable3ScreenState.BedTags(0x10u, 0x11u, new[] { 0x20u, 0x21u, 0x22u }, new[] { 0x30u, 0x31u, 0x32u },
					new[] { 0x40u, 0x41u }, new[] { 0x50u, 0x51u }),
				BedSleep = 1,
			};

			Fable3ScreenPage page = Fable3ScreenModel.Compose("BedMenu", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
			AssertThat(page.Rows.Count).IsEqual(2);
			AssertThat(page.Selected).IsEqual(0);
			AssertThat(page.Rows[0].Line).IsEqual("Sleep    Until midday");
			AssertThat(page.Rows[0].Description).IsEqual("Sleep until midday");
			AssertThat(page.Rows[0].Ghosted).IsFalse();
			AssertThat(page.Rows[1].Line).IsEqual("Sex    Protected");
			AssertThat(page.Rows[1].Description).IsEqual("Safe");
			AssertThat(page.Rows[1].Ghosted).IsTrue();
			Fable3ScreenModelTests.NoRowSpellsArtWithCharacters(page);
			// The option is a choice mark between drawn arrows, never "< x >", which the title's text
			// markup reads as a tag and draws as nothing (int7d D2).
			AssertThat(page.Rows[0].Choice).IsEqual("Until midday");
			AssertThat(page.Rows[1].Choice).IsEqual("Protected");
			AssertThat(page.Rows.All(row => row.Line.IndexOfAny(new[] { '<', '>' }) < 0)).IsTrue();

			screen.Opened = new[] { 1, 0, 1 };
			screen.Cursor = 1;
			page = Fable3ScreenModel.Compose("BedMenu", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
			AssertThat(page.Rows[1].Ghosted).IsFalse();
			AssertThat(page.Selected).IsEqual(1);
		}
		finally
		{
			Fable3Elements.TagText = before;
		}
	}

	[TestCase]
	public void WeddingPickerTierIsTheFieldAndUnaffordableTiersAreGhosted()
	{
		var stated = new Dictionary<string, Probe>
		{
			["WeddingPicker.Title"] = Text("Brightwall"),
			["WeddingPicker.Details.WeddingDetails0.Group.Title"] = Text("Poor"),
			["WeddingPicker.Details.WeddingDetails1.Group.Title"] = Text("Standard"),
			["WeddingPicker.Details.WeddingDetails1.Group.Cost"] = Text("2000"),
		};
		var screen = new Fable3ScreenState { Tier = 1, Opened = new[] { 1, 0, 0 } };

		Fable3ScreenPage page = Fable3ScreenModel.Compose("WeddingPicker", stated, screen, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("Brightwall");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Rows[1].Line).IsEqual("Standard    2000");
		AssertThat(page.Rows[0].Ghosted).IsFalse();
		AssertThat(page.Rows[1].Ghosted).IsTrue();
	}

	[TestCase]
	public void PartnersRowsAreTheRowWritersRecords()
	{
		const string row0 = "CoopPartnersInfoBox.Details.PartnerDetails0";
		const string row1 = "CoopPartnersInfoBox.Details.PartnerDetails1";
		var stated = new Dictionary<string, Probe>
		{
			[row0 + ".GamerTag"] = Text("Alice"),
			[row0 + ".NumChildren"] = Text("2"),
			[row1 + ".GamerTag"] = Text("Bob"),
			[row1 + ".NumChildren"] = Text("stale"),
		};
		var screen = new Fable3ScreenState { Cursor = 1 };
		screen.PartnerRows[row0] = (true, true, false);
		screen.PartnerRows[row1] = (false, false, true);

		Fable3ScreenPage page = Fable3ScreenModel.Compose("CoopPartnersInfoBox", stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("Alice    2    Online");
		AssertThat(page.Rows[1].Line).IsEqual("Bob    Offline");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Prompts[0].Button).IsEqual("A");
	}

	[TestCase]
	public void TreasuryQueryStandsOverThePage()
	{
		var stated = new Dictionary<string, Probe>
		{
			["TreasureAccounts.Personal.Personal Balance Value"] = Text("1200"),
			["TreasureAccounts.QueryBox.Group.QueryBox.Text"] = Text("Transfer 100?"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("TreasureAccounts", stated, new Fable3ScreenState { RootState = 5 }, Resolve)!.Value;
		AssertThat(page.Query).IsNotNull();
		AssertThat(page.Query!.Text).IsEqual("Transfer 100?");
		AssertThat(page.Rows[0].Line).IsEqual("Personal Balance    1200");
	}

	/// <summary>
	/// The treasury draws what its file's root slide for the page shows: 2 both balances and the
	/// transactions with the X flip, 3 the personal balance and the transactions (the Treasury group
	/// inactive), 4 both balances and the transfer row under the cursor with its description, no
	/// transactions; the query only on 5, never a query text left from an earlier one on 4.
	/// </summary>
	[TestCase]
	public void TreasuryDrawsWhatItsPagesSlideShows()
	{
		var stated = new Dictionary<string, Probe>
		{
			["TreasureAccounts.Personal.Personal Balance Value"] = Text("1200"),
			["TreasureAccounts.Treasury.Treasury Balance Value"] = Text("9000"),
			["TreasureAccounts.Transactions.Transaction Name.Transaction 1"] = Text("Buying Items"),
			["TreasureAccounts.Transactions.Transaction Value.Transaction 1"] = Text("-38"),
			["TreasureAccounts.Transactions.Transaction Name.Transaction 2"] = Text("Testers"),
			["TreasureAccounts.Transactions.Transaction Value.Transaction 2"] = Text("-94"),
			["TreasureAccounts.MoneyTransfer.TransferFundsComponent.Text.Text"] = Text("Transfer to Treasury"),
			["TreasureAccounts.MoneyTransfer.TransferFundsComponent.Value.Value"] = Text("100"),
			["TreasureAccounts.MoneyTransfer.DescriptionText"] = Text("Gain followers."),
			["TreasureAccounts.Buttons.X_Button.Group.Text"] = Text("Withdraw"),
			["TreasureAccounts.QueryBox.Group.QueryBox.Text"] = Text("Transfer 100?"),
		};

		Fable3ScreenPage Page(int root) => Fable3ScreenModel.Compose("TreasureAccounts", stated, new Fable3ScreenState { RootState = root }, Resolve)!.Value;

		Fable3ScreenPage personal = Page(3);
		AssertThat(personal.Rows.Select(row => row.Name).ToArray()).IsEqual(new[] { "Personal", "Transaction1", "Transaction2" });
		AssertThat(personal.Selected).IsEqual(-1);
		AssertThat(personal.Cursor).IsFalse();
		AssertThat(personal.Query).IsNull();
		AssertThat(personal.Prompts.Any(prompt => prompt.Button == "X")).IsFalse();

		Fable3ScreenPage both = Page(2);
		AssertThat(both.Rows.Select(row => row.Name).ToArray()).IsEqual(new[] { "Personal", "Treasury", "Transaction1", "Transaction2" });
		AssertThat(both.Selected).IsEqual(-1);
		AssertThat(both.Prompts.Any(prompt => prompt.Button == "X" && prompt.Label == "Withdraw")).IsTrue();
		AssertThat(both.Description).IsEqual(string.Empty);

		Fable3ScreenPage transfer = Page(4);
		AssertThat(transfer.Rows.Select(row => row.Name).ToArray()).IsEqual(new[] { "Personal", "Treasury", "Transfer" });
		AssertThat(transfer.Selected).IsEqual(2);
		AssertThat(transfer.Rows[2].Line).IsEqual("Transfer to Treasury    100");
		AssertThat(transfer.Rows[2].Choice).IsEqual("100");
		AssertThat(transfer.Description).IsEqual("Gain followers.");
		AssertThat(transfer.Query).IsNull();

		AssertThat(Page(5).Query!.Text).IsEqual("Transfer 100?");
	}

	/// <summary>
	/// The divorce box draws the page its Show dispatches on its ctor's stated mode: an asking page
	/// (0 / 3) both names, the reason picker under the cursor, the assets and the children, with A
	/// and B; a told page (1 / 4) the partner, the partner's reason and both shares, A only; a
	/// confirmed page (2 / 5) the partner and one share. A path another page left behind is not read.
	/// </summary>
	[TestCase]
	public void DivorceDrawsThePageItsShowDispatched()
	{
		const string fore = "DivorceMessageBox.Foreground.";
		var stated = new Dictionary<string, Probe>
		{
			[fore + "Title"] = Text("Divorce"),
			[fore + "ActionDescription"] = Text("Choose a reason"),
			[fore + "GamerTagPics.Pic1.Tag"] = Text("Hero"),
			[fore + "GamerTagPics.Pic2.Tag"] = Text("Spouse"),
			[fore + "ReasonsPicker.Reasons.ReasonText"] = Text("Snoring"),
			[fore + "ReasonReport.ReasonReportTitle"] = Text("Spouse says"),
			[fore + "ReasonReport.ReasonText"] = Text("Chickens"),
			[fore + "JointAssetsBreakdown.Amount1"] = Text("500"),
			[fore + "JointAssetsBreakdown.Tag2"] = Text("Spouse's share"),
			[fore + "JointAssetsBreakdown.Amount2"] = Text("250"),
			[fore + "Kids"] = Text("2 children orphaned"),
		};

		Fable3ScreenPage asking = Fable3ScreenModel.Compose("DivorceMessageBox", stated, new Fable3ScreenState { Divorce = (1, 3, 0, 0) }, Resolve)!.Value;
		AssertThat(asking.Rows[0].Line).IsEqual("Hero    Spouse");
		AssertThat(asking.Rows[asking.Selected].Line).IsEqual("Snoring");
		AssertThat(asking.Rows[asking.Selected].Choice).IsEqual("Snoring");
		AssertThat(asking.Prompts.Count).IsEqual(2);
		AssertThat(asking.Cursor).IsTrue();
		AssertThat(asking.Rows[^1].Line).IsEqual("2 children orphaned");

		Fable3ScreenPage told = Fable3ScreenModel.Compose("DivorceMessageBox", stated, new Fable3ScreenState { Divorce = (1, 1, 0, 0) }, Resolve)!.Value;
		AssertThat(told.Rows[0].Line).IsEqual("Spouse");
		AssertThat(told.Rows[1].Line).IsEqual("Spouse says    Chickens");
		AssertThat(told.Rows[^1].Line).IsEqual("Spouse's share    250");
		AssertThat(told.Selected).IsEqual(-1);
		AssertThat(told.Prompts.Count).IsEqual(1);

		Fable3ScreenPage confirmed = Fable3ScreenModel.Compose("DivorceMessageBox", stated, new Fable3ScreenState { Divorce = (1, 5, 0, 0) }, Resolve)!.Value;
		AssertThat(confirmed.Rows.Count).IsEqual(2);
		AssertThat(confirmed.Rows[1].Line).IsEqual("500");
		AssertThat(Fable3BoxScreenModel.DivorcePageOf(4)).IsEqual(Fable3BoxScreenModel.DivorceKind.Told);
	}

	/// <summary>
	/// Every box screen stands in its own document's frame, and the frame's slide follows the page
	/// the screen's calls stated: the divorce page (asking 2, told 3, confirmed 4), the treasury
	/// page, the stats page, the bed row, the join choice.
	/// </summary>
	[TestCase]
	public void EachBoxScreenStandsInItsOwnDocumentsFrame()
	{
		foreach (string kind in new[]
		{
			"screen:herostatus", "screen:stats", "screen:promises", "screen:quests", "screen:schedule", "screen:join",
			"screen:visibility", "screen:friends", "screen:chat", "screen:treasury", "screen:bed", "screen:food",
			"screen:wedding", "screen:partners", "screen:proposal", "screen:divorce",
		})
		{
			AssertThat(Fable3ScreenFrames.FrameOf(Fable3ScreenModel.RootOf(kind))).IsNotNull();
		}

		AssertThat(Fable3ScreenFrames.FrameOf("SaveMenu")).IsNull();
		AssertThat(Fable3ScreenFrames.FrameOf("BedMenu")!.Value.Document).IsEqual("bedmenubox");
		AssertThat(Fable3ScreenFrames.RootSlide("DivorceMessageBox", new Fable3ScreenState { Divorce = (1, 3, 0, 0) })).IsEqual(2);
		AssertThat(Fable3ScreenFrames.RootSlide("DivorceMessageBox", new Fable3ScreenState { Divorce = (1, 4, 0, 0) })).IsEqual(3);
		AssertThat(Fable3ScreenFrames.RootSlide("DivorceMessageBox", new Fable3ScreenState { Divorce = (1, 2, 0, 0) })).IsEqual(4);
		AssertThat(Fable3ScreenFrames.RootSlide("TreasureAccounts", new Fable3ScreenState { RootState = 4 })).IsEqual(4);
		AssertThat(Fable3ScreenFrames.RootSlide("StatsBox", new Fable3ScreenState { StatsPage = 3 })).IsEqual(3);
		AssertThat(Fable3ScreenFrames.RootSlide("BedMenu", new Fable3ScreenState { Cursor = 1 })).IsEqual(2);
		AssertThat(Fable3ScreenFrames.RootSlide("OrbEmote", new Fable3ScreenState { Online = (18, 3, 0u) })).IsEqual(3);
		AssertThat(Fable3ScreenFrames.RootSlide("OrbEmote", new Fable3ScreenState { Online = (18, 3, 0u), Cursor = 0 })).IsEqual(1);
		AssertThat(Fable3ScreenFrames.RootSlide("QuestSummary", Fable3ScreenState.Empty)).IsEqual(1);
	}

	[TestCase]
	public void SaveMenuRowAndGhostingAreItsOpensStatements()
	{
		// The guest's order (TU3 sub_829B5480 / sub_829B5540 step the row by one): Save 1,
		// Save and Quit 2, Quit 3, Character Management 4.
		var screen = new Fable3ScreenState { SaveRow = 4, SaveAllowed = false };
		Fable3ScreenPage page = Fable3ScreenModel.Compose("SaveMenu", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
		AssertThat(string.Join(",", page.Rows.Select(row => row.Name))).IsEqual("Save,SaveAndQuit,Quit,CharacterManagement");
		AssertThat(page.Selected).IsEqual(3);
		AssertThat(page.Rows[0].Ghosted).IsTrue();
		AssertThat(page.Rows[1].Ghosted).IsTrue();
		AssertThat(page.Rows[3].Ghosted).IsFalse();

		screen.SaveRow = 3;
		screen.SaveAllowed = true;
		page = Fable3ScreenModel.Compose("SaveMenu", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
		AssertThat(page.Selected).IsEqual(2);
		AssertThat(page.Rows[page.Selected].Name).IsEqual("Quit");
		AssertThat(page.Rows[0].Ghosted).IsFalse();

		// Any other row is the highlight's Quit (TU3 0x829B53A4).
		screen.SaveRow = 0;
		page = Fable3ScreenModel.Compose("SaveMenu", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
		AssertThat(page.Rows[page.Selected].Name).IsEqual("Quit");
	}

	[TestCase]
	public void ScreenStatementsKeepEachBoxsRecordUntilItCloses()
	{
		Fable3ScreenStatements.Log = _ => { };
		Fable3ScreenStatements.Clear();
		Fable3ScreenStatements.Take(Call("@Screen.Opened", S("40001000"), S("bed"), N(1f), N(0f), N(1f)));
		Fable3ScreenStatements.Take(Call("@Screen.Bed", S("40001000"), S("10"), S("11"), S("20"), S("21"), S("22"), S("30"), S("31"),
			S("32"), S("40"), S("41"), S("50"), S("51")));
		Fable3ScreenStatements.Take(Call("@Screen.Online", S("40002000"), N(19f), N(3f), S("77")));
		Fable3ScreenStatements.Take(Call("@Screen.Friends", S("40003000"), B(false)));
		Fable3ScreenStatements.Take(Call("@Screen.Divorce", S("40004000"), N(2f), N(3f), N(1f), N(250f)));

		Fable3ScreenState state = Fable3ScreenStatements.Of(0x40001000u);
		AssertThat(state.OpenedFlag(0, false)).IsTrue();
		AssertThat(state.OpenedFlag(1, true)).IsFalse();
		AssertThat(state.Bed!.SexTitle).IsEqual(0x11u);
		AssertThat(state.Bed.Sleep[2]).IsEqual(0x22u);
		AssertThat(state.Bed.SexAbout[1]).IsEqual(0x51u);
		(int Type, int Row, uint Tag) online = Fable3ScreenStatements.Of(0x40002000u).Online!.Value;
		AssertThat(online.Type).IsEqual(19);
		AssertThat(online.Row).IsEqual(3);
		AssertThat(online.Tag).IsEqual(0x77u);
		AssertThat(Fable3ScreenStatements.Of(0x40003000u).InviteAllowed == false).IsTrue();
		(int Pad, int Mode, int Reason, int Fee) divorce = Fable3ScreenStatements.Of(0x40004000u).Divorce!.Value;
		AssertThat(divorce.Pad).IsEqual(2);
		AssertThat(divorce.Mode).IsEqual(3);
		AssertThat(divorce.Reason).IsEqual(1);
		AssertThat(divorce.Fee).IsEqual(250);

		Fable3ScreenStatements.Forget(0x40001000u);
		AssertThat(Fable3ScreenStatements.Of(0x40001000u).Bed is null).IsTrue();
		AssertThat(Fable3ScreenStatements.Take(Call("@Screen.Fields", S("40001000"), S("bed"), B(true), N(0f)))).IsFalse();
	}

	private static Engine_UiModel.GuiNativeCall Call(string name, params Engine_UiModel.GuiNativeArgument[] arguments) => new(name, arguments);

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument N(float value) =>
		new(Engine_UiModel.GuiNativeArgumentKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static Engine_UiModel.GuiNativeArgument B(bool value) => new(Engine_UiModel.GuiNativeArgumentKind.Boolean, value ? "1" : "0");
}
