using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Screen 23's right-hand pages, owned: which page stands for the highlighted row, and what the
/// guest's own page fill wrote on it.
///
/// The rows are the guest's population (`lhGuiWorldMapScreen_PopulateMenu` 0x829A2F40 / disc
/// 0x828F3EC0), so a row carries the guest's own (type, id): quests 42 and jobs 43 by their ordinal in
/// `lhWorldMap_BuildQuestList` / `BuildJobList`, sales 44 by their ordinal in `BuildSalesList`, family
/// 45 by their ordinal in `BuildFamilyList`, regions 41 by `index + 999` into `sub_82518330`. The
/// screen's ported Update runs the highlighted row's page fill — the page's vf +12, as the book's tick
/// does — and each fill finds its row by that id itself and writes its page by element name
/// (gui_screen_stub_world_map.cpp). Those writes are the statement: <see cref="Engine_UiPageFills"/>
/// holds one per page scope, keyed by the row's id, and this class hands them to the book by path.
/// Nothing on these pages is composed here.
///
/// Our Properties category (<see cref="Engine_UiPropertySource"/>) is the remaster's own: no guest page
/// states a property, so its rows take the sales page's title-and-body shape from that source.
/// </summary>
public static class Engine_UiWorldMapPage
{
	private const uint ScreenId = 23u;

	/// <summary>Row key of the first region: `sub_829A3A30` spawns `index + 999` (0x829A3B4C) and
	/// `sub_829AB0E0` takes 999 back off (0x829AB17C).</summary>
	private const int RegionKeyBase = 999;

	/// <summary>The guest's spawn types — `sub_829A3A30` 41, `sub_829A3BA0` 42, `sub_829A3E98` 43,
	/// `sub_829A40C0` 44, `sub_829A42C8` 45. The row's type IS its page.</summary>
	private const int RegionEventType = 41;

	private const int QuestEventType = 42;

	private const int JobEventType = 43;

	private const int SalesEventType = 44;

	private const int FamilyEventType = 45;

	/// <summary>
	/// The page fill a row type is stated under (gui_screen_stub_world_map.cpp `kRowPages`), null for a
	/// row no guest page describes.
	/// </summary>
	private static string? ScopeOf(int eventType) => eventType switch
	{
		RegionEventType => "row.region",
		QuestEventType => "row.quest",
		JobEventType => "row.job",
		SalesEventType => "row.sale",
		FamilyEventType => "row.family",
		_ => null,
	};

	/// <summary>
	/// The Economy row's five-star strip — `Master Slide` then `1star`..`5star`. The fill hands
	/// `GetEconomyRating + 1` (0x829AB374), and `sub_8246FE20` can return 5: the guest's slide 6 is
	/// refused by `sub_82BB5760` and the strip keeps what it showed, so the strip's last slide stands.
	/// </summary>
	private const string StarRating = "Foreground.Book.Pages.Page1.Contents.Text.StarRating";

	private const int StarSlides = 5;

	/// <summary>Which region the highlighted row names, or -1: the row's own id less the 999 the
	/// population added (the index the region and minimap fills take back, 0x829AB17C / 0x829AC05C).</summary>
	public static int RegionOf(Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || row.EventType != RegionEventType)
		{
			return -1;
		}

