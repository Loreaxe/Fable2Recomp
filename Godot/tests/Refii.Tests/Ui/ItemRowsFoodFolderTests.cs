namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>
/// The item book's Food folder as an online joiner's capture stated it (run 2026-11-28 10:36): the
/// guest's header `Food and Drink` (parent Root, label "Food (1)!", type 301) and one row under it,
/// `INV_ITEM_ALCOHOL_BEER_3_NAME` (type 3, id 1, rating 6). The folder's source,
/// `items:use:Food and Drink`, must give that row. Pure: no Godot type, no GDB (the row states no item).
/// </summary>
[TestSuite]
public class ItemRowsFoodFolderTests
{
	private const uint ItemsScreen = 15u;

	/// <summary>The capture of one screen's population.</summary>
	private sealed class CapturedSource : Engine_UiSource
	{
		public readonly List<Engine_UiModel.MenuRow> Rows = new();

		public override List<Engine_UiModel.MenuRow> GetMenuRows(out ulong generation)
		{
			generation = 1;
			return new List<Engine_UiModel.MenuRow>(Rows);
		}

		public override uint MenuScreenId() => ItemsScreen;
	}

	private static CapturedSource JoinersFoodCapture()
	{
		var source = new CapturedSource();
		source.Rows.Add(new Engine_UiModel.MenuRow("Food and Drink", "Root", 0u, 301, 0, "Food (1)<exclamation.img>"));
		source.Rows.Add(new Engine_UiModel.MenuRow(
			"INV_ITEM_ALCOHOL_BEER_3_NAME", "Food and Drink", 1u, 3, 6, "Bowerstone Brown Beer<exclamation.img>"));
		return source;
	}

	[TestCase]
	public void TheFoodFolderSourceGivesTheBeer()
	{
		Engine_UiSource was = Engine_UiSource.Current;
		uint bound = Engine_UiMenuSources.BoundScreen;
		try
		{
			Engine_UiSource.Current = JoinersFoodCapture();
			Engine_UiMenuSources.BoundScreen = ItemsScreen;

			IReadOnlyList<Engine_UiMenuSources.Row> rows = Engine_UiItemRows.Rows("use:Food and Drink");
			AssertThat(rows.Count).IsEqual(1);
			AssertThat(rows[0].Name).IsEqual("INV_ITEM_ALCOHOL_BEER_3_NAME");
			AssertThat(rows[0].EventType).IsEqual(3);
			AssertThat(rows[0].Id).IsEqual(1u);
			AssertThat(rows[0].Unviewed).IsTrue();
		}
		finally
		{
			Engine_UiSource.Current = was;
			Engine_UiMenuSources.BoundScreen = bound;
		}
	}

	[TestCase]
	public void AnotherCategoryTakesNoneOfTheBeer()
	{
		Engine_UiSource was = Engine_UiSource.Current;
		uint bound = Engine_UiMenuSources.BoundScreen;
		try
		{
			Engine_UiSource.Current = JoinersFoodCapture();
			Engine_UiMenuSources.BoundScreen = ItemsScreen;
			AssertThat(Engine_UiItemRows.Rows("use:Potions").Count).IsEqual(0);
			AssertThat(Engine_UiItemRows.Rows("use:Food").Count).IsEqual(0);
		}
		finally
		{
			Engine_UiSource.Current = was;
			Engine_UiMenuSources.BoundScreen = bound;
		}
	}
}
