using System.Collections.Generic;
using System.Globalization;
using Refii.Equipment;
using Godot;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The item detail page, owned — the right-hand page of the item, clothing, gift, furniture and
/// decoration books (screens 15, 16, 25, 26 and 27, one skeleton over three `.bgf`).
///
/// Same cause as <see cref="Engine_UiAbilityPage"/>: the guest fills this page on a
/// highlight-change edge keyed off ITS Lua highlight, Godot owns the cursor, so the key never
/// moves. See <see cref="Engine_UiDetailPages"/>.
///
/// No new reader — `Engine_ItemDetails.BuildOne` already resolves an item's GDB record (name
/// tag, description tag, value) and is what the bank browser reads.
///
/// Every field the page shows — name, description, value, value caption, `Info` — is the host's.
/// </summary>
public static class Engine_UiItemPage
{
	/// <summary>
	/// Keyed without a page, so the one entry reaches both copies — the item books put the
	/// name on Page1 and Page2 exactly as the abilities book does.
	/// </summary>
	private const string ItemName = "Contents.ItemName";

	private const string ItemDescription = "Contents.ItemDescription";

	/// <summary>
	/// The item's value. Matching is on whole trailing segments, and neither `ItemValueName` nor
	/// `ItemValue2` ends with `Contents.ItemValue`, so this reaches only the element it names.
	/// </summary>
	private const string ItemValue = "Contents.ItemValue";

	/// <summary>
	/// The `GUI_SCREEN_SHOP_VALUE` caption beside the price. Hidden WITH the price when the
	/// record has no `ShopItemComponent` — an item with no shop price has no value line at all,
	/// and leaving the caption up with nothing under it is what the guest avoids here.
	/// </summary>
	private const string ItemValueName = "Contents.ItemValueName";

	/// <summary>
	/// THE EQUIPMENT BOOK SPELLS IT DIFFERENTLY. `equipmentscreen.bgf` names the same caption
	/// `ItemValue2`, so writing only `ItemValueName` left screen 16's value line with no host
	/// source at all — one of the four keys a run reported as matching no element there.
	/// </summary>
	private const string EquipmentValueName = "Contents.ItemValue2";

	/// <summary>Page1's one-line summary — the primary stat's label and value, space-joined.</summary>
	private const string Info = "Contents.Info";

	/// <summary>The equipment book's own Info, under `TypeSpecific`.</summary>
	private const string EquipmentInfo = "TypeSpecific.Info";

	/// <summary>The star strip. The GDB `Rating` integer IS the slide index — it is not binned.</summary>
	private const string StarRating = "Contents.StarRating";

	private const string ValueCaptionTag = "GUI_SCREEN_SHOP_VALUE";

	/// <summary>
	/// The shop spells the same two values differently — `shopscreen.bgf` hangs the description
	/// under `Text.Group.Group` and calls the item's worth `ValueValue` — so both spellings ride
	/// in one set; a key matching nothing on a screen is never used.
	///
	/// `PriceValue` is deliberately absent: what the shop CHARGES is the shopkeeper's number
	/// after markup, not the item's worth.
	/// </summary>
	private const string ShopDescription = "Text.Group.Group.ItemDescription";

	private const string ShopValue = "TextElements.ValueValue";

	/// <summary>
	/// What the shop CHARGES, which is a different number from what the item is worth —
	/// `sub_82992F90` writes this element and `ValueValue` from two different functions.
	/// </summary>
	private const string ShopAskingPrice = "TextElements.PriceValue";

	/// <summary>The element the price display writes (`dword_8349CF34`).</summary>
	private const string ShopPricePath = "Foreground.Book.Pages.Page1.Contents.Text.TextElements.PriceValue";

	private const uint ShopScreenId = 22;

	/// <summary>What the shop's row edge wrote, when it ran for the row with this ticket.</summary>
	private static Engine_UiPageFills.Fill? ShopRowFill(uint ticket) =>
		ticket == 0u ? null : Engine_UiPageFills.ForRow(ShopScreenId, ticket);

