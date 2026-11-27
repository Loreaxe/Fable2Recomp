namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// The box screens Godot owns (hero status, promise, king's schedule, quest summary, wedding picker,
/// chat channel, stats): what a press and a pointer pick mean as the tick action handed to the box's
/// own handler (display_box_hooks.cpp), and which working values stay Godot's.
/// </summary>
[TestSuite]
public class Fable3ScreenAnswersTests
{
	private const uint Box = 0x40AB0010u;

	private static Fable3BoxAnswers.Press Press(bool a = false, bool b = false, bool y = false, bool up = false, bool down = false,
		bool left = false, bool right = false) => new(a, b, false, y, up, down, left, right);

	private static string? Lever(string root, Fable3BoxAnswers.Press press, Fable3ScreenState? screen = null, bool muted = false,
		int player = Fable3ActingPlayer.One) => Fable3BoxAnswers.ForScreen(root, Box, press, screen ?? new Fable3ScreenState(), muted, player).Lever;

	[TestCase]
	public void EveryBoxScreenIsOwnedButTheSaveMenu()
	{
		foreach (string root in new[] { "HeroStatus", "Promises", "KingsSchedule", "QuestSummary", "StatsBox", "WeddingPicker", "ChatChannel",
			"FriendsChannel", "OrbEmote", "Visibilities", "BedMenu", "TreasureAccounts", "CoopPartnersInfoBox", "DivorceMessageBox",
			"InfoBox", "InfoBox.MarriageProposal" })
		{
			AssertThat(Fable3BoxAnswers.OwnsScreen(root)).IsTrue();
		}

		foreach (string? root in new[] { "SaveMenu", null })
		{
			AssertThat(Fable3BoxAnswers.OwnsScreen(root)).IsFalse();
		}
	}

	[TestCase]
	public void HeroStatusPromiseAndScheduleAnswerTheirOwnButtons()
	{
		AssertThat(Lever("HeroStatus", Press(b: true))).IsEqual("box:1:40AB0010:close");
		AssertThat(Lever("HeroStatus", Press(a: true))).IsNull();
		AssertThat(Lever("Promises", Press(a: true))).IsEqual("box:1:40AB0010:accept");
		AssertThat(Lever("Promises", Press(b: true))).IsEqual("box:1:40AB0010:close");
		AssertThat(Lever("KingsSchedule", Press(a: true))).IsEqual("box:1:40AB0010:accept");
		AssertThat(Lever("KingsSchedule", Press(b: true))).IsNull();
	}

	[TestCase]
	public void TheLeverCarriesThePlayerWhoPressed()
	{
		AssertThat(Lever("HeroStatus", Press(b: true), player: Fable3ActingPlayer.Two)).IsEqual("box:2:40AB0010:close");
		AssertThat(Lever("KingsSchedule", Press(a: true), player: Fable3ActingPlayer.Two)).IsEqual("box:2:40AB0010:accept");
	}

	[TestCase]
	public void QuestSummaryMovesGodotsCursorAndPicksIt()
	{
		var screen = new Fable3ScreenState { Cursor = 3 };
		AssertThat(Lever("QuestSummary", Press(down: true), screen)).IsEqual("box:1:40AB0010:row:4");
		AssertThat(Lever("QuestSummary", Press(up: true), screen)).IsEqual("box:1:40AB0010:row:2");
		AssertThat(Lever("QuestSummary", Press(a: true), screen)).IsEqual("box:1:40AB0010:select:3");
		AssertThat(Lever("QuestSummary", Press(b: true), screen)).IsEqual("box:1:40AB0010:close");
		// Before any lever the cursor is the ctor's row 0, and up from it is no row.
		AssertThat(Lever("QuestSummary", Press(up: true))).IsNull();
		AssertThat(Lever("QuestSummary", Press(a: true))).IsEqual("box:1:40AB0010:select:0");
	}

