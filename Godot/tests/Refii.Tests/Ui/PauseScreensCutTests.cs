namespace Refii.Tests.Ui;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>
/// The pause screens' statements Godot takes from the guest: the shop's third query answer, the
/// adjuster's render statement, the world map's emphasis by name, the purse every list screen tells, the
/// gift list's gold row and decoration's Y. Pure .NET — no Godot runtime.
/// </summary>
[TestSuite]
public class PauseScreensCutTests
{
	private const uint Map = 0x4A002300u;

	private static Engine_UiModel.MenuChunk Chunk(string text, params (string Name, string Value)[] staged)
	{
		var bag = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach ((string name, string value) in staged)
		{
			bag[name] = value;
		}

		return new Engine_UiModel.MenuChunk(Map, text, bag);
	}

	/// <summary>Query 20's X is code 75 (`cmpwi r31, 0x4B` at 0x826AF8EC); B answers nothing.</summary>
	[TestCase]
	public void ShopEquipAndContinueIsQueryTwentysAlternate()
	{
		var table = new Engine_UiModals();
		Engine_UiModalTemplate twenty = table.Find(20, uint.MaxValue, dismissal: false)!;
		AssertThat(twenty.Confirm).IsEqual(36u);
		AssertThat(twenty.Cancel).IsNull();
		AssertThat(twenty.Alternate).IsEqual(75u);
		AssertThat(table.Find(15, uint.MaxValue, dismissal: false)!.Alternate).IsNull();

		string json = table.ToJson();
		AssertThat(json.Contains("\"alternate\": 75", StringComparison.Ordinal)).IsTrue();

		var copy = new Engine_UiModals();
		var warnings = new List<string>();
		copy.ApplyText("{ \"modals\": { \"20\": { \"kind\": \"query\", \"confirm\": 36, \"alternate\": 75 }," +
			" \"21\": { \"kind\": \"message\", \"confirm\": 36, \"alternate\": 75 } } }", _ => { }, warnings.Add);
		AssertThat(copy.Find(20, uint.MaxValue, dismissal: false)!.Alternate).IsEqual(75u);
		AssertThat(warnings.Count).IsEqual(1);
	}

	/// <summary>The adjuster is drawn from its widget's render statement (`sub_826B8BB0`), keyed by
	/// the widget: the latest render wins, its flags format the value and its step gates the arrows.</summary>
	[TestCase]
	public void AdjusterValueIsTheWidgetsRenderStatement()
	{
		Engine_UiNumericRender.Clear();
		const uint widget = 0x40123418u;
		AssertThat(Engine_UiNumericRender.Of(widget)).IsNull();

		Engine_UiNumericRender.Stated(widget, 20, -60, 25, 5, 0);
		Engine_UiNumericRender.Stated(widget, 25, -60, 25, 5, 4 | 2);
		Engine_UiGuiBoxes.AdjusterState state = Engine_UiNumericRender.Of(widget)!;
		AssertThat(state.Value).IsEqual(25);
		AssertThat(state.Display).IsEqual("+25%");
		AssertThat(state.CanIncrease).IsFalse();
		AssertThat(state.CanDecrease).IsTrue();

		Engine_UiNumericRender.Stated(widget, 1500, 0, 5000, 100, 1);
		AssertThat(Engine_UiNumericRender.Of(widget)!.Display).IsEqual("1500");
		AssertThat(Engine_UiNumericRender.Of(0u)).IsNull();
		Engine_UiNumericRender.Clear();
		AssertThat(Engine_UiNumericRender.Of(widget)).IsNull();
	}

