namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// The session and family boxes Godot owns (join / visibility, friends channel, co-op partners, bed,
/// divorce, treasury, marriage proposal, food query): what a press and a pointer pick mean as the
/// `box:` lever their own handlers answer (session_boxes.cpp, family_boxes.cpp), and which working
/// values stay Godot's.
/// </summary>
[TestSuite]
public class Fable3SessionBoxAnswersTests
{
	private const uint Box = 0x40AB0020u;

	private static Fable3BoxAnswers.Press Press(bool a = false, bool b = false, bool x = false, bool y = false, bool up = false,
		bool down = false, bool left = false, bool right = false) => new(a, b, x, y, up, down, left, right);

	private static Fable3BoxAnswers.ScreenStep Step(string root, Fable3BoxAnswers.Press press, Fable3ScreenState screen,
		int player = Fable3ActingPlayer.One) => Fable3BoxAnswers.ForScreen(root, Box, press, screen, false, player);

	[TestCase]
	public void JoinOptionsStartOnTheStatedRowAndHandTheCursorsRowOver()
	{
		var screen = new Fable3ScreenState { Online = (18, 2, 0u) };
		AssertThat(Step("OrbEmote", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:choose:2");
		Fable3BoxAnswers.ScreenStep down = Step("OrbEmote", Press(down: true), screen);
		AssertThat(down.Lever).IsEqual("box:1:40AB0020:move:3");
		AssertThat(down.Cursor).IsEqual(2);
		screen.Cursor = 2;
		AssertThat(Step("OrbEmote", Press(down: true), screen).Any).IsFalse();
		AssertThat(Step("Visibilities", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:choose:3");
		AssertThat(Step("Visibilities", Press(b: true), screen).Lever).IsEqual("box:1:40AB0020:close");
	}

	[TestCase]
	public void FriendsChannelRowsAreTheGuestsOneToThree()
	{
		var screen = new Fable3ScreenState();
		AssertThat(Step("FriendsChannel", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:row:1");
		AssertThat(Step("FriendsChannel", Press(up: true), screen).Any).IsFalse();
		screen.Cursor = 2;
		AssertThat(Step("FriendsChannel", Press(a: true), screen, Fable3ActingPlayer.Two).Lever).IsEqual("box:2:40AB0020:row:3");
		AssertThat(Fable3SessionBoxAnswers.PointScreen("FriendsChannel", Box, "Join", true, screen).Lever).IsEqual("box:1:40AB0020:row:2");
		Fable3BoxAnswers.ScreenStep rest = Fable3SessionBoxAnswers.PointScreen("FriendsChannel", Box, "Join", false, screen);
		AssertThat(rest.Cursor).IsEqual(1);
		AssertThat(rest.Lever).IsEqual("box:1:40AB0020:move:2");
	}

	[TestCase]
	public void PartnersCursorIsStatedByItsLeverAndTheButtonsCarryTheRecord()
	{
		var screen = new Fable3ScreenState();
		AssertThat(Step("CoopPartnersInfoBox", Press(down: true), screen).Lever).IsEqual("box:1:40AB0020:row:1");
		AssertThat(Step("CoopPartnersInfoBox", Press(up: true), screen).Any).IsFalse();
		screen.Cursor = 9;
		AssertThat(Fable3SessionBoxAnswers.PartnerWindowRow(screen)).IsEqual(7);
		AssertThat(Step("CoopPartnersInfoBox", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:join:9");
		AssertThat(Step("CoopPartnersInfoBox", Press(y: true), screen).Lever).IsEqual("box:1:40AB0020:card:9");
		AssertThat(Step("CoopPartnersInfoBox", Press(x: true), screen).Lever).IsEqual("box:1:40AB0020:divorce:9");
		AssertThat(Step("CoopPartnersInfoBox", Press(b: true), screen).Lever).IsEqual("box:1:40AB0020:close");
		// The window's row 0 is record 2 (scroll = 9 - 7).
		AssertThat(Fable3SessionBoxAnswers.PointScreen("CoopPartnersInfoBox", Box, "Partner0", false, screen).Lever)
			.IsEqual("box:1:40AB0020:row:2");
		AssertThat(Fable3SessionBoxAnswers.PointScreen("CoopPartnersInfoBox", Box, "Partner7", false, screen).Any).IsFalse();
	}

	[TestCase]
	public void BedOptionsAreGodotsAndEveryMovePlaysItsCue()
	{
		var screen = new Fable3ScreenState { Opened = new[] { 0, 0, 0 } };
		Fable3BoxAnswers.ScreenStep right = Step("BedMenu", Press(right: true), screen);
		AssertThat(right.Lever).IsEqual("box:1:40AB0020:option:right:1");
		AssertThat(right.Sleep).IsEqual(1);
		AssertThat(Step("BedMenu", Press(left: true), screen).Sleep).IsEqual(2);
		Fable3BoxAnswers.ScreenStep down = Step("BedMenu", Press(down: true), screen);
		AssertThat(down.Lever).IsEqual("box:1:40AB0020:row:0");
		AssertThat(down.Cursor == null).IsTrue();
		AssertThat(Step("BedMenu", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:sleep:0");
		AssertThat(Step("BedMenu", Press(b: true), screen).Lever).IsEqual("box:1:40AB0020:cancel");

		screen.Opened = new[] { 1, 0, 0 };
		down = Step("BedMenu", Press(down: true), screen);
		AssertThat(down.Lever).IsEqual("box:1:40AB0020:row:1");
		AssertThat(down.Cursor).IsEqual(1);
		screen.Cursor = 1;
		Fable3BoxAnswers.ScreenStep still = Step("BedMenu", Press(right: true), screen);
		AssertThat(still.Lever).IsEqual("box:1:40AB0020:option:right:0");
		AssertThat(still.Sex == null).IsTrue();
		AssertThat(Step("BedMenu", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:sex:0");
		screen.Opened = new[] { 1, 0, 1 };
		AssertThat(Step("BedMenu", Press(right: true), screen).Sex).IsEqual(1);
		screen.BedSex = 1;
		AssertThat(Step("BedMenu", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:sex:1");
		AssertThat(Fable3SessionBoxAnswers.PointScreen("BedMenu", Box, "Sleep", false, screen).Lever).IsEqual("box:1:40AB0020:row:0");
	}

	[TestCase]
	public void FamilyBoxesHandEachButtonToTheirHandler()
	{
		var screen = new Fable3ScreenState();
		AssertThat(Step("DivorceMessageBox", Press(a: true), screen).Lever).IsEqual("box:1:40AB0020:accept");
		AssertThat(Step("DivorceMessageBox", Press(b: true), screen).Lever).IsEqual("box:1:40AB0020:cancel");
		AssertThat(Step("DivorceMessageBox", Press(right: true), screen).Lever).IsEqual("box:1:40AB0020:reason:next");
		AssertThat(Step("DivorceMessageBox", Press(left: true), screen).Lever).IsEqual("box:1:40AB0020:reason:prev");
		AssertThat(Step("TreasureAccounts", Press(x: true), screen).Lever).IsEqual("box:1:40AB0020:flip");
		AssertThat(Step("TreasureAccounts", Press(right: true), screen).Lever).IsEqual("box:1:40AB0020:more");
		AssertThat(Step("TreasureAccounts", Press(b: true), screen).Lever).IsEqual("box:1:40AB0020:back");
		AssertThat(Step("InfoBox.MarriageProposal", Press(x: true), screen).Lever).IsEqual("box:1:40AB0020:leave");
		AssertThat(Step("InfoBox.MarriageProposal", Press(y: true), screen).Lever).IsEqual("box:1:40AB0020:card");
		AssertThat(Step("InfoBox", Press(a: true), screen, Fable3ActingPlayer.Two).Lever).IsEqual("box:2:40AB0020:yes");
		AssertThat(Step("InfoBox", Press(b: true), screen).Lever).IsEqual("box:1:40AB0020:no");
		AssertThat(Step("InfoBox", Press(up: true), screen).Any).IsFalse();
	}
}
