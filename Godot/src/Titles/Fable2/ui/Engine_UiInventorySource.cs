using System.Collections.Generic;
using Refii.Core;
using Refii.Equipment;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE HERO'S INVENTORY FOR A MOD'S LIST, AND THE ITEM FACTS THE BOOKS SHARE.
///
/// The item books list the guest's own rows (<see cref="Engine_UiItemRows"/>: the populates' spawns, each
/// with the item its row builder was handed). What is left here: `inventory:usable[:&lt;p&gt;[:&lt;cats&gt;]]`,
/// a MOD'S list of what a player's hero can use, read from that player's hero
/// (<see cref="Engine_UiModListSources.Inventory"/>, the `query inventory` reply — never a menu), the mod
/// surface's half (Engine_UiInventoryMod.cs), the item category constants, the shipped database and string
/// book every Fable 2 page reads, and the record facts a row's command needs (`UsableFromInventory`, the
/// weapon class).
/// </summary>
public static partial class Engine_UiInventorySource
{
	/// <summary>The source name a mod's menu asks for: `inventory:usable[:&lt;p&gt;[:&lt;cats&gt;]]`.</summary>
	public const string SourceName = "inventory";

	/// <summary>
	/// The item CATEGORY enum, 1..26, as the guest's own switch names it — `sub_826CEB60`, whose
	/// jump table at 0x826CEBE8 is indexed by `Category - 1` and whose arms write the group name
	/// the row is filed under. The same table drives `sub_82430B58`.
	///
	/// The NAMES are not in the executable's strings as `CategoryWeapons` and friends — those
	/// live only in `globals.gdb`, where each is a field whose value is the integer. The binary
	/// holds the integer→display-name mapping, and the two agree everywhere they overlap
	/// (Weapons 1, Misc/Tools 2, Gifts 3, Food 5, Potions 6, Dyes 8, Books 16, Dog 24).
	///
	/// Corroborated against a real run: the item screen spawns its nine category rows in the
	/// order KeyItems, Books, Food and Drink, Potions, Gifts, Trophies, Augmentations, Dyes,
	/// Furniture (`sub_829B1578`), which is exactly what the screen shows.
	/// </summary>
	public static class Category
	{
		public const int Weapons = 1;
		public const int KeyItems = 2;
		public const int Gifts = 3;
		public const int Trophies = 4;
		public const int Food = 5;
		public const int Potions = 6;
		public const int Augmentations = 7;
		public const int Dyes = 8;

		/// <summary>`sub_829B2168` asks `sub_829B1FC0` for 9, 10 and 11 and names the rows Hair, Beard, Mustache.</summary>
		public const int Hair = 9;
		public const int Beard = 10;
		public const int Moustache = 11;
		public const int Furniture = 15;
		public const int Books = 16;
		public const int Hats = 17;
		public const int Coats = 18;
		public const int Shirts = 19;
		public const int Gloves = 20;
		public const int Pants = 21;
		public const int Shoes = 22;
		public const int Accessories = 23;
		public const int Dog = 24;
		public const int Suits = 25;

		/// <summary>
		/// Every integer the guest names `KeyItems`. 2 is the category proper; 9..14 and 26 are
		/// folded onto it by `sub_826CEB60`'s jump table, which is why hairstyles and makeup
		/// appear under KeyItems on the real screen — that is the shipped grouping, not a defect.
		/// </summary>
		public static readonly int[] KeyItemsFold = { 2, 9, 10, 11, 12, 13, 14, 26 };

		/// <summary>
		/// 14 and 24 name a group but the guest spawns no header row for them
		/// (`sub_82430B58` returns 0 — table slots 0x82430BB8 and 0x82430BE0).
		/// </summary>
		public static bool HasHeaderRow(int category) => category != 14 && category != 24;

