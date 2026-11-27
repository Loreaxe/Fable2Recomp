using System.Collections.Generic;
using System.Globalization;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// SCREEN 28'S PAGES AS THEIR FILLS WROTE THEM (<see cref="Engine_UiPageFills"/>): page 1
/// `sub_82998FB0` and page 2 `sub_8299B068` at the open, page 1's tick `sub_8299A728` over them —
/// every text as the guest composed it, a row the fill hid blanked (the template blanks by value), the
/// star slide `sub_8299AF88` gave. <see cref="StatedValues"/> and <see cref="StatedSlides"/>.
///
/// The rest of this class is the WORLD MAP'S PROPERTY ROW (Engine_UiPropertySource): the remaster's
/// map properties have no guest page, so their numbers arrive from the guest's providers
/// (`Fable2Bridge.GetPropertyEstate`) and are captioned here under the sign's own tags.
///
/// Screen 28's two detail pages — the property Godot draws for the building the guest resolved.
///
/// THE SHAPE IS NOT THE ITEM BOOK'S, which is why no existing provider fitted. The screen's left
/// column is a list of COMMANDS (Buy, Sell, Rent Out, Evict…), not of properties: there is one
/// building, resolved when the screen opens, and no selection-change event to hook. So nothing
/// here is cursor-driven — both pages describe the same record.
///
/// The bridge hands over NUMBERS ONLY. Value, economy, income, the sale floor, the purchase price
/// and the three not-owned deltas are the guest's own answers, because each of them averages or
/// walks something we have no business reproducing. Everything below is presentation: which of
/// the four `Variable` rows stands, what its caption says, the gold prefix, the star bin, the
/// legend Page 2 carries. That division is the point — a modder replaces this file and the data
/// keeps arriving unchanged.
/// </summary>
public static class Engine_UiRealEstatePage
{
	/// <summary>TU1-normalized in-game screen id for the real-estate screen.</summary>
	public const uint ScreenId = 28u;

	/// <summary>
	/// Packs the <see cref="Fable2Bridge.RealEstate"/> flags tested by <c>RealEstateMenu</c>'s
	/// row conditions into a compact bitmask (known, owned, tenanted, business) so the list
	/// rebuilds whenever any condition-relevant property attribute changes.
	/// </summary>
	/// <param name="estate">The sampled real-estate snapshot.</param>
	/// <returns>
	/// Bitmask covering <see cref="Fable2Bridge.RealEstate.Known"/>,
	/// <see cref="Fable2Bridge.RealEstate.Owned"/>,
	/// <see cref="Fable2Bridge.RealEstate.HasTenant"/>, and
	/// <see cref="Fable2Bridge.RealEstate.IsBusiness"/>.
	/// </returns>
	public static int ConditionKey(in Fable2Bridge.RealEstate estate) =>
		(estate.Known ? 1 : 0) |
		(estate.Owned ? 2 : 0) |
		(estate.HasTenant ? 4 : 0) |
		(estate.IsBusiness ? 8 : 0);

	// Page one. Paths read out of the binary's own statics rather than guessed —
	// `Foreground.Book.Pages.Page1.Contents.Title` at 0x820EB14C, and so on.
	private const string Title = "Page1.Contents.Title";
	private const string Type = "Page1.Contents.Type";
	private const string OwnerLabel = "Page1.Group.Owner";
	private const string OwnerName = "Page1.Group.OwnerName";
	private const string BaseValue = "Page1.Group.BaseValueAmount";
	private const string Decoration = "Page1.Group.DecorationAmount";
	private const string Economy = "Page1.Group.EconomyAmount";
	private const string SalePrice = "Page1.Group.SalePriceAmount";
	private const string Bonuses = "Page1.Group.Bonuses";
	private const string BonusesList = "Page1.Group.BonusesList";

	// Page two.
	private const string Page2Title = "Page2.Contents.Title";
	private const string Address = "Page2.Contents.Group.AddressAmount";
	private const string History = "Page2.Contents.Group.HistoryAmount";

