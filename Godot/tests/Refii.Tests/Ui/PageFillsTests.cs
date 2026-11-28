namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using Refii.Titles.Fable2;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// `@PageFill` (page_fills_stated.cpp): a page fill's writes by the names it gave, held per screen and
/// scope, a row scope's statement belonging to the row its key names; the shop's front page and the
/// upgrade-furniture pages read them as their channels. No Godot runtime call is made.
/// </summary>
[TestSuite]
public sealed class PageFillsTests
{
	private const string Front = "Foreground.FrontPage.Contents.";

	private const string Page1 = "Foreground.Book.Pages.Page1.Contents.";

	private static Engine_UiModel.GuiNativeCall Call(params string[] arguments)
	{
		var list = new List<Engine_UiModel.GuiNativeArgument>();
		foreach (string argument in arguments)
		{
			list.Add(new Engine_UiModel.GuiNativeArgument(Engine_UiModel.GuiNativeArgumentKind.String, argument));
		}

		return new Engine_UiModel.GuiNativeCall("@PageFill", list);
	}

	[TestCase]
	public void TheShopsFrontPageIsWhatItsFillWrote()
	{
		Engine_UiPageFills.Reset();
		Engine_UiPageFills.Filled(Call("22", "front", "0",
			"text", Front + "ShopName", "Bowerstone Fruit & Veg Stall",
			"slide", Front + "StarRating", "6",
			"shown", Front + "Group.SaleShortage", "0",
			"slide", Front + "Group.OwnerKeeper", "1"));

		Engine_UiShopFrontPage.Statements? said = Engine_UiShopFrontPage.State();
		AssertThat(said).IsNotNull();
		AssertThat(said!.Values[Front + "ShopName"]).IsEqual("Bowerstone Fruit & Veg Stall");
		AssertThat(said.Slides[Front + "StarRating"]).IsEqual(6);
		AssertThat(said.Slides[Front + "Group.OwnerKeeper"]).IsEqual(1);
		AssertThat(said.Active[Front + "Group.SaleShortage"]).IsFalse();

		Engine_UiPageFills.Reset();
		AssertThat(Engine_UiShopFrontPage.State()).IsNull();
	}

	[TestCase]
	public void ARowStatementBelongsToTheRowItsKeyNames()
	{
		Engine_UiPageFills.Reset();
		Engine_UiPageFills.Filled(Call("22", "row", "65538",
			"text", Page1 + "Text.TextElements.PriceValue", "$120",
			"record", "preview", "1A2B3C4D"));

		AssertThat(Engine_UiPageFills.ForRow(22, 65538)).IsNotNull();
		AssertThat(Engine_UiPageFills.ForRow(22, 65538)!.Texts[Page1 + "Text.TextElements.PriceValue"]).IsEqual("$120");
		AssertThat(Engine_UiPageFills.ForRow(22, 65538)!.Records["preview"]).IsEqual(0x1A2B3C4Du);
		AssertThat(Engine_UiPageFills.ForRow(22, 3)).IsNull();
		AssertThat(Engine_UiPageFills.ForRow(26, 65538)).IsNull();

		Engine_UiPageFills.Filled(Call("22", "front", "0", "text", "Foreground.FrontPage.Contents.ShopName", "Next"));
		AssertThat(Engine_UiPageFills.ForRow(22, 65538)).IsNull();
	}