		/// <summary>
		/// The Babel tag for a category's header row — `GUI_SCREEN_INVENTORY_CATEGORIES_*`, all
		/// of which resolve in the shipped book. Empty where the guest spawns no header (14, 24)
		/// or names no group.
		/// </summary>
		public static string TagOf(int category) => category switch
		{
			Weapons => Tag + "WEAPONS",
			2 or 9 or 10 or 11 or 12 or 13 or 26 => Tag + "TOOLS",
			Gifts => Tag + "GIFTS",
			Trophies => Tag + "TROPHIES",
			Food => Tag + "FOOD",
			Potions => Tag + "POTIONS",
			Augmentations => Tag + "AUGMENTATIONS",
			Dyes => Tag + "DYES",
			Furniture => Tag + "FURNITURE",
			Books => Tag + "BOOKS",
			Hats => Tag + "HATS",
			Coats => Tag + "COATS",
			Shirts => Tag + "SHIRTS",
			Gloves => Tag + "GLOVES",
			Pants => Tag + "PANTS",
			Shoes => Tag + "SHOES",
			Accessories => Tag + "MASKS",
			Suits => Tag + "SUITS",
			_ => string.Empty,
		};

		private const string Tag = "GUI_SCREEN_INVENTORY_CATEGORIES_";

		/// <summary>The group name a category files an item under, or empty for none.</summary>
		public static string NameOf(int category) => category switch
		{
			Weapons => "Weapons",
			2 or 9 or 10 or 11 or 12 or 13 or 14 or 26 => "KeyItems",
			Gifts => "Gifts",
			Trophies => "Trophies",
			Food => "Food and Drink",
			Potions => "Potions",
			Augmentations => "Augmentations",
			Dyes => "Dyes",
			Furniture => "Furniture",
			Books => "Books",
			Hats => "Hats",
			Coats => "Coats",
			Shirts => "Shirts",
			Gloves => "Gloves",
			Pants => "Pants",
			Shoes => "Shoes",
			Accessories or Dog => "Accessories",
			Suits => "Suits",
			_ => string.Empty,
		};
	}

	/// <summary>`globals.gdb`, for the item records: what a menu's path set, else the game's shared database.</summary>
	public static GdbMulti? Globals
	{
		get => _globals ?? Engine_GameGdb.Database;
		set => _globals = value;
	}

	private static GdbMulti? _globals;

	/// <summary>The string book, for the item name tags.</summary>
	public static Refii.Text.Engine_BabelDatabase? Strings { get; set; }

	/// <summary>What the last read produced, for the report.</summary>
	public static string Report { get; private set; } = "not read yet";

	private static readonly uint NameTagField = Engine_GameHash.Fnv1("NameTag");
	private static readonly uint RatingField = Engine_GameHash.Fnv1("Rating");
	private static readonly uint IconField = Engine_GameHash.Fnv1("IconGraphic");
	private static readonly uint VisibleField = Engine_GameHash.Fnv1("VisibleInGUI");
	private static readonly uint UsableField = Engine_GameHash.Fnv1("UsableFromInventory");
	private static readonly uint InventoryComponentField =
		Engine_GameHash.Fnv1("InventoryItemComponent");

	private static readonly List<Engine_UiMenuSources.Row> None = new();

	/// <summary>
	/// `inventory:usable[:&lt;p&gt;[:&lt;categories&gt;]]` — A MOD'S LIST of what player p's hero can use from the
	/// inventory (p 1 or 2; absent or 0 the menus' owner): only the rows `use` acts on (UsableFromInventory,
	/// the guest's own test), each named by its record for the row's typed subject (ModRowSubject: item,
	/// "0x…") and carrying no command, so the mod menu's rowCommand runs for it instead of the use. Read from
	/// that player's hero (Engine_UiModListSources.Inventory). Any other argument lists nothing.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		if (argument != UsableOperation && !argument.StartsWith(UsableOperation + ":", System.StringComparison.Ordinal))
		{
			Say(argument, "not a usable list — the item books are the guest's rows (Engine_UiItemRows)");
			return None;
		}

		string rest = argument.Length > UsableOperation.Length ? argument[(UsableOperation.Length + 1)..] : string.Empty;
		int colon = rest.IndexOf(':');
		int.TryParse(colon < 0 ? rest : rest[..colon], out int usablePlayer);
		HashSet<int>? wanted = ParseCategories(colon < 0 ? string.Empty : rest[(colon + 1)..]);
		List<Fable2Bridge.InventoryRecord> records = Engine_UiModListSources.Inventory(usablePlayer);