	[TestCase]
	public void QuestSummaryPointerNamesTheListRowBehindTheWindowSlot()
	{
		// Cursor on list row 8, drawn in window slot 6 (the guest's window starts at cursor - 6).
		var screen = new Fable3ScreenState { Cursor = 8 };
		for (int slot = 0; slot < 7; ++slot)
		{
			screen.QuestRows[slot] = (0x1000u + (uint)slot, slot == 6, slot == 6);
		}

		AssertThat(Fable3BoxAnswers.PointScreen("QuestSummary", Box, "Quest2", false, screen).Lever).IsEqual("box:1:40AB0010:row:4");
		AssertThat(Fable3BoxAnswers.PointScreen("QuestSummary", Box, "Quest6", true, screen).Lever).IsEqual("box:1:40AB0010:select:8");
	}

	[TestCase]
	public void WeddingTierIsGodotsStatedAsItMovesAndGoesWithTheAnswer()
	{
		var screen = new Fable3ScreenState();
		Fable3BoxAnswers.ScreenStep down = Fable3BoxAnswers.ForScreen("WeddingPicker", Box, Press(down: true), screen);
		AssertThat(down.Lever).IsEqual("box:1:40AB0010:tier:1");
		AssertThat(down.Tier).IsEqual(1);
		AssertThat(Fable3BoxAnswers.ForScreen("WeddingPicker", Box, Press(up: true), screen).Any).IsFalse();

		screen.Tier = 2;
		AssertThat(Fable3BoxAnswers.ForScreen("WeddingPicker", Box, Press(down: true), screen).Any).IsFalse();
		AssertThat(Lever("WeddingPicker", Press(a: true), screen)).IsEqual("box:1:40AB0010:accept:2");
		AssertThat(Lever("WeddingPicker", Press(b: true), screen)).IsEqual("box:1:40AB0010:decline:2");

		Fable3BoxAnswers.ScreenStep click = Fable3BoxAnswers.PointScreen("WeddingPicker", Box, "Tier0", true, screen);
		AssertThat(click.Lever).IsEqual("box:1:40AB0010:accept:0");
		AssertThat(click.Tier).IsEqual(0);
		Fable3BoxAnswers.ScreenStep rest = Fable3BoxAnswers.PointScreen("WeddingPicker", Box, "Tier1", false, screen);
		AssertThat(rest.Lever).IsEqual("box:1:40AB0010:tier:1");
		AssertThat(rest.Tier).IsEqual(1);
		AssertThat(Fable3BoxAnswers.PointScreen("WeddingPicker", Box, "Tier2", false, screen).Any).IsFalse();
	}

	[TestCase]
	public void ChatMuteIsGodotsLeftMutesRightUnmutesAndASavesIt()
	{
		Fable3BoxAnswers.ScreenStep muted = Fable3BoxAnswers.ForScreen("ChatChannel", Box, Press(left: true), new Fable3ScreenState(), muted: false);
		AssertThat(muted.Lever).IsEqual("box:1:40AB0010:mute:1");
		AssertThat(muted.Muted).IsEqual(true);
		// The guest's rule: left does nothing once muted, right nothing while audible.
		AssertThat(Fable3BoxAnswers.ForScreen("ChatChannel", Box, Press(left: true), new Fable3ScreenState(), muted: true).Any).IsFalse();
		AssertThat(Fable3BoxAnswers.ForScreen("ChatChannel", Box, Press(right: true), new Fable3ScreenState(), muted: false).Any).IsFalse();
		AssertThat(Lever("ChatChannel", Press(right: true), muted: true)).IsEqual("box:1:40AB0010:mute:0");
		AssertThat(Lever("ChatChannel", Press(a: true), muted: true)).IsEqual("box:1:40AB0010:chat:1");
		AssertThat(Lever("ChatChannel", Press(a: true), muted: false)).IsEqual("box:1:40AB0010:chat:0");
		AssertThat(Lever("ChatChannel", Press(b: true), muted: true)).IsEqual("box:1:40AB0010:close");
		Fable3BoxAnswers.ScreenStep click = Fable3BoxAnswers.PointScreen("ChatChannel", Box, "Mute", true, new Fable3ScreenState(), muted: true);
		AssertThat(click.Muted).IsEqual(false);
		AssertThat(click.Lever).IsEqual("box:1:40AB0010:mute:0");
	}