	[TestCase]
	public void TheFurniturePagesTakeTheirOpacityColourAndPreview()
	{
		Engine_UiPageFills.Reset();
		Engine_UiPageFills.Filled(Call("26", "row", "2",
			"text", Page1 + "HouseValueAmount", "<gold_img>+250",
			"colour", Page1 + "HouseValueAmount", "00A000FF",
			"opacity", Page1 + "Select.Replace", "50",
			"record", "card", "0000BEEF"));

		var row = new Engine_UiExpandableMenu.Item { IdLow = 2, IdHigh = 0 };
		Engine_UiFurniturePage.Statements? said = Engine_UiFurniturePage.State(row);
		AssertThat(said).IsNotNull();
		AssertThat(said!.Values[Page1 + "HouseValueAmount"]).IsEqual("<gold_img>+250");
		AssertThat(said.Opacities[Page1 + "Select.Replace"]).IsEqual(0.5f);
		AssertThat(said.Tints[Page1 + "HouseValueAmount"].G).IsEqual(160f / 255f);
		AssertThat(said.Tints[Page1 + "HouseValueAmount"].R).IsEqual(0f);
		AssertThat(Engine_UiFurniturePage.PreviewRecord(row)).IsEqual(0xBEEFu);

		Engine_UiPageFills.Filled(Call("26", "row", "2", "record", "card", "0000BEEF", "record", "preview", "00000ABC"));
		AssertThat(Engine_UiFurniturePage.PreviewRecord(row)).IsEqual(0xABCu);
		AssertThat(Engine_UiFurniturePage.State(new Engine_UiExpandableMenu.Item { IdLow = 3 })).IsNull();
	}

	[TestCase]
	public void RealEstateReadsBothPagesWithTheTickOverPageOne()
	{
		const string Group = "Foreground.Book.Pages.Page1.Group.";
		Engine_UiPageFills.Reset();
		Engine_UiPageFills.Filled(Call("28", "page1", "0",
			"text", "Foreground.Book.Pages.Page1.Contents.Title", "Miracle Manor",
			"slide", "Foreground.Book.Pages.Page1.Contents.star", "4",
			"text", Group + "Variable2", "Not vacant",
			"shown", Group + "Variable2", "0",
			"shown", Group + "Variable4", "0",
			"text", Group + "Variable1Amount", "<gold_img>120"));
		Engine_UiPageFills.Filled(Call("28", "page2", "0",
			"text", "Foreground.Book.Pages.Page2.Contents.Group.AddressAmount", "Bowerstone"));
		Engine_UiPageFills.Filled(Call("28", "tick", "0",
			"text", Group + "Variable4", "Rent", "shown", Group + "Variable4", "1",
			"text", Group + "Variable1Amount", "<gold_img>150"));

		IReadOnlyDictionary<string, string> values = Engine_UiRealEstatePage.StatedValues();
		AssertThat(values["Foreground.Book.Pages.Page1.Contents.Title"]).IsEqual("Miracle Manor");
		AssertThat(values["Foreground.Book.Pages.Page2.Contents.Group.AddressAmount"]).IsEqual("Bowerstone");
		AssertThat(values[Group + "Variable2"]).IsEqual(string.Empty);
		AssertThat(values[Group + "Variable4"]).IsEqual("Rent");
		AssertThat(values[Group + "Variable1Amount"]).IsEqual("<gold_img>150");
		AssertThat(Engine_UiRealEstatePage.StatedSlides()["Foreground.Book.Pages.Page1.Contents.star"]).IsEqual(4);

		// The next open's page 1 drops the last visit's tick.
		Engine_UiPageFills.Filled(Call("28", "page1", "0", "text", Group + "Variable1Amount", "<gold_img>90"));
		AssertThat(Engine_UiRealEstatePage.StatedValues()[Group + "Variable1Amount"]).IsEqual("<gold_img>90");
	}

	[TestCase]
	public void AStatementWithoutAScreenOrScopeIsDropped()
	{
		AssertThat(Engine_UiPageFills.Parse(Call("x", "row", "0"))).IsNull();
		AssertThat(Engine_UiPageFills.Parse(Call("22", "", "0"))).IsNull();
		AssertThat(Engine_UiPageFills.Parse(Call("22", "row", "1", "text", "", "x"))!.Value.Fill.Texts.Count).IsEqual(0);
	}
	private static Engine_UiExpandableMenu.Item MapRow(string name, int type, int id)
	{
		var category = new Engine_UiExpandableMenu.Item("Quests", "GUI_SCREEN_WORLD_MAP_QUESTS") { EventType = 301 };
		var root = new Engine_UiExpandableMenu.Item("Root", string.Empty);
		category.Parent = root;
		return new Engine_UiExpandableMenu.Item(name, name) { EventType = type, IdLow = id & 0xFFFF, IdHigh = id >> 16, Parent = category };
	}