	/// <summary>
	/// `SetItemEmphasis` by name lands on a row the model never spawned (the world map's template
	/// categories, `sub_823DFA98(23, name, 3)`), and `GetLastItemIndex` — answered at the runner —
	/// states nothing.
	/// </summary>
	[TestCase]
	public void NameEmphasisIsKeptForRowsTheModelDoesNotHold()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Chunk("g_Menu:SetItemEmphasis()", ("String1", "Quests"), ("Float1", "1")));
		AssertThat(model.For(Map).Emphases["Quests"]).IsEqual(Engine_UiMenuConstants.Emphasis.PrimaryQuest);
		AssertThat(model.For(Map).Rows.Count).IsEqual(0);

		model.Apply(Chunk("g_Menu:GetLastItemIndex()"));
		AssertThat(model.For(Map).Rows.Count).IsEqual(0);
		AssertThat(model.For(Map).Emphases.Count).IsEqual(1);
	}

	/// <summary>
	/// Every caller of the purse writer `sub_8246B5E8` that owns a list states <c>purse</c>; the
	/// pause root owns none. The flag round-trips through the templates file.
	/// </summary>
	[TestCase]
	public void PurseScreensAreEveryCallerOfThePurseWriter()
	{
		Engine_UiTemplateSet authored = Fable2UiTemplates.AuthoredDefaults();
		uint[] purses = { 14, 15, 16, 17, 18, 21, 22, 23, 25, 26, 27, 28 };
		foreach (uint id in purses)
		{
			AssertThat(authored.Screens[id].Purse).OverrideFailureMessage($"screen {id}").IsTrue();
		}

		AssertThat(authored.Screens[0].Purse).IsFalse();

		Engine_UiScreenTemplate shop = authored.Screens[22];
		Engine_UiScreenTemplate? read = Engine_UiTemplateJson.ParseScreen(22, Engine_UiTemplateJson.ScreenJson(shop));
		AssertThat(read!.Purse).IsTrue();
	}

	/// <summary>
	/// The gift list's Gold row stands only while the giver has gold (`ble` at 0x82430A80), and
	/// decoration's Y is the take-down of the guest's own code 39 arm (`sub_826B7770` case 13), posted
	/// as the ported screen's lever.
	/// </summary>
	[TestCase]
	public void GiftGoldAndDecorationRemoveFollowTheGuest()
	{
		Engine_UiTemplateSet authored = Fable2UiTemplates.AuthoredDefaults();
		Engine_UiMenuEntry gold = authored.Menus["GiftGivingMenu"].Entries.Single(e => e.Name == "Gold");
		AssertThat(gold.When).IsEqual(Engine_UiRowConditions.HasGold);
		AssertThat(Engine_UiRowConditions.Game.Contains(Engine_UiRowConditions.HasGold)).IsTrue();

		AssertThat(Engine_UiRowConditions.Holds(gold.When,
			new Dictionary<string, bool> { [Engine_UiRowConditions.HasGold] = false })).IsFalse();
		AssertThat(Engine_UiRowConditions.Holds(gold.When,
			new Dictionary<string, bool> { [Engine_UiRowConditions.HasGold] = true })).IsTrue();

		AssertThat(authored.Screens[27].FlipAction).IsEqual(Fable2UiVerbs.DecorationTakeDownAction);
		AssertThat(authored.Screens[25].FlipAction).IsEqual(string.Empty);
	}

	/// <summary>
	/// The villager card renames on X — the guest's code 38 (`sub_826CCBA8`, `cmpwi r4, 0x26`), the
	/// card's authored "Set Nickname" prompt — and Y does nothing there; the JSON form round-trips it.
	/// </summary>
	[TestCase]
	public void VillagerCardRenamesOnX()
	{
		Engine_UiTemplateSet authored = Fable2UiTemplates.AuthoredDefaults();
		AssertThat(authored.Screens[10].XAction).IsEqual(Fable2UiVerbs.QueryRenameAction);
		AssertThat(authored.Screens[10].FlipAction).IsEqual(string.Empty);
		AssertThat(authored.Screens[10].RevealedBack).IsEqual(Fable2UiVerbs.QueryCloseAction);
	}

	/// <summary>
	/// The logbook's expression icon is the provider's, from the row's record `IconGraphic`
	/// (FillExpression `sub_829BCDF0` 0x829bcee8 stores that name's hash): no told guest unit.
	/// </summary>
	[TestCase]
	public void LogbookExpressionIconIsTheProvidersNotTold()
	{
		Engine_UiTemplateSet authored = Fable2UiTemplates.AuthoredDefaults();
		AssertThat(authored.Screens[17].GuestTextUnits).NotContains("ExpressionsPage.Contents.Icon");
	}
}