	[TestCase]
	public void StatsCursorStaysInsideTheStatedListAndTheButtonsFollowThePage()
	{
		var screen = new Fable3ScreenState { Cursor = 1 };
		screen.StatsWindows[2] = (0, 2, 8);
		AssertThat(Lever("StatsBox", Press(down: true), screen)).IsNull();
		AssertThat(Lever("StatsBox", Press(up: true), screen)).IsEqual("box:1:40AB0010:stats-row:0");
		AssertThat(Lever("StatsBox", Press(a: true), screen)).IsEqual("box:1:40AB0010:stats-open:1");
		AssertThat(Lever("StatsBox", Press(b: true), screen)).IsEqual("box:1:40AB0010:stats-back");
		AssertThat(Lever("StatsBox", Press(y: true), screen)).IsNull();

		screen.StatsPage = 4;
		screen.Cursor = 2;
		screen.StatsWindows[4] = (0, 5, 9);
		AssertThat(Lever("StatsBox", Press(a: true), screen)).IsNull();
		AssertThat(Lever("StatsBox", Press(y: true), screen)).IsEqual("box:1:40AB0010:stats-card:2");
		AssertThat(Lever("StatsBox", Press(down: true), screen)).IsEqual("box:1:40AB0010:stats-row:3");
	}

	[TestCase]
	public void TheAchievementsCategoryIsPastTheGuestsLastAndOpensThePage()
	{
		var screen = new Fable3ScreenState { Cursor = 1 };
		screen.StatsWindows[2] = (0, 2, 8);
		Fable3BoxAnswers.ScreenStep onto = Fable3BoxAnswers.ForScreen("StatsBox", Box, Press(down: true), screen);
		AssertThat(onto.Lever).IsNull();
		AssertThat(onto.StatsAchievements).IsEqual(true);

		screen.StatsAchievements = true;
		Fable3BoxAnswers.ScreenStep open = Fable3BoxAnswers.ForScreen("StatsBox", Box, Press(a: true), screen);
		AssertThat(open.OpenAchievements).IsTrue();
		AssertThat(open.Lever).IsNull();
		AssertThat(Fable3BoxAnswers.ForScreen("StatsBox", Box, Press(up: true), screen).StatsAchievements).IsEqual(false);
		AssertThat(Fable3BoxAnswers.ForScreen("StatsBox", Box, Press(down: true), screen).Any).IsFalse();
		AssertThat(Fable3BoxAnswers.ForScreen("StatsBox", Box, Press(b: true), screen).Lever).IsEqual("box:1:40AB0010:stats-back");
		AssertThat(Fable3BoxAnswers.PointScreen("StatsBox", Box, Fable3ScreenModel.StatsAchievementsRow, true, screen).OpenAchievements)
			.IsTrue();

		// The statistics page has no host row.
		screen.StatsAchievements = false;
		screen.StatsPage = 3;
		screen.StatsWindows[3] = (0, 2, 9);
		AssertThat(Fable3BoxAnswers.ForScreen("StatsBox", Box, Press(down: true), screen).StatsAchievements).IsNull();
	}

	[TestCase]
	public void StatsPointerAddsTheWindowsFirstRow()
	{
		var screen = new Fable3ScreenState { StatsPage = 3, Cursor = 7 };
		screen.StatsWindows[3] = (6, 12, 6);
		AssertThat(Fable3BoxAnswers.PointScreen("StatsBox", Box, "Statistic1", false, screen).Any).IsFalse();
		AssertThat(Fable3BoxAnswers.PointScreen("StatsBox", Box, "Statistic3", false, screen).Lever).IsEqual("box:1:40AB0010:stats-row:9");
		AssertThat(Fable3BoxAnswers.PointScreen("StatsBox", Box, "Statistic3", true, screen).Lever).IsEqual("box:1:40AB0010:stats-open:9");
	}