	private const string Quests = "Foreground.Book.Pages.QuestsPage.Contents.";

	private const string Jobs = "Foreground.Book.Pages.JobsPage.Contents.";

	/// <summary>
	/// The world map's row pages (gui_screen_stub_world_map.cpp): one restated scope per page, keyed by
	/// the guest's own row id. A page's statement belongs to its row; the five scopes are row scopes, so
	/// none drops another; the page shows the row's own texts, blanks what the fill hid, and is blank
	/// until its row's statement lands rather than another row's.
	/// </summary>
	[TestCase]
	public void TheWorldMapsRowPagesAreTheirRowsOwnFills()
	{
		Engine_UiPageFills.Reset();
		Engine_UiPageFills.Filled(Call("23", "row.quest", "0",
			"text", Quests + "PageTitle", "The Hero of Bower Lake",
			"text", Quests + "AButton.ButtonText", "Trail",
			"shown", Quests + "AButton", "1"));
		Engine_UiPageFills.Filled(Call("23", "row.job", "0",
			"text", Jobs + "PageTitle", "Bartender",
			"slide", Jobs + "StarRating", "3",
			"shown", Jobs + "AButton", "0"));

		// Quest 0 and job 0 share an id and not a page: each scope keeps its own.
		AssertThat(Engine_UiPageFills.ForRow(23, "row.quest", 0)).IsNotNull();
		AssertThat(Engine_UiPageFills.ForRow(23, "row.job", 0)).IsNotNull();
		AssertThat(Engine_UiPageFills.ForRow(23, 0)).IsNull();

		IReadOnlyDictionary<string, string> quest = Engine_UiWorldMapPage.Values(MapRow("The Hero of Bower Lake", 42, 0), null);
		AssertThat(quest[Quests + "PageTitle"]).IsEqual("The Hero of Bower Lake");
		AssertThat(quest.ContainsKey(Jobs + "PageTitle")).IsFalse();
		AssertThat(Engine_UiWorldMapPage.Active(MapRow("The Hero of Bower Lake", 42, 0))["Foreground.Book.Pages.QuestsPage"]).IsTrue();
		AssertThat(Engine_UiWorldMapPage.Active(MapRow("The Hero of Bower Lake", 42, 0))[Quests + "AButton"]).IsTrue();

		IReadOnlyDictionary<string, string> job = Engine_UiWorldMapPage.Values(MapRow("Bartender", 43, 0), null);
		AssertThat(job[Jobs + "PageTitle"]).IsEqual("Bartender");
		AssertThat(Engine_UiWorldMapPage.Slides(MapRow("Bartender", 43, 0))[Jobs + "StarRating"]).IsEqual(3);
		AssertThat(Engine_UiWorldMapPage.Active(MapRow("Bartender", 43, 0))[Jobs + "AButton"]).IsFalse();

		// Quest 1 before its statement lands: the quest page is blank, not quest 0's.
		IReadOnlyDictionary<string, string> waiting = Engine_UiWorldMapPage.Values(MapRow("Treasure Island of Doom", 42, 1), null);
		AssertThat(waiting[Quests + "PageTitle"]).IsEqual(string.Empty);
		AssertThat(waiting[Quests + "AButton.ButtonText"]).IsEqual(string.Empty);

		// The economy strip's guest slide 6 is refused by the guest; the strip's last slide stands.
		Engine_UiPageFills.Filled(Call("23", "row.region", "1002",
			"text", "Foreground.Book.Pages.Page1.Contents.PageTitle", "Oakfield",
			"slide", "Foreground.Book.Pages.Page1.Contents.Text.StarRating", "6"));
		Engine_UiExpandableMenu.Item region = MapRow("Oakfield", 41, 1002);
		AssertThat(Engine_UiWorldMapPage.RegionOf(region)).IsEqual(3);
		AssertThat(Engine_UiWorldMapPage.Slides(region)["Foreground.Book.Pages.Page1.Contents.Text.StarRating"]).IsEqual(5);

		// The map's row scopes are row scopes: a page fill of another screen's open leaves them.
		Engine_UiPageFills.Filled(Call("22", "front", "0", "text", Front + "ShopName", "Next"));
		AssertThat(Engine_UiPageFills.ForRow(23, "row.quest", 0)).IsNotNull();
		Engine_UiPageFills.Reset();
	}