		uint key = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		long index = (long)key - RegionKeyBase;
		return index >= 0 && index < int.MaxValue ? (int)index : -1;
	}

	/// <summary>The highlighted row's own fill statement, when it has landed for this row.</summary>
	private static Engine_UiPageFills.Fill? Stated(Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || ScopeOf(row.EventType) is not { } scope)
		{
			return null;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		return Engine_UiPageFills.ForRow(ScreenId, scope, id);
	}

	/// <summary>
	/// The named category pages — `QuestsPage`, `JobsPage`, `SalesPage`, `FamilyPage`, and
	/// `FriendsPage` (never shown) — and the region pages `Page1` / `Page2`: the guest shows one by the
	/// highlighted row's type (`sub_826C6698`, jump table 0x826C6764) and hides the rest
	/// (`sub_826C6840`); stated here by the same type. Then what the row's fill showed and hid.
	/// </summary>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row)
	{
		var active = new Dictionary<string, bool>();
		foreach (string page in CategoryPages)
		{
			active[page] = false;
		}

		Engine_UiExpandableMenu.Item? root = row;
		while (root?.Parent is { } up && up.Parent is not null)
		{
			root = up;
		}

		// `worldmapscreen.bgf` authors SEVEN pages on the leaf: `Page1` (the region readout),
		// `Page2` (the minimap) and the five named category pages. All seven are switched.
		active[RegionPage] = false;
		active[MapPage] = false;

		// A FOLDER (a row with children) shows no page, as in every book.
		bool folder = row is null || row.Children.Count > 0 ||
			row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu;
		if (folder)
		{
			return active;
		}

		string? shown = row!.EventType switch
		{
			QuestEventType => QuestsPage,
			JobEventType => JobsPage,
			SalesEventType => SalesPage,
			FamilyEventType => FamilyPage,
			RegionEventType => RegionPage,
			_ => null,
		};

		// A leaf the types do not name falls back to its category folder's page, and to the region
		// page when it has none — our Properties rows, and rows a mod adds.
		if (shown is null && !ReferenceEquals(root, row) && root is not null)
		{
			shown = CategoryPageOf(root.Name) ?? RegionPage;
		}

		// THE MAP IS THE REGION PAGE'S OTHER LEAF: a stick-right turns the book from the readout
		// (`Page1`) to the minimap (`Page2`, `sub_82990E48` pushes page index 1), left turns it back.
		// The turn is host-owned — see Engine_UiMinimapPage.DriveFlip.
		if (ReferenceEquals(shown, RegionPage) && Engine_UiMinimapPage.Shown)
		{
			shown = MapPage;
		}

		if (shown is not null)
		{
			active[shown] = true;
		}

		// What the row's own fill showed or hid — the A-button and its caption (`sub_824708D8`,
		// `sub_82470A78`), the Mystery Island rows (`sub_829AB0E0` 0x829AB6C0..0x829AB738).
		if (Stated(row) is { } fill)
		{
			foreach ((string path, bool on) in fill.Shown)
			{
				active[path] = on;
			}
		}
		else if (ReferenceEquals(shown, SalesPage) && row.EventType != SalesEventType)
		{
			// A Properties row on the sales page: its A is the repair, not a sale's travel.
			active[SaleAButton] = false;
		}

		return active;
	}

	private const string QuestsPage = "Foreground.Book.Pages.QuestsPage";

	private const string JobsPage = "Foreground.Book.Pages.JobsPage";

	private const string SalesPage = "Foreground.Book.Pages.SalesPage";

	private const string SaleAButton = "SalesPage.Contents.AButton";

	private const string FamilyPage = "Foreground.Book.Pages.FamilyPage";

	/// <summary>`Page1` — the region readout.</summary>
	private const string RegionPage = "Foreground.Book.Pages.Page1";

	/// <summary>`Page2` — `MiniMap`, `Legend` and `Bowerstone`.</summary>
	private const string MapPage = "Foreground.Book.Pages.Page2";

	private static readonly string[] CategoryPages =
	{
		QuestsPage, JobsPage, SalesPage, FamilyPage, "Foreground.Book.Pages.FriendsPage",
	};

	private static string? CategoryPageOf(string rootName) => rootName switch
	{
		"Quests" => QuestsPage,
		"Jobs" => JobsPage,
		"Sales" => SalesPage,

		// Our Properties category takes the sales page's title-and-text shape (Engine_UiPropertySource.FillPage).
		"Properties" => SalesPage,
		"Family" => FamilyPage,
		_ => null,
	};

	/// <summary>The slides the row's fill gave: the region's economy strip, the job's two star strips.</summary>
	public static IReadOnlyDictionary<string, int> Slides(Engine_UiExpandableMenu.Item? row)
	{
		if (Stated(row) is not { Slides.Count: > 0 } fill)
		{
			return Engine_UiPageProviders.NoSlides;
		}

		var slides = new Dictionary<string, int>(fill.Slides, System.StringComparer.Ordinal);
		if (slides.TryGetValue(StarRating, out int stars) && stars > StarSlides)
		{
			slides[StarRating] = StarSlides;
		}

		return slides;
	}

	/// <summary>
	/// The page's texts as the row's fill wrote them, by path; an element the fill hid is blanked. Until
	/// the row's own statement lands (a guest tick behind the cursor) the page is blank rather than
	/// another row's: every path the page's last statement wrote is cleared.
	/// </summary>
	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row, Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(16, System.StringComparer.Ordinal);
		if (row is null)
		{
			return values;
		}

		if (ScopeOf(row.EventType) is { } scope)
		{
			if (Stated(row) is { } fill)
			{
				foreach ((string path, string text) in fill.Texts)
				{
					values[path] = text;
				}

				foreach ((string path, bool shown) in fill.Shown)
				{
					if (!shown)
					{
						values[path] = string.Empty;
					}
				}
			}
			else if (Engine_UiPageFills.Of(ScreenId, scope) is { } last)
			{
				foreach (string path in last.Texts.Keys)
				{
					values[path] = string.Empty;
				}
			}

			return values;
		}

		// A PROPERTIES ROW — a property's sign, or what a partnership row does.
		Engine_UiPropertySource.FillPage(values, row.Name, strings);
		return values;
	}
}