	[TestCase]
	public void ANewStatsPageStartsGodotsCursorOnRowZeroAndTheLeverMovesIt()
	{
		Fable3ScreenStatements.Clear();
		Fable3ScreenStatements.Log = _ => { };
		Fable3ScreenStatements.Take(Call("@Screen.Cursor", "40AB0010", 5f));
		AssertThat(Fable3ScreenStatements.Of(Box).Cursor).IsEqual(5);

		// The Show's page 2 is the page the box is on: nothing moves.
		Fable3ScreenStatements.Take(Call("@Screen.StatsPage", "40AB0010", 2f));
		AssertThat(Fable3ScreenStatements.Of(Box).Cursor).IsEqual(5);
		Fable3ScreenStatements.Take(Call("@Screen.StatsPage", "40AB0010", 3f));
		AssertThat(Fable3ScreenStatements.Of(Box).Cursor).IsEqual(0);
		AssertThat(Fable3ScreenStatements.Of(Box).StatsPage).IsEqual(3);

		// Godot's working values live with the box's record and go with it.
		Fable3ScreenStatements.Working(Box).Tier = 2;
		AssertThat(Fable3ScreenStatements.Of(Box).Tier).IsEqual(2);
		Fable3ScreenStatements.Forget(Box);
		AssertThat(Fable3ScreenStatements.Of(Box).Tier).IsEqual(0);
		AssertThat(Fable3ScreenStatements.Of(Box).Cursor).IsNull();
	}

	[TestCase]
	public void TheEpilogueClosesOnAAndTheCreditsAskTheGuestOnAOrB()
	{
		AssertThat(Fable3EndScreens.Lever(true, false, a: true, b: false, Fable3ActingPlayer.One)).IsEqual("epilogue:1:close");
		AssertThat(Fable3EndScreens.Lever(true, false, a: false, b: true, Fable3ActingPlayer.One)).IsNull();
		AssertThat(Fable3EndScreens.Lever(true, false, a: true, b: false, Fable3ActingPlayer.Two)).IsEqual("epilogue:2:close");
		AssertThat(Fable3EndScreens.Lever(false, true, a: false, b: true, Fable3ActingPlayer.One)).IsEqual("credits:1:skip");
		AssertThat(Fable3EndScreens.Lever(false, true, a: true, b: false, Fable3ActingPlayer.Two)).IsEqual("credits:2:skip");
		// The epilogue is the open screen object when both are up; nothing up takes nothing.
		AssertThat(Fable3EndScreens.Lever(true, true, a: true, b: false, Fable3ActingPlayer.One)).IsEqual("epilogue:1:close");
		AssertThat(Fable3EndScreens.Lever(false, false, a: true, b: true, Fable3ActingPlayer.One)).IsNull();
	}

	[TestCase]
	public void AWorldMapSceneLeftPresentedByABarePopIsStranded()
	{
		Fable3WorldMapMode.Reset();
		Fable3WorldMapMode.Log = _ => { };
		AssertThat(Fable3WorldMapMode.Stranded(presented: true)).IsFalse();

		// The proper order: enter, (present), the close un-presents, the mode leaves.
		Fable3WorldMapMode.Entered(2);
		AssertThat(Fable3WorldMapMode.Owner).IsEqual(2);
		Fable3WorldMapMode.ScenePresented(true);
		Fable3WorldMapMode.ScenePresented(false);
		Fable3WorldMapMode.Left();
		AssertThat(Fable3WorldMapMode.Owner).IsEqual(0);
		AssertThat(Fable3WorldMapMode.Stranded(presented: false)).IsFalse();

		// The bare pop: the mode leaves and the scene stays presented.
		Fable3WorldMapMode.Entered(1);
		Fable3WorldMapMode.ScenePresented(true);
		Fable3WorldMapMode.Left();
		AssertThat(Fable3WorldMapMode.Stranded(presented: true)).IsTrue();

		// Pushed again (the recovery): the map reads input again.
		Fable3WorldMapMode.Entered(1);
		AssertThat(Fable3WorldMapMode.Stranded(presented: true)).IsFalse();

		// A map table that never had the world map's mode (the town map) is never stranded.
		Fable3WorldMapMode.Reset();
		Fable3WorldMapMode.Left();
		AssertThat(Fable3WorldMapMode.Stranded(presented: true)).IsFalse();
	}