	/// <summary>The minimap page's fill (minimap_page_stated.cpp): the map image's texture and turn, the legend, the
	/// projection and the icons in call order, held for the region row that asked; each icon placed through the
	/// stated projection, the hero's turned by its facing, the others not.</summary>
	[TestCase]
	public void TheMinimapPageIsItsRegionRowsOwnFill()
	{
		const string page2 = "Foreground.Book.Pages.Page2.Contents.";
		Engine_UiPageFills.Reset();
		Engine_UiMinimapPage.Reset();
		Engine_UiPageFills.Filled(Call("23", "row.minimap", "1002",
			"texture", page2 + "Bowerstone", "1A2B3C4D",
			"rotation", page2 + "Bowerstone", "0 0 1.5708",
			"text", page2 + "Legend.KeyName1", "Hero",
			"shown", page2 + "Legend.KeyName1", "1",
			"texture", page2 + "Legend.KeyImage1", "0000BEEF",
			"shown", page2 + "Legend.KeyName10", "0",
			"map", "Page2", "90 0 0 0 0 1 1 400",
			"icon", "0000BEEF", "200 200 0 0 1 0",
			"icon", "0000CAFE", "100 100 0 0 0 0"));

		Engine_UiPageFills.Fill? fill = Engine_UiPageFills.ForRow(23, "row.minimap", 1002);
		AssertThat(fill).IsNotNull();
		AssertThat(fill!.Textures[page2 + "Bowerstone"]).IsEqual(0x1A2B3C4Du);
		AssertThat(fill.Rotations[page2 + "Bowerstone"].Z).IsEqual(1.5708f);
		AssertThat(fill.Projection).IsNotNull();
		AssertThat(fill.Projection!.Value.Extent).IsEqual(400f);
		AssertThat(fill.Icons.Count).IsEqual(2);
		AssertThat(fill.Icons[1].Texture).IsEqual(0x0000CAFEu);

		Engine_UiExpandableMenu.Item region = MapRow("Oakfield", 41, 1002);
		AssertThat(Engine_UiMinimapPage.Values(region).Count).IsEqual(0);  // the page does not stand yet
		Engine_UiMinimapPage.DriveFlip(true, 1, 1f);
		AssertThat(Engine_UiMinimapPage.Values(region)[page2 + "Legend.KeyName1"]).IsEqual("Hero");
		AssertThat(Engine_UiMinimapPage.Textures(region)[page2 + "Legend.KeyImage1"]).IsEqual(0x0000BEEFu);
		AssertThat(Engine_UiMinimapPage.Active(region)[page2 + "Legend.KeyName10"]).IsFalse();
		AssertThat(Engine_UiMinimapPage.Rotations(region)[page2 + "Bowerstone"]).IsEqual(1.5708f);

		List<Engine_UiMinimapPage.Marker> markers = Engine_UiMinimapPage.Markers(region, new Godot.Vector3(10f, 20f, 0f));
		AssertThat(markers.Count).IsEqual(2);
		// The centre of the region (n = 0.5) sits on the map image's own position.
		AssertThat(markers[0].Position.X).IsEqualApprox(10f, 0.001f);
		AssertThat(markers[0].Position.Y).IsEqualApprox(20f, 0.001f);
		// (270 − (0 + 90))·π/180 − atan2(1, 0) = π − π/2.
		AssertThat(markers[0].Rotation).IsEqualApprox(System.MathF.PI / 2f, 0.0001f);
		AssertThat(markers[1].Rotation).IsEqual(0f);
		AssertThat(markers[1].Depth).IsEqualApprox(0.1f, 0.0001f);

		// Another region before its statement lands: blank legend, no markers.
		Engine_UiExpandableMenu.Item other = MapRow("Bowerstone Market", 41, 1003);
		AssertThat(Engine_UiMinimapPage.Values(other)[page2 + "Legend.KeyName1"]).IsEqual(string.Empty);
		AssertThat(Engine_UiMinimapPage.Active(other)[page2 + "Legend.KeyName1"]).IsFalse();
		AssertThat(Engine_UiMinimapPage.Markers(other, new Godot.Vector3(10f, 20f, 0f)).Count).IsEqual(0);

		// A malformed projection is dropped, not half-read.
		Engine_UiPageFills.Filled(Call("23", "row.minimap", "1004", "map", "Page2", "90 0 nan"));
		AssertThat(Engine_UiPageFills.ForRow(23, "row.minimap", 1004)!.Projection).IsNull();
		Engine_UiMinimapPage.Reset();
		Engine_UiPageFills.Reset();
	}