	/// <summary>`GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_*`, in bit order — `sub_8299A550`.</summary>
	private static readonly string[] TypeTags =
	{
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_HOME",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_DOCK",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_FARM",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_TOWNHALL",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_WAREHOUSE",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_SHOP",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_BAR",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_BORDELLO",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_UNIQUE",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_STALL",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_HOUSE",
		"GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_TEMPLE",
	};

	private const string RenterTag = "GUI_SCREEN_REAL_ESTATE_RENTER";
	private const string OwnerOtherTag = "GUI_SCREEN_REAL_ESTATE_OWNER_OTHER";
	private const string TenantOtherTag = "GUI_SCREEN_REAL_ESTATE_TENANT_OTHER";
	private const string SellingFeesTag = "GUI_SCREEN_REAL_ESTATE_SELLING_FEES";
	private const string RentTag = "GUI_RENT";
	private const string ProfitTag = "GUI_SCREEN_REAL_ESTATE_PROFIT";
	private const string NotVacantTag = "GUI_SCREEN_REAL_ESTATE_NOT_VACANT";
	private const string OwnerKilledTag = "GUI_SCREEN_REAL_ESTATE_OWNER_KILLED";
	private const string HeroOpinionTag = "GUI_SCREEN_REAL_ESTATE_HERO_OPINION";
	private const string HouseLegendTag = "GUI_SCREEN_REAL_ESTATE_HOUSE_LEGEND";
	private const string ShopLegendTag = "GUI_SCREEN_REAL_ESTATE_SHOP_LEGEND";

	/// <summary>The literal the guest prefixes every money string with; the font draws the coin.</summary>
	private const string Gold = "<gold_img>";

	private static readonly uint NameField = Engine_GameHash.Fnv1("BuildingName");
	private static readonly uint AddressField = Engine_GameHash.Fnv1("BuildingAddress");
	private static readonly uint AnecdotesField = Engine_GameHash.Fnv1("BuildingAnecdotes");
	private static readonly uint CanSellField = Engine_GameHash.Fnv1("CanSellOncePurchased");

	/// <summary>
	/// The hero's name on a property the hero owns is the hero's TITLE: `sub_824D8A10` takes
	/// the title record's `HeroTitleComponent` reference and that entry's `TextTag`, and the
	/// filler puts it through Babel. There is no "you" tag and no player name anywhere on the
	/// page.
	/// </summary>
	private static readonly uint HeroTitleComponentField = Engine_GameHash.Fnv1("HeroTitleComponent");

	private static readonly uint TextTagField = Engine_GameHash.Fnv1("TextTag");

	/// <summary>
	/// The bonus list is a TWO-LEVEL reference, not an array — `sub_82917F60` reads `Bonuses`
	/// as a type-6 entry reference (`sub_8227B820` asks for type 6, one value), resolves the
	/// GROUP entry it names, then enumerates THAT entry's properties and keeps the type-6 ones.
	/// Each of those references a bonus whose caption is its `ShortNameTextTag`.
	/// </summary>
	private static readonly uint BonusesField = Engine_GameHash.Fnv1("Bonuses");

	private static readonly uint ShortNameField = Engine_GameHash.Fnv1("ShortNameTextTag");

	/// <summary>`L"- "` at 0x820EB5E8, and the line break at 0x820ADF4C.</summary>
	private const string BonusBullet = "- ";

	private const uint ScreenId = 28;

	/// <summary>Both pages' texts as the fills wrote them, page 1's tick over page 1; a row a fill hid
	/// is blank. Empty before the screen's pages have been filled.</summary>
	public static IReadOnlyDictionary<string, string> StatedValues()
	{
		var values = new Dictionary<string, string>(32);
		foreach (string scope in new[] { "page1", "page2", Engine_UiPageFills.Tick })
		{
			if (Engine_UiPageFills.Of(ScreenId, scope) is not { } fill)
			{
				continue;
			}

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
				else if (!fill.Texts.ContainsKey(path) && values.TryGetValue(path, out string? hidden) && hidden.Length == 0)
				{
					values.Remove(path);
				}
			}
		}