	/// <summary>The shop page's damage row — value and its label, written together.</summary>
	private const string ShopDamageValue = "TextElements.DamageValue";

	private const string ShopDamageLabel = "TextElements.Damage";

	private const string ShopInfo = "TextElements.Info";

	/// <summary>The guest's own UI codes for the two shop transactions (`sub_826AE5C0`).</summary>
	private const int BuyEventType = 30;

	private const int SellEventType = 31;

	/// <summary>
	/// `CreateMenuItem` event types that mean "category, not an item" — the same two the
	/// abilities fill checks (`sub_826C3C78` at `0x826c3d0c`).
	/// </summary>
	private const int SubmenuEventType = 301;

	private const int SpellAssignEventType = 23;

	/// <summary>
	/// Whether <paramref name="screen"/>'s pages are its own fills' statements: the items book (15:
	/// `sub_829B5160` page 1 and `sub_829B6B50` page 2), the clothing book (16: `sub_829B6310` / `sub_829B7368`)
	/// and the gift book (25: `sub_829AA588`), stated as the "row" fill for the highlighted row's ticket
	/// (gui_screen_stub_items.cpp, gui_screen_stub_clothing.cpp, gui_screen_stub_gift.cpp).
	/// </summary>
	public static bool StatesPages(uint screen) => screen is 15 or 16 or 25;

	/// <summary>The fill the book stated for <paramref name="row"/>, or null (a category, or a row whose fill has
	/// not landed yet — one guest tick behind the cursor).</summary>
	public static Engine_UiPageFills.Fill? Stated(uint screen, Engine_UiExpandableMenu.Item? row) =>
		StatesPages(screen) && row is { } stated && !string.IsNullOrEmpty(stated.Name) && stated.EventType != SubmenuEventType
			? Engine_UiPageFills.ForRow(screen, ((uint)stated.IdHigh << 16) | ((uint)stated.IdLow & 0xFFFFu))
			: null;

	/// <summary>The paths each book's fills have written, so a row's page blanks what another row's statement wrote.</summary>
	private static readonly Dictionary<uint, HashSet<string>> StatedPaths = new();

	private static HashSet<string> PathsOf(uint screen) =>
		StatedPaths.TryGetValue(screen, out HashSet<string>? paths) ? paths : StatedPaths[screen] = new(System.StringComparer.Ordinal);

	/// <summary>
	/// A stated book's page texts, by the full path its fill named: the name, the info line, the A prompt's
	/// caption, the value caption and amount, the description with the augment lines (`sub_82469AE8`, joined
	/// with `&lt;bullet.img&gt;` 0x829B702C), the dye footer's apply / scrub wording, the None row's name. A path
	/// the fill hid is blank; so is every path an earlier statement wrote until this row's lands.
	/// </summary>
	public static IReadOnlyDictionary<string, string> StatedValues(uint screen, Engine_UiExpandableMenu.Item? row)
	{
		HashSet<string> paths = PathsOf(screen);
		var values = new Dictionary<string, string>(paths.Count, System.StringComparer.Ordinal);
		foreach (string path in paths)
		{
			values[path] = string.Empty;
		}

		if (Stated(screen, row) is not { } fill)
		{
			return values;
		}

		foreach ((string path, string text) in fill.Texts)
		{
			paths.Add(path);
			values[path] = fill.Shown.TryGetValue(path, out bool shown) && !shown ? string.Empty : text;
		}

		return values;
	}

	/// <summary>The detail values for the highlighted row, or blanks.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(3)
		{
			[ItemName] = string.Empty,
			[ItemDescription] = string.Empty,
			[ItemValue] = string.Empty,
			[ShopDescription] = string.Empty,
			[ShopValue] = string.Empty,
			[ShopAskingPrice] = string.Empty,

			// `sub_82992998` writes the shop's `Info` as an EMPTY string on every item row
			// (0x82992b88..bb8): the authored "Info:" caption never shows.
			[ShopInfo] = string.Empty,
		};