		var rows = new List<Engine_UiMenuSources.Row>(records.Count);
		int hidden = 0;
		foreach (Fable2Bridge.InventoryRecord record in records)
		{
			if ((wanted is not null && !wanted.Contains(record.Category)) || !Listable(record, true) ||
				!UsableFromInventory(record.RecordId))
			{
				++hidden;
				continue;
			}

			string tag = NameTagOf(record);
			rows.Add(new Engine_UiMenuSources.Row(
				Name: Refii.Mods.ModRowSubject.ItemRow(record.RecordId),
				Label: LabelOf(record, tag),
				Icon: IconOf(record),
				EventType: 0,
				Id: record.RecordId,
				Rating: RatingOf(record),
				Committable: false,
				Ghosted: false,
				Command: null,
				Counted: true,
				Unviewed: !record.Viewed,
				FromMod: true,
				Instance: record.Item));
		}

		Report = $"{rows.Count} usable row(s) from {records.Count} inventory record(s) of player {usablePlayer}" +
			(hidden == 0 ? string.Empty : $", {hidden} not listed");
		Say(argument, Report);
		return rows;
	}

	private static readonly Dictionary<string, string> Said =
		new(System.StringComparer.Ordinal);

	private static void Say(string argument, string report)
	{
		if (Said.TryGetValue(argument, out string? was) && was == report)
		{
			return;
		}

		Said[argument] = report;
		Godot.GD.Print($"[inventory-source] '{argument}': {report}");
	}

	/// <summary>
	/// What the hero carries in <paramref name="category"/>, by record with its label — the decorate mode's
	/// furniture cards (GameDecorate), read from the hero's inventory (<see cref="Fable2Bridge.GetHeroInventory"/>),
	/// not from a menu.
	/// </summary>
	public static List<(uint Record, string Label)> Carried(int category)
	{
		var carried = new List<(uint, string)>();
		foreach (Fable2Bridge.InventoryRecord record in Fable2Bridge.GetHeroInventory())
		{
			if (record.Category == category && Listable(record, true))
			{
				carried.Add((record.RecordId, LabelOf(record, NameTagOf(record))));
			}
		}

		return carried;
	}

	/// <summary>`inventory:usable` — only what `use` acts on, as typed rows for a mod's menu.</summary>
	public const string UsableOperation = "usable";

	/// <summary>
	/// Whether the record's item is usable from the inventory — `UsableFromInventory` on its
	/// InventoryItemComponent, the guest's own test (`sub_827649C8`, checked at 0x826D19B8 before it will
	/// dispatch a use). True when there is no database to ask.
	/// </summary>
	public static bool UsableFromInventory(uint record) =>
		Globals is null ||
		(TryItemField(record, UsableField, (int)GdbFieldType.Bool, out uint usable) && usable != 0);

	/// <summary>
	/// A COMPONENT of an item, by the field the entity names it with.
	///
	/// `GetHeroInventory` returns `item + 0x04`, and that is the ENTITY record — it carries
	/// `InventoryItemComponent`, `AppearanceModifierComponent` and so on as reference fields, and
	/// carries none of the item's own properties. Every property a list needs lives on one of
	/// those components: `NameTag`, `IconGraphic`, `Rating` and `UsableFromInventory` on the
	/// InventoryItemComponent, `CanBeGift` on the GiftComponent.
	///
	/// Reading them off the entity returns nothing, which is exactly what a screen full of
	/// `0x959C9372 x2` looks like. Verified against the shipped file: entity `0x959C9372` names
	/// InventoryItemComponent `33882401`, and THAT record holds
	/// `NameTag = INV_ITEM_CLOTHES_BANDIT_SUIT_NAME`.
	///
	/// The hop is one field lookup; the INHERITANCE walk happens on the far side, because a
	/// clothing item states only its two tags locally and takes icon and rating from its parents
	/// up to `InventoryItemComponent`.
	/// </summary>
	private static bool TryComponent(uint entity, uint field, out GdbCursor cursor)
	{
		cursor = default;
		return Globals is { } globals &&
			globals.TryLookup(entity, out GdbCursor owner) &&
			globals.TryFindInherited(owner, field, (int)GdbFieldType.Reference,
				out _, out uint component, out _) &&
			component != 0u &&
			globals.TryLookup(component, out cursor);
	}

	/// <summary>One inherited field off an item's InventoryItemComponent.</summary>
	/// <summary>
	/// A whole number off an item, WHICHEVER of the two numeric types it was authored as.
	///
	/// The shipped database mixes them and the two reads here had it backwards BOTH ways:
	/// `Rating` is `enum` on all 513 records that state it and was read as `int`, while
	/// `WeaponType` is `int` on all 128 and was read as `enum`. A wrong type code returns
	/// nothing and says nothing, so both simply behaved as though the field were absent.
	/// </summary>
	private static bool TryItemNumber(uint entity, uint field, out uint value) =>
		TryItemField(entity, field, (int)GdbFieldType.Int, out value) ||
		TryItemField(entity, field, (int)GdbFieldType.Enum, out value);

	private static bool TryItemField(uint entity, uint field, int type, out uint value)
	{
		value = 0u;
		return TryComponent(entity, InventoryComponentField, out GdbCursor item) &&
			Globals!.TryFindInherited(item, field, type, out _, out value, out _);
	}

	/// <summary>A string field's text, through the dictionaries of every GDB: the field may be inherited from another's record.</summary>
	private static string ItemString(uint entity, uint field) =>
		TryItemField(entity, field, (int)GdbFieldType.String, out uint hash) ? Globals!.NameOf(hash) : string.Empty;

	private static readonly uint WeaponTypeField = Engine_GameHash.Fnv1("WeaponType");

	/// <summary>
	/// `WeaponType` hangs off the item's WEAPON component, not its inventory component — checked
	/// against the shipped database: no InventoryItemComponent chain carries it, every
	/// WeaponComponent chain does (Enum, e.g. entry 21200 states 7). Reading it through
	/// `InventoryItemComponent` returned nothing for every weapon, and both halves listed empty.
	/// </summary>
	private static readonly uint WeaponComponentField = Engine_GameHash.Fnv1("WeaponComponent");

	private static bool TryWeaponType(uint entity, out uint weaponType)
	{
		weaponType = 0u;
		return TryComponent(entity, WeaponComponentField, out GdbCursor weapon) &&
			(Globals!.TryFindInherited(weapon, WeaponTypeField, (int)GdbFieldType.Enum,
				out _, out weaponType, out _) ||
			Globals!.TryFindInherited(weapon, WeaponTypeField, (int)GdbFieldType.Int,
				out _, out weaponType, out _));
	}

	/// <summary>The record's weapon class by its own WeaponType: 1 melee, 2 ranged, 0 neither or no database.</summary>
	public static int WeaponClassOf(uint record) =>
		Globals is not null && TryWeaponType(record, out uint weaponType) ? WeaponClass((int)weaponType) : 0;

	/// <summary>`sub_82561420` — 1 melee, 2 ranged, 0 neither.</summary>
	private static int WeaponClass(int weaponType) => weaponType switch
	{
		2 or 3 or 4 => 1,
		5 or 6 or 7 or 8 => 2,
		_ => 0,
	};

	/// <summary>The action prefix the guest-tick pump answers — see `DispatchInventoryAction`.</summary>
	public const string InventoryAction = "Inventory:";

	private static HashSet<int>? ParseCategories(string argument)
	{
		if (argument.Length == 0)
		{
			return null;
		}

		var wanted = new HashSet<int>();
		foreach (string part in argument.Split(','))
		{
			if (int.TryParse(part.Trim(), out int category))
			{
				wanted.Add(category);
			}
		}

		return wanted.Count == 0 ? null : wanted;
	}


	/// <summary>
	/// Whether the guest would list this record at all — `sub_827642E0`, which every populator
	/// applies before anything else.
	///
	/// `VisibleInGUI` is the item's own veto, and it is READ ONLY WHEN THE PROPERTY EXISTS: the
	/// guest tests for presence (type 8) and only then reads the bool, so an item that states
	/// nothing is visible rather than hidden. Getting that backwards empties the list.
	///
	/// THE SUITS AND EQUIPPED EXCLUSIONS ARE NOT HERE, and that is a correction.
	///
	/// They were, and they belong to the GIFT screen alone. `sub_8242FB28` switches on the item's
	/// category at `item+16`: case 0x19 (Suits) falls straight to the reject, and cases 0x11..0x16
	/// (clothing) reject only when `sub_824C4258(component15, item)` says the item is equipped AND
	/// `*(item+12) <= 1` leaves no spare. That whole function is reached only on `*a1 == 1 || 5`,
	/// the gift modes, and its default path is the `CanBeGift` test.
	///
	/// The clothing screen's own builder applies NEITHER. `sub_829B3D28` walks the inventory and
	/// spawns every record that passes this and states a category in 17..24 — no equipped test at
	/// all — which is why the worn hat is a row on the real screen and why the guest's header read
	/// `Headgear (6)` over what we were listing as five.
	/// </summary>
	private static bool Listable(Fable2Bridge.InventoryRecord record, bool asked)
	{
		if (Globals is null)
		{
			return true;
		}

		return !TryItemField(record.RecordId, VisibleField, (int)GdbFieldType.Bool,
			out uint visible) || visible != 0;
	}

	/// <summary>
	/// The item's display text: its `NameTag` through the string book, with the stack count
	/// appended the way the shipped screens do it ("Silver Key x5"). Falls back to the GDB key
	/// so an item with no tag is still identifiable rather than blank.
	/// </summary>
	/// <summary>The item's `NameTag`, off its InventoryItemComponent — see TryComponent.</summary>
	private static string NameTagOf(Fable2Bridge.InventoryRecord record) =>
		ItemString(record.RecordId, NameTagField);

	/// <summary>
	/// The row's display text: resolves via Engine_ItemNames (or Babel tag fallback), with the stack
	/// count appended the way the guest does it — `L" x"` then the number (`word_820CE5E0`, 0x826CE31C),
	/// so "Silver Key x5". An unresolved tag shows verbatim, which is what the guest does too
	/// (`sub_8230E0E8`'s third fallback copies the raw tag).
	///
	/// An OUTFIT reads its name alone: it is worn as its pieces, so how many copies of the suit
	/// item are carried says nothing the player can use — a remaster choice over the guest's
	/// suffix, which it appends to every row.
	/// </summary>
	private static string LabelOf(Fable2Bridge.InventoryRecord record, string tag)
	{
		// Try resolving via Engine_ItemNames first (unified DRY path matching editor & item pages):
		if (Globals is { } gdb)
		{
			string displayName = DisplayNameOf(gdb, record.RecordId);
			if (displayName.Length != 0)
			{
				return record.Count > 1 ? $"{displayName} x{record.Count}" : displayName;
			}
		}

		string text = tag.Length == 0
			? $"0x{record.RecordId:X8}"
			: Strings is { } book && book.TryGetString(tag, out string resolved) &&
				resolved.Length != 0
					? resolved
					: tag;

		return record.Count > 1 && record.Category != Category.Suits ? $"{text} x{record.Count}" : text;
	}

	/// <summary>
	/// The item's resolved display name, or empty when the item page's resolver yields nothing
	/// usable (the caller then falls back to the Babel tag). Cached per record: `BuildOne` is a
	/// full item read — definition, appearance, every stat component, augments — and the Items
	/// screen asked it for every carried item in every one of its nine categories on every
	/// open, and again on every refresh sample. The name depends only on the GDB and the text
	/// banks it resolves through, so an entry is good while those are the same instances.
	/// </summary>
	private static string DisplayNameOf(GdbMulti gdb, uint recordId)
	{
		Refii.Text.TextBank? bank = Engine_BnkTextBankLoader.Shared;
		if (DisplayNames.TryGet(recordId, out string? name, gdb, bank))
		{
			return name;
		}

		name = string.Empty;
		if (Engine_ItemDetails.BuildOne(gdb, recordId) is { } detail)
		{
			string resolved = Engine_ItemNames.DisplayNameOf(detail);
			if (!string.IsNullOrEmpty(resolved) && resolved != detail.Label)
			{
				name = resolved;
			}
		}

		DisplayNames.Put(recordId, name, gdb, bank);
		return name;
	}

	private static readonly Engine_UiIdentityCache<uint, string> DisplayNames = new();

	private static string IconOf(Fable2Bridge.InventoryRecord record) =>
		ItemString(record.RecordId, IconField);

	/// <summary>
	/// The star strip a mod's usable row draws: the constant 11 the augment, dye and book categories carry
	/// (`li r29, 0xB` 0x826CE50C) and the GDB `Rating` doubled otherwise (`slwi r29, r3, 1` 0x826CE524), the row
	/// builder's own arms; a usable list holds no weapon, so the augment-slot arm is not asked.
	/// </summary>
	private static int RatingOf(Fable2Bridge.InventoryRecord record) =>
		record.Category is Category.Augmentations or Category.Dyes or Category.Books
			? 11
			: TryItemNumber(record.RecordId, RatingField, out uint rating)
				? (int)rating * 2
				: Engine_UiMenuConstants.EmptyRatingSlide;
}