		return values;
	}

	/// <summary>The slides the fills gave (the star strip, `sub_8299AF88`).</summary>
	public static IReadOnlyDictionary<string, int> StatedSlides()
	{
		var slides = new Dictionary<string, int>(2);
		foreach (string scope in new[] { "page1", "page2", Engine_UiPageFills.Tick })
		{
			if (Engine_UiPageFills.Of(ScreenId, scope) is { } fill)
			{
				foreach ((string path, int slide) in fill.Slides)
				{
					slides[path] = slide;
				}
			}
		}

		return slides.Count == 0 ? Engine_UiPageProviders.NoSlides : slides;
	}

	/// <summary>Both pages' text for a property the map names. Empty everywhere when no building has been resolved.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Fable2Bridge.RealEstate estate,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(20);
		if (!estate.Known)
		{
			return values;
		}

		string name = Tag(estate.Record, NameField, strings);
		values[Title] = name;
		values[Page2Title] = name;
		values[Address] = Tag(estate.Record, AddressField, strings);

		// Anecdotes, a blank line, then the legend. Bit 0 of the type mask is HOME; everything
		// else takes the SHOP legend — `(*(_DWORD *)(v2 + 28) & 1) != 0` at `sub_8299B068`.
		string anecdotes = Tag(estate.Record, AnecdotesField, strings);
		string legend = Babel(strings, (estate.TypeMask & 1u) != 0u ? HouseLegendTag : ShopLegendTag);
		values[History] = $"{anecdotes}\n\n{legend}";

		int type = estate.Type;
		values[Type] = type >= 0 && type < TypeTags.Length
			? Babel(strings, TypeTags[type])
			: string.Empty;

		// WHOEVER IS IN IT — `sub_82998FB0`'s three branches, ownership tested first
		// (0x8299959c). Rented out: the tenant's name under the RENTER caption. Owned and not
		// rented: the hero's title, and the caption keeps its authored text. Not owned: the
		// owner's name. The "OTHER" tags stand in for a name the guest could not resolve.
		if (estate.HasTenant)
		{
			values[OwnerLabel] = Babel(strings, RenterTag);
			values[OwnerName] = estate.Occupant.Length != 0
				? estate.Occupant
				: Babel(strings, TenantOtherTag);
		}
		else if (estate.Owned)
		{
			values[OwnerName] = HeroTitle(estate.TitleRecord, strings);
		}
		else
		{
			values[OwnerName] = estate.Occupant.Length != 0
				? estate.Occupant
				: Babel(strings, OwnerOtherTag);
		}

		values[BaseValue] = Money(estate.Value - estate.Economy - estate.Decoration);
		values[Decoration] = Money(estate.Decoration);
		values[Economy] = Money(estate.Economy);
		values[SalePrice] = Money(estate.SalePrice);

		foreach (var pair in VariableRows(estate, strings))
		{
			values[pair.Key] = pair.Value;
		}

		// THE BONUS LIST. An empty one is a legitimate state — the guest hides both elements
		// with `sub_82426328(..., 0)` rather than drawing an empty box — so the caption stands
		// only when there is something under it.
		string bonuses = BonusList(estate.BonusesRecord, strings);
		values[BonusesList] = bonuses;
		values[Bonuses] = bonuses.Length == 0
			? string.Empty
			: Babel(strings, "GUI_SCREEN_REAL_ESTATE_BONUSES");
		return values;
	}

	/// <summary>
	/// THE SIGN AS LINES, for a page with one text block (the world map's property row): the same
	/// values <see cref="Values"/> gives both pages, under the sign's own captions, in its order —
	/// type, owner, base value, decoration, economy, sale price, the context rows, bonuses, then
	/// page two's address and history. A caption whose value is empty is left out.
	/// </summary>
	public static IReadOnlyList<string> Lines(
		Fable2Bridge.RealEstate estate,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var lines = new List<string>(16);
		IReadOnlyDictionary<string, string> v = Values(estate, strings);
		string Get(string key) => v.TryGetValue(key, out string? text) ? text : string.Empty;
		void Add(string caption, string value)
		{
			if (value.Length != 0)
			{
				lines.Add(caption.Length != 0 ? $"{caption} <s1>{value}</s1>" : value);
			}
		}

		Add(string.Empty, Get(Type));
		Add(Get(OwnerLabel) is { Length: > 0 } label ? label : Babel(strings, "GUI_SCREEN_REAL_ESTATE_OWNER"),
			Get(OwnerName));
		Add(Babel(strings, "GUI_SCREEN_REAL_ESTATE_BASE_VALUE"), Get(BaseValue));
		Add(Babel(strings, "GUI_SCREEN_REAL_ESTATE_DECORATION"), Get(Decoration));
		Add(Babel(strings, "GUI_SCREEN_REAL_ESTATE_ECONOMY"), Get(Economy));
		Add(Babel(strings, "GUI_SCREEN_REAL_ESTATE_SALE_PRICE"), Get(SalePrice));
		for (int slot = 1; slot <= 4; ++slot)
		{
			Add(Get($"Page1.Group.Variable{slot}"), Get($"Page1.Group.Variable{slot}Amount"));
		}

		if (Get(BonusesList) is { Length: > 0 } bonuses)
		{
			lines.Add($"{Get(Bonuses)}<br>{bonuses.TrimEnd('\n').Replace("\n", "<br>")}");
		}

		Add(Babel(strings, "GUI_SCREEN_REAL_ESTATE_ADDRESS"), Get(Address));
		if (Get(History) is { Length: > 0 } history)
		{
			lines.Add($"{Babel(strings, "GUI_SCREEN_REAL_ESTATE_HISTORY")}<br>{history.Replace("\n", "<br>")}");
		}

		return lines;
	}

	/// <summary>
	/// The four context rows, exactly as `sub_82998FB0` chooses them.
	///
	/// OWNED: row 1 is the selling fee, and only when the record allows reselling; rows 2 and 3
	/// are always hidden; row 4 is the rent when there is a tenant, otherwise the profit — and
	/// the profit stands only for a BUSINESS with a positive income (`mask &amp; 0xEFE`, the guest's
	/// own test, which excludes HOME and UNIQUE).
	///
	/// NOT OWNED: rows 1 to 3 are the three price adjustments and row 4 is hidden.
	/// </summary>
	private static Dictionary<string, string> VariableRows(
		Fable2Bridge.RealEstate estate, Refii.Text.Engine_BabelDatabase? strings)
	{
		var rows = new Dictionary<string, string>(8);
		for (int slot = 1; slot <= 4; ++slot)
		{
			rows[$"Page1.Group.Variable{slot}"] = string.Empty;
			rows[$"Page1.Group.Variable{slot}Amount"] = string.Empty;
		}

		void Row(int slot, string tag, int amount)
		{
			rows[$"Page1.Group.Variable{slot}"] = Babel(strings, tag);
			rows[$"Page1.Group.Variable{slot}Amount"] = Money(amount);
		}

		if (estate.Owned)
		{
			if (CanSell(estate.Record))
			{
				Row(1, SellingFeesTag, estate.SellingFees);
			}

			if (estate.HasTenant)
			{
				Row(4, RentTag, estate.Income);
			}
			else if (estate.IsBusiness && estate.Income > 0)
			{
				Row(4, ProfitTag, estate.Income);
			}

			return rows;
		}

		Row(1, NotVacantTag, estate.NotVacant);
		Row(2, OwnerKilledTag, estate.OwnerKilled);
		Row(3, HeroOpinionTag, estate.HeroOpinion);
		return rows;
	}

	/// <summary>`&lt;gold_img&gt;` then a plain decimal — `sub_8230AE78` is `%d`, no separators.</summary>
	private static string Money(int amount) =>
		Gold + amount.ToString(CultureInfo.InvariantCulture);

	/// <summary>A GDB string property off the building's record, resolved through Babel.</summary>
	private static string Tag(uint record, uint field, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (Engine_UiInventorySource.Globals is not { } globals ||
			!globals.TryLookup(record, out GdbCursor cursor) ||
			!globals.TryFindInherited(cursor, field, (int)GdbFieldType.String, out _,
				out uint tagHash, out _) ||
			!cursor.View.Names.TryGetValue(tagHash, out string? tag) || tag.Length == 0)
		{
			return string.Empty;
		}

		// A tag that resolves to nothing is drawn verbatim, which is the guest's own last
		// resort in `sub_8230E0E8` — better a visible key than a blank line.
		return strings is { } book && book.TryGetString(tag, out string resolved) &&
			resolved.Length != 0
				? resolved
				: tag;
	}

	/// <summary>
	/// The building's bonuses, one `"- "` line each, exactly as `sub_82917F60` assembles them.
	///
	/// `Bonuses` on the component-184 record REFERENCES a group entry; the group's own
	/// properties are then enumerated and every type-6 one names a bonus. `InheritedFields` is
	/// the same walk the guest uses (`sub_82A00990`: root-most first, a child shadowing its
	/// ancestor, `parent` excluded) — and excluding `parent` matters here rather than being a
	/// detail, because `parent` is itself a type-6 reference and would list the base record as
	/// though it were a bonus.
	/// </summary>
	private static string BonusList(uint record, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (record == 0u || Engine_UiInventorySource.Globals is not { } globals ||
			!globals.TryLookup(record, out GdbCursor cursor) ||
			!globals.TryFindInherited(cursor, BonusesField, (int)GdbFieldType.Reference, out _,
				out uint group, out _) ||
			group == 0u || !globals.TryLookup(group, out GdbCursor groupCursor))
		{
			return string.Empty;
		}

		var body = new System.Text.StringBuilder();
		foreach ((uint _, GdbFieldType type, uint bonus) in globals.InheritedFields(groupCursor))
		{
			if (type != GdbFieldType.Reference || bonus == 0u)
			{
				continue;
			}

			string caption = Tag(bonus, ShortNameField, strings);
			if (caption.Length != 0)
			{
				body.Append(BonusBullet).Append(caption).Append('\n');
			}
		}

		return body.ToString();
	}

	/// <summary>
	/// The hero's current title, as `sub_824D8A10` reads it: the title record's
	/// `HeroTitleComponent` reference, then that entry's `TextTag`, through Babel. Empty when the
	/// hero holds no title (`*(comp57+92) == 0` skips the whole lookup) or the reference is
	/// missing, which the guest draws as an empty name.
	/// </summary>
	private static string HeroTitle(uint record, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (record == 0u || Engine_UiInventorySource.Globals is not { } globals ||
			!globals.TryLookup(record, out GdbCursor cursor) ||
			!globals.TryFindInherited(cursor, HeroTitleComponentField,
				(int)GdbFieldType.Reference, out _, out uint component, out _) ||
			component == 0u)
		{
			return string.Empty;
		}

		return Tag(component, TextTagField, strings);
	}

	/// <summary>`CanSellOncePurchased` — the record's own bool; false hides the selling fee.</summary>
	private static bool CanSell(uint record) =>
		Engine_UiInventorySource.Globals is { } globals &&
		globals.TryLookup(record, out GdbCursor cursor) &&
		globals.TryFindInherited(cursor, CanSellField, (int)GdbFieldType.Bool, out _,
			out uint value, out _) &&
		value != 0u;

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is { } book && book.TryGetString(tag, out string resolved) ? resolved : tag;
}