		ShopLayout = (0, 0);
		if (row is null || string.IsNullOrEmpty(row.Name) ||
			row.EventType == SubmenuEventType || row.EventType == SpellAssignEventType)
		{
			return values;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return values;
		}

		// Two candidate keys, because a row's id means something different per screen: the
		// logbook's expression rows carry a GDB record HASH (`sub_829BCDF0` feeds it to
		// `sub_821DA348`), the abilities rows a record NAME, and these books resolve theirs
		// through a `std::map` at `menuMgr+4` into a 16-byte ENTITY HANDLE (`sub_826D1698` at
		// `0x826d1754`). Both are probed; a lookup succeeds only on an exact record-hash match.
		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);

		// THE ASKING PRICE, before the record lookup — a shop row whose GDB record does not
		// resolve still has a price, and it is the one number on that page the item book
		// cannot answer. On a shop row the id IS the per-population ticket, and the event
		// type is the transaction: 30 buy, 31 sell. The item page's price display
		// `sub_82992F90` writes it — `"$"` (`word_820CEE80`) and the shopkeeper's
		// `CECShopkeeper_BuyPrice` / `SellPrice` (0x82993148 / 0x82993150) — from the row edge,
		// which states the row it ran for (Engine_UiPageFills, keyed by the ticket).
		if (row.EventType == BuyEventType || row.EventType == SellEventType)
		{
			values[ShopAskingPrice] = ShopRowFill(id) is { } rowFill &&
				rowFill.Texts.TryGetValue(ShopPricePath, out string? asking)
					? asking
					: string.Empty;
		}

		ItemDetail? detail = Resolve(multi, row, id);

		if (detail is null)
		{
			// The identity currency is not settled here. Abilities rows carry a GDB record name
			// (`AbilityPhysiqueBase`) and resolve by `Fnv1(name)`; the item books' own filler
			// resolves `(Float2<<16)|Float1` through the `menuMgr+4` map into an entity handle
			// and reads the live entity's def, never the spawn name. The first miss per row name
			// is reported so the answer comes from a run rather than an assumption. A shop row
			// whose ticket the bridge has not answered yet is pending, not unresolved.
			bool shopPending = (row.EventType == BuyEventType || row.EventType == SellEventType) &&
				ShopRowFill(id) is null;
			if (!shopPending && _unresolved.Add(row.Name))
			{
				GD.Print($"[item-page] '{row.Name}' (type {row.EventType}, label " +
					$"'{row.StatedLabel}') is not a GDB record — the item books may key on the " +
					"entity handle rather than the spawn name");
			}

			// The row's own label, already localised by the guest at spawn: an unresolved record
			// costs the description and the value, not the name.
			values[ItemName] = row.StatedLabel ?? string.Empty;
			return values;
		}

		// Resolve name and description via Engine_ItemNames (same single source of truth as editor):
		string name = Engine_ItemNames.DisplayNameOf(detail);
		if (string.IsNullOrEmpty(name) || name == detail.Label)
		{
			string babelName = Babel(strings, detail.NameTagName);
			if (!string.IsNullOrEmpty(babelName))
			{
				name = babelName;
			}
		}
		values[ItemName] = !string.IsNullOrEmpty(name) ? name : (row.StatedLabel ?? string.Empty);

		string desc = Engine_ItemNames.DescriptionOf(detail);
		if (string.IsNullOrEmpty(desc))
		{
			desc = Babel(strings, detail.DescTagName);
		}
		values[ItemDescription] = desc;
		values[ShopDescription] = values[ItemDescription];

		// `Money` is -1 when unset, not 0 — only an unresolved value is blank. The prefix is the
		// guest's: `sub_829B6B50` writes `ItemValue` as the wide literal at `0x820CEE80`
		// (`L"$"`) followed by `itoa(BasePrice)`; the shop's `ValueValue` takes the same form.
		int worth = detail.BasePrice >= 0 ? detail.BasePrice : detail.Money;
		values[ItemValue] = worth >= 0
			? "$" + worth.ToString(CultureInfo.InvariantCulture)
			: string.Empty;
		values[ShopValue] = values[ItemValue];

		// THE STAT LINES, THE AUGMENTS AND THE SUMMARY — everything Page2 says beyond the
		// description. `sub_829B6EAC` builds one blob and puts it in `ItemDescription`:
		//
		//     for each stat:   label + " " + value + "\n"
		//     if augments:     Babel("GUI_SCREEN_AUGMENT_AUGMENTS") + "\n"
		//     for each:        "<bullet.img>" + name + "\n"
		//     then:            the description itself
		//
		// Without it every item read as three lines whatever it was — a sword and a pie looked
		// the same. See Engine_UiItemStats for where each number comes from.
		// The inventory keys its records by the DEFINITION's id (`item+4`), which is what the
		// augment read is asked by.
		uint definition = detail.DefinitionHash != 0 ? detail.DefinitionHash : detail.RecordHash;
		bool shopRow = row.EventType == BuyEventType || row.EventType == SellEventType;

		// The books left here are the shop's, the furniture's and the decoration's (the item books state their
		// own pages, StatedValues): a shop row's item is the SHOP's, so the definition's own seed is what is read.
		Engine_UiItemStats.Stats stats = Engine_UiItemStats.For(definition, strings, null);
		if (shopRow)
		{
			ShopStatsPage(values, stats, strings);
		}

		// THE SHOP'S ONE STAT LINE. `sub_82992998` writes `Damage` / `DamageValue` from the
		// detail builder's PRIMARY pair (`detail+0x08` / `+0x0C`, 0x82992c08..c20) for every
		// category — a weapon's damage, a potion's health, a garment's largest change — and both
		// blank when the category states none. The authored `Health` pair is never addressed.
		// Its worth is `"$" + BasePrice` with the local starting at 0 (`sub_829931A8`), so an
		// item without a `ShopItemComponent` reads `$0` rather than nothing.
		if (shopRow)
		{
			values[ShopDamageLabel] = stats.Primary.Label;
			values[ShopDamageValue] = stats.Primary.Value;
			values[ShopValue] = "$" + System.Math.Max(detail.BasePrice, 0).ToString(CultureInfo.InvariantCulture);
		}
		if (stats.Any)
		{
			var body = new System.Text.StringBuilder();
			foreach (Engine_UiItemStats.Line line in stats.Lines)
			{
				body.Append(line.Label).Append(' ').Append(line.Value).Append('\n');
			}

			if (stats.Augments.Count > 0)
			{
				body.Append(Babel(strings, "GUI_SCREEN_AUGMENT_AUGMENTS")).Append('\n');
				foreach (string augment in stats.Augments)
				{
					body.Append(Engine_UiItemStats.Bullet).Append(augment).Append('\n');
				}
			}

			body.Append(values[ItemDescription]);
			values[ItemDescription] = body.ToString();
		}

		// Page1's one-liner — the primary stat, which is the largest appearance change on a
		// garment and the damage on a weapon.
		if (stats.Primary.Label.Length != 0)
		{
			string summary = $"{stats.Primary.Label} {stats.Primary.Value}";
			values[Info] = summary;
			values[EquipmentInfo] = summary;
		}

		// The caption only stands when there is a price to caption — the VALUE's own emptiness
		// decides, not `stats.Priced`: an item without a `ShopItemComponent` has no worth to
		// show (`Money` is −1 and the guest skips the line), and a bare "Base Value:" over
		// nothing is what every weapon page showed.
		values[ItemValueName] = values[ItemValue].Length != 0
			? Babel(strings, ValueCaptionTag)
			: string.Empty;
		values[EquipmentValueName] = values[ItemValueName];

		// One line per row, change-keyed: a page that fills is as worth seeing as one that does
		// not. The provider was silently absent for the whole item-book family, so "the page is
		// blank" and "the page is not ours" looked identical from a log.
		string filled = $"{row.Name}|{values[ItemName]}|{values[ItemValue]}";
		if (_reported != filled)
		{
			_reported = filled;
			GD.Print($"[item-page] '{values[ItemName]}' — value {(values[ItemValue].Length == 0
				? "unstated" : values[ItemValue])}, description " +
				$"{(values[ItemDescription].Length == 0 ? "MISSING" : "resolved")}, " +
				$"record {detail.RecordHash:X8} definition {detail.DefinitionHash:X8} " +
				$"({(stats.Lines.Count)} stat line(s), {stats.Augments.Count} slot(s)), " +
				$"preview mesh {detail.PreviewMesh:X8} worn {detail.WornModelPathHash:X8} " +
				$"model {detail.ModelPathHash:X8}");
		}

		return values;
	}

	/// <summary>
	/// THE SHOP'S STATS PAGE IS A GRID, NOT A BLOB. `sub_82993408` (screen 22's Page2 filler, run
	/// on a highlight change) writes the detail builder's two vectors row by row: `Stat{i}` /
	/// `StatValue{i}` for i &lt; 8 (0x82993c78 `cmpwi r29, 8`), then `Augments` with
	/// `GUI_SCREEN_AUGMENT_AUGMENTS` and `Augment{i}` for i &lt; 5 (0x82993e58), each slot's name
	/// bare. Rows past the item's count are hidden (<see cref="ShopStatsActive"/>), and the
	/// description is the description alone — the item books' concatenated block is
	/// `sub_829B6EAC`'s, a different page. `Health`, `Drunkeness` and `Corruption` are authored
	/// and never addressed: food and potions report through the same Stat rows.
	/// </summary>
	private static void ShopStatsPage(
		Dictionary<string, string> values,
		Engine_UiItemStats.Stats stats,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		int lines = System.Math.Min(stats.Lines.Count, ShopStatRows);
		int augments = System.Math.Min(stats.Augments.Count, ShopAugmentRows);
		ShopLayout = (lines, augments);

		for (int i = 0; i < lines; i++)
		{
			values[$"{ShopStatGroup}.Stat{i + 1}"] = stats.Lines[i].Label;
			values[$"{ShopStatGroup}.StatValue{i + 1}"] = stats.Lines[i].Value;
		}

		values[$"{ShopAugmentGroup}.Augments"] = Babel(strings, "GUI_SCREEN_AUGMENT_AUGMENTS");
		for (int i = 0; i < augments; i++)
		{
			values[$"{ShopAugmentGroup}.Augment{i + 1}"] = stats.Augments[i];
		}
	}

	/// <summary>
	/// Which rows of the shop's stats page stand, as switches over Page2: the `Stats` caption
	/// and its rule only when there are stat rows, `Augments` and `line2` only when the item has
	/// slots, and every unused row off (the guest fades them through opacity and leaves their
	/// authored "Base Value:" text behind).
	/// </summary>
	public static void ShopStatsActive(Dictionary<string, bool> active, string page)
	{
		(int lines, int augments) = ShopLayout;
		active[$"{page}.Contents.{ShopStatGroup}.Stats"] = lines > 0;
		active[$"{page}.Contents.{ShopStatGroup}.line"] = lines > 0;
		for (int i = 0; i < ShopStatRows; i++)
		{
			active[$"{page}.Contents.{ShopStatGroup}.Stat{i + 1}"] = i < lines;
			active[$"{page}.Contents.{ShopStatGroup}.StatValue{i + 1}"] = i < lines;
		}

		active[$"{page}.Contents.{ShopAugmentGroup}.Augments"] = augments > 0;
		active[$"{page}.Contents.{ShopAugmentGroup}.line2"] = augments > 0;
		for (int i = 0; i < ShopAugmentRows; i++)
		{
			active[$"{page}.Contents.{ShopAugmentGroup}.Augment{i + 1}"] = i < augments;
		}
	}

	/// <summary>
	/// The shop stats page closes up around what it shows: `sub_82993408` writes `Group.Group`'s
	/// y as `n ? (8 − n)·30 − 144 : 0` (0x82993d6c) and `ItemDescription`'s as
	/// `(m ? (5 − m)·30 : 175) + (n == 0 ? 144 : 0) − 115` (0x82993e84), x and z kept — the
	/// authored layout is the full eight-and-five case (−144, −115).
	/// </summary>
	public static void ShopStatsPositions(Dictionary<string, Vector3> positions, string page)
	{
		(int lines, int augments) = ShopLayout;
		float group = lines > 0 ? ((ShopStatRows - lines) * ShopRowPitch) - 144f : 0f;
		float description = (augments > 0 ? (ShopAugmentRows - augments) * ShopRowPitch : 175f) +
			(lines == 0 ? 144f : 0f) - 115f;
		positions[$"{page}.Contents.{ShopAugmentGroup}"] = new Vector3(0f, group, 0f);
		positions[$"{page}.Contents.{ShopAugmentGroup}.ItemDescription"] = new Vector3(30f, description, 0f);
	}

	/// <summary>The current shop row's stat and augment counts, as its page was last filled.</summary>
	private static (int Lines, int Augments) ShopLayout;

	private const string ShopStatGroup = "Text.Group";
	private const string ShopAugmentGroup = "Text.Group.Group";
	private const int ShopStatRows = 8;
	private const int ShopAugmentRows = 5;
	private const float ShopRowPitch = 30f;

	private static ItemDetail? Resolve(GdbMulti multi, Engine_UiExpandableMenu.Item row, uint id)
	{
		// A shop row's item is the record the row edge's item fill set the preview to
		// (`sub_828C3B30(page+32, item+4)` 0x82992c5c), stated with the fill for this ticket.
		if (row.EventType == BuyEventType || row.EventType == SellEventType)
		{
			return ShopRowFill(id) is { } filled && filled.Records.TryGetValue("preview", out uint record) && record != 0u
				? Engine_ItemDetails.BuildOne(multi, record)
				: null;
		}

		// An item book's row is the guest's own: its id is the population ticket and its item the one its row
		// builder `sub_826CE230` was handed (Engine_UiItemRows).
		if (row.Instance != 0u && Engine_UiItemRows.ItemOf(id) is { Record: not 0u } stated)
		{
			return Engine_ItemDetails.BuildOne(multi, stated.Record);
		}

		return Engine_ItemDetails.BuildOne(multi, id)
			?? Engine_ItemDetails.BuildOne(multi, Refii.Core.Engine_GameHash.Fnv1(row.Name))
			?? ByNameTag(multi, row.Name);
	}

	/// <summary>
	/// The item whose NAME TAG the row carries. The item books spawn rows with the record's
	/// `NameTag` as `String1` (`INV_ITEM_BOWER_LAKE_TOMB_SEAL_NAME`), not the record name, so
	/// `Fnv1(row.Name)` is no record's hash; the tag indexes every record once per database.
	/// </summary>
	private static ItemDetail? ByNameTag(GdbMulti multi, string nameTag)
	{
		if (nameTag.Length == 0)
		{
			return null;
		}

		if (!ReferenceEquals(_indexedFor, multi))
		{
			_byNameTag.Clear();
			foreach (ItemDetail item in Engine_ItemDetails.Build(multi))
			{
				if (item.NameTagName.Length > 0)
				{
					_byNameTag.TryAdd(item.NameTagName, item);
				}
			}

			_indexedFor = multi;
		}

		return _byNameTag.TryGetValue(nameTag, out ItemDetail? found) ? found : null;
	}

	/// <summary>
	/// THE ITEM'S PICTURE, on Page1. Dumped from the shipped files, the three item books put it
	/// at `Page1.Contents.ItemImage` (gift, shop) and `Page1.Contents.TypeSpecific.ItemImage`
	/// (equipment), so both tails are stated and a key matching nothing is simply unused.
	///
	/// The channel is a RESOURCE-PATH HASH, as the abilities page's tarot frame is. `IconGraphic`
	/// resolves in the shipped databases for 561 of its 562 records and already carries the
	/// converted extension — `art\gui\wheelicons\icon_quest_giver.tex` — so the extension is
	/// REPLACED rather than appended, which leaves a `.tex` value untouched and still corrects
	/// the handful authored against a source file.
	/// </summary>
	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row)
	{
		var textures = new Dictionary<string, uint>(2);
		if (row is null || string.IsNullOrEmpty(row.Name) ||
			row.EventType == SubmenuEventType || row.EventType == SpellAssignEventType ||
			Engine_GameGdb.Database is not { } multi)
		{
			return textures;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		ItemDetail? detail = Resolve(multi, row, id);
		if (detail is null || detail.IconTex.Length == 0)
		{
			return textures;
		}

		string path = detail.IconTex;
		int dot = path.LastIndexOf('.');
		if (dot > 0)
		{
			path = path[..dot];
		}

		uint hash = Refii.Core.Engine_GameHash.ResourcePath(path + ".tex");
		textures[ItemImage] = hash;
		textures[EquipmentItemImage] = hash;
		return textures;
	}

	/// <summary>
	/// THE SPINNING PREVIEW for the row the player is on, resolved from the item's own record —
	/// for a screen whose preview the guest has not stated (Engine_UiPreviewItems). The placement
	/// is shipped data.
	///
	/// Returns null when the item names no mesh — most consumables do not, and an empty ring is
	/// correct for them.
	/// </summary>
	public static Fable2Bridge.UiPreviewObject? Preview(
		uint screenId, Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || string.IsNullOrEmpty(row.Name) ||
			row.EventType == SubmenuEventType || row.EventType == SpellAssignEventType ||
			Engine_GameGdb.Database is not { } multi)
		{
			ReportPreview($"'{row?.Name}' type {row?.EventType}: no candidate");
			return null;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);

		// Upgrade furniture's rows are LEVELS: the preview is the level's unlock item or the
		// installed card (Engine_UiFurniturePage.PreviewRecord).
		ItemDetail? detail = screenId == Engine_UiFurniturePage.ScreenId
			? Engine_UiFurniturePage.PreviewRecord(row) is { } furniture ? Engine_ItemDetails.BuildOne(multi, furniture) : null
			: Resolve(multi, row, id);
		if (detail is null)
		{
			ReportPreview($"'{row.Name}' id {id:X8}: no record");
			return null;
		}

		// The row's item INSTANCE, so the panel asks what dye the guest holds on this copy — the
		// dye is the instance's own record, and a second copy of a dyed record is not dyed.
		return FromDetail(screenId, detail, row.Name, row.Instance);
	}

	/// <summary>
	/// THE PREVIEW THE GUEST STATED: the record its preview set was handed for that screen
	/// (`sub_828C3B30`, Engine_UiPreviewItems), resolved the same way as a row's. Null when the
	/// record names no mesh or is not in the database.
	/// </summary>
	public static Fable2Bridge.UiPreviewObject? PreviewOfRecord(uint screenId, uint record)
	{
		if (record == 0u || Engine_GameGdb.Database is not { } multi ||
			Engine_ItemDetails.BuildOne(multi, record) is not { } detail)
		{
			ReportPreview($"stated record {record:X8} on screen {screenId}: no record");
			return null;
		}

		return FromDetail(screenId, detail, $"stated {record:X8}", 0u);
	}

	private static Fable2Bridge.UiPreviewObject? FromDetail(
		uint screenId, ItemDetail detail, string name, uint instance)
	{
		// WHICH MESH: `GUIScreenMesh` when the item states one, else the item's OWN model. The
		// weapon and clothing components state `GUIScreenMesh` as the null tag (0x811C9DC5,
		// FNV-1 of ""), so every weapon page drew an empty ring — while the definition's
		// `GraphicAppearanceStaticMeshComponent.ModelFile` (read into `WornModelPathHash`) is
		// the resource hash of the very model the guest spins there. The artist's offsets
		// (`GUIScreen*` on the component chain) apply to either.
		uint mesh = detail.PreviewMesh != 0
			? detail.PreviewMesh
			: detail.WornModelPathHash != 0
				? detail.WornModelPathHash
				: detail.ModelPathHash;

		ReportPreview($"'{name}' → mesh 0x{mesh:X8} " +
			$"(stated {detail.PreviewMesh:X8}, worn {detail.WornModelPathHash:X8}, " +
			$"model {detail.ModelPathHash:X8}) scale {detail.PreviewScale} axis {detail.PreviewRotationAxis} " +
			$"at {detail.PreviewTranslation}");

		if (mesh == 0)
		{
			return null;
		}

		return new Fable2Bridge.UiPreviewObject(
			screenId, mesh,

			// No guest entity behind it — this one is ours.
			Entity: 0,
			detail.PreviewTranslation,
			detail.PreviewRotation,
			detail.PreviewScale,
			detail.PreviewRotationAxis,
			Item: instance);
	}

	private const string ItemImage = "Contents.ItemImage";

	private const string EquipmentItemImage = "TypeSpecific.ItemImage";

	private static GdbMulti? _indexedFor;
	private static readonly Dictionary<string, ItemDetail> _byNameTag = new(System.StringComparer.Ordinal);

	/// <summary>Row names already reported as unresolved — one line each, not one per frame.</summary>
	private static readonly HashSet<string> _unresolved = new();

	/// <summary>The last fill reported, so a steady cursor does not print every frame.</summary>
	private static string _reported = string.Empty;

	private static string _previewReported = string.Empty;

	/// <summary>One line per change of the preview decision, whatever it was.</summary>
	private static void ReportPreview(string decided)
	{
		if (_previewReported != decided)
		{
			_previewReported = decided;
			GD.Print("[item-preview] " + decided);
		}
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && !string.IsNullOrEmpty(tag) &&
		strings.TryGetString(tag, out string value)
			? value
			: string.Empty;

	/// <summary>
	/// The shop row's GDB `Rating`, or 0 while its record is unresolved — `sub_82992998` takes
	/// the star slide from `detail+0x44` and applies it only for 1..5 (0x82992b38..b64).
	/// </summary>
	public static int ShopRating(Engine_UiExpandableMenu.Item? row, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (row is null || (row.EventType != BuyEventType && row.EventType != SellEventType) ||
			Engine_GameGdb.Database is not { } multi)
		{
			return 0;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		ItemDetail? detail = Resolve(multi, row, id);
		return detail is null
			? 0
			: Engine_UiItemStats.For(detail.DefinitionHash != 0 ? detail.DefinitionHash : detail.RecordHash, strings).Rating;
	}

	/// <summary>
	/// THE STAR STRIP. `sub_82426BA8(elem, ctx, details+0x44)` where `+0x44` is
	/// `sub_823BF820(entry, "Rating")` — the GDB integer goes STRAIGHT to the slide index, with
	/// no compare, divide or clamp anywhere on the item path. (The real-estate page's strip does
	/// bin a value; applying that rule here would be wrong, and only the shop range-checks it.)
	/// </summary>
	public static IReadOnlyDictionary<string, int> Slides(
		Engine_UiExpandableMenu.Item? row,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		if (row is null || string.IsNullOrEmpty(row.Name))
		{
			return Engine_UiPageProviders.NoSlides;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return Engine_UiPageProviders.NoSlides;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		ItemDetail? detail = Resolve(multi, row, id);
		if (detail is null)
		{
			return Engine_UiPageProviders.NoSlides;
		}

		int rating = Engine_UiItemStats.For(detail.DefinitionHash != 0 ? detail.DefinitionHash : detail.RecordHash, strings).Rating;
		return rating > 0
			? new Dictionary<string, int>(1) { [StarRating] = rating }
			: Engine_UiPageProviders.NoSlides;
	}
}