	[TestCase]
	public void TheMapsButtonsAreItsOwnersLevers()
	{
		const uint a = 0x1000u, b = 0x2000u, x = 0x4000u, y = 0x8000u, start = 0x0010u, lb = 0x0100u, rb = 0x0200u;
		AssertThat(Fable3MapInput.Levers(0u, a, player: 1, owner: 1)).ContainsExactly("map:1:120");
		AssertThat(Fable3MapInput.Levers(0u, b | y | x | start, 1, 1)).ContainsExactly("map:1:121", "map:1:123", "map:1:124", "map:1:14");
		// Held is not pressed again; a bumper states its down and its up.
		AssertThat(Fable3MapInput.Levers(a, a, 1, 1)).IsEmpty();
		AssertThat(Fable3MapInput.Levers(0u, lb, 1, 1)).ContainsExactly("map-held:1:9:down");
		AssertThat(Fable3MapInput.Levers(lb | rb, lb, 1, 1)).ContainsExactly("map-held:1:10:up");
		// The map reads its opener's pad alone, and nothing with no map mode.
		AssertThat(Fable3MapInput.Levers(0u, a, player: 1, owner: 2)).IsEmpty();
		AssertThat(Fable3MapInput.Levers(0u, a, player: 2, owner: 2)).ContainsExactly("map:2:120");
		AssertThat(Fable3MapInput.Levers(0u, a, player: 1, owner: 0)).IsEmpty();
	}

	[TestCase]
	public void AMapOfASeatWithNoPadHereIsDrivenByPlayerOneAsItsOwner()
	{
		// Player two's map in a solo game (the guest registered receiver 2): pad 0 acts as player 2.
		AssertThat(Fable3MapInput.ActsAs(pad: 0, owner: 2, ownersPadConnected: false)).IsEqual(2);
		// With player two's pad here, that pad drives it and pad 0 is player one, whom the map does not read.
		AssertThat(Fable3MapInput.ActsAs(pad: 1, owner: 2, ownersPadConnected: true)).IsEqual(2);
		AssertThat(Fable3MapInput.ActsAs(pad: 0, owner: 2, ownersPadConnected: true)).IsEqual(1);
		// Player one's map: pad 0 always, pad 1 never.
		AssertThat(Fable3MapInput.ActsAs(pad: 0, owner: 1, ownersPadConnected: true)).IsEqual(1);
		AssertThat(Fable3MapInput.ActsAs(pad: 1, owner: 1, ownersPadConnected: true)).IsEqual(2);
		AssertThat(Fable3MapInput.ActsAs(pad: 0, owner: 0, ownersPadConnected: false)).IsEqual(0);
		AssertThat(Fable3MapInput.Levers(0u, 0x2000u, Fable3MapInput.ActsAs(0, 2, false), 2)).ContainsExactly("map:2:121");
	}

	private static Engine_UiModel.GuiNativeCall Call(string name, string box, float number) =>
		new(name, new[]
		{
			new Engine_UiModel.GuiNativeArgument(Engine_UiModel.GuiNativeArgumentKind.String, box),
			new Engine_UiModel.GuiNativeArgument(Engine_UiModel.GuiNativeArgumentKind.Number,
				number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
		});
}