	/// <summary>The items book's row fill: its texts by path, a hidden path blank, the swatch's tint as a colour of its
	/// own (blue, green, red, alpha in memory, stated RRGGBBAA), and nothing from another row's statement.</summary>
	[TestCase]
	public void AnItemBooksPageIsItsRowsOwnFill()
	{
		const string page2 = "Foreground.Book.Pages.Page2.Contents.";
		Engine_UiPageFills.Reset();
		Engine_UiPageFills.Filled(Call("15", "row", "7",
			"text", Page1 + "ItemName", "Health Potion",
			"shown", Page1 + "DyeSwatch", "1",
			"tint", Page1 + "DyeSwatch.dynamic", "FF8000FF",
			"text", page2 + "ItemValue", "$40",
			"shown", page2 + "ItemValue", "0",
			"slide", page2 + "StarRating", "4"));

		var row = new Engine_UiExpandableMenu.Item { Name = "INV_ITEM_POTION_HEALTH_NAME", EventType = 3, IdLow = 7, IdHigh = 0 };
		Engine_UiPageFills.Fill? fill = Engine_UiItemPage.Stated(15u, row);
		AssertThat(fill).IsNotNull();
		AssertThat(fill!.Tints[Page1 + "DyeSwatch.dynamic"].R8).IsEqual(255);
		AssertThat(fill.Tints[Page1 + "DyeSwatch.dynamic"].G8).IsEqual(128);
		AssertThat(fill.Colours.Count).IsEqual(0);
		AssertThat(fill.Slides[page2 + "StarRating"]).IsEqual(4);

		IReadOnlyDictionary<string, string> values = Engine_UiItemPage.StatedValues(15u, row);
		AssertThat(values[Page1 + "ItemName"]).IsEqual("Health Potion");
		AssertThat(values[page2 + "ItemValue"]).IsEqual(string.Empty);

		var other = new Engine_UiExpandableMenu.Item { Name = "INV_ITEM_PIE_NAME", EventType = 3, IdLow = 8, IdHigh = 0 };
		AssertThat(Engine_UiItemPage.Stated(15u, other)).IsNull();
		AssertThat(Engine_UiItemPage.StatedValues(15u, other)[Page1 + "ItemName"]).IsEqual(string.Empty);
		AssertThat(Engine_UiItemPage.StatesPages(22u)).IsFalse();
		Engine_UiPageFills.Reset();
	}
}
