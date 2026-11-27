using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Core;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Host;

namespace Refii.Titles.Fable2;

/// <summary>
/// THE INVENTORY AS A MOD'S SCREEN READS IT (<c>RefiiHost.inventory_items</c>): everything the game's own
/// inventory pages show about what player 1's or 2's hero carries, one Dictionary per stack, and the same
/// actions their rows run (<c>RefiiHost.inventory_action</c>).
/// </summary>
public static partial class Engine_UiInventorySource
{
	private static readonly uint DescriptionField = Engine_GameHash.Fnv1("DescriptionTag");
	private static readonly uint ShopField = Engine_GameHash.Fnv1("ShopItemComponent");
	private static readonly uint PriceField = Engine_GameHash.Fnv1("BasePrice");
	private static readonly uint CategoryField = Engine_GameHash.Fnv1("Category");

	private readonly record struct ModItemFacts(string Name, string Description, string Icon, string Model, string[] Pieces, int Category,
		int Value, int WeaponClass, Engine_UiItemStats.Stats Stats, bool Usable, Vector3 Rotation, int RotationAxis);

	/// <summary>
	/// What a record's text and records give, read once per culture, mod set and book: a name and a stat block
	/// are several lookups each. Dropped when the culture, the mounted mod tables (a mod set's GDB and text
	/// overlays), the string book or the database changes.
	/// </summary>
	private static readonly Dictionary<uint, ModItemFacts> ModFacts = new();

	private static (string Culture, object? Tables, object? Book, object? Database) _factsFor;

	/// <summary>What <paramref name="player"/>'s hero (1 or 2) carries, from the pump's per-player snapshot (<see cref="Fable2Bridge.GetPlayerInventory"/>).</summary>
	public static Godot.Collections.Array ModItems(int player)
	{
		var items = new Godot.Collections.Array();
		if (Globals is not { } database)
		{
			return items;
		}

		(string, object?, object?, object?) standing =
			(Refii.Text.Engine_GameCulture.Current, Refii.Mods.ModText.Mounted, Strings, database);
		if (!standing.Equals(_factsFor))
		{
			ModFacts.Clear();
			_factsFor = standing;
		}

		var worn = new HashSet<uint>();
		var carrying = new List<Fable2Bridge.InventoryRecord>();
		var carried = new HashSet<uint>();
		foreach ((Fable2Bridge.InventoryRecord record, bool on) in Fable2Bridge.GetPlayerInventory(player))
		{
			carrying.Add(record);
			carried.Add(record.RecordId);
			if (on)
			{
				worn.Add(record.RecordId);
			}
		}

		foreach (Fable2Bridge.InventoryRecord record in carrying)
		{
			if (!Listable(record, false))
			{
				continue;
			}

			ModItemFacts facts = FactsOf(record);
			int category = record.Category >= 0 ? record.Category : facts.Category;
			bool on = worn.Contains(record.RecordId);

			// An outfit goes on as the pieces of it the hero carries, and with none has nothing to put on,
			// so the game's own list leaves it out. It is itself never worn.
			if (category == Category.Suits)
			{
				int owned = 0;
				int wearing = 0;
				foreach (uint piece in Engine_UiItemStats.SuitPieces(record.RecordId))
				{
					if (carried.Contains(piece))
					{
						++owned;
						wearing += worn.Contains(piece) ? 1 : 0;
					}
				}

				if (owned == 0)
				{
					continue;
				}

				on = wearing == owned;
			}

			var lines = new Godot.Collections.Array();
			foreach (Engine_UiItemStats.Line line in facts.Stats.Lines)
			{
				lines.Add(new Godot.Collections.Dictionary { ["label"] = line.Label, ["value"] = line.Value });
			}

			items.Add(new Godot.Collections.Dictionary
			{
				["definition"] = "0x" + record.RecordId.ToString("X8", CultureInfo.InvariantCulture),
				["count"] = record.Count,
				["category"] = category,
				["name"] = facts.Name,
				["description"] = facts.Description,
				["icon"] = facts.Icon,
				["model"] = facts.Model,
				["pieces"] = facts.Pieces,
				["rotation"] = facts.Rotation,
				["rotation_axis"] = facts.RotationAxis,
				["value"] = facts.Value,
				["rating"] = facts.Stats.Rating,
				["usable"] = facts.Usable,
				["worn"] = on,
				["weapon_class"] = facts.WeaponClass,
				["new"] = !record.Viewed,
				["lines"] = lines,
			});
		}

		return items;
	}

	private static ModItemFacts FactsOf(Fable2Bridge.InventoryRecord record)
	{
		if (ModFacts.TryGetValue(record.RecordId, out ModItemFacts known))
		{
			return known;
		}

		string name = DisplayNameOf(Globals!, record.RecordId);
		if (name.Length == 0)
		{
			name = LabelOf(record with { Count = 1 }, NameTagOf(record));
		}

		// A tag no record names but the text bank holds, as the item page finds it.
		if (name == NameTagOf(record) &&
			Engine_BnkTextBankLoader.TryLookup(Engine_GameHash.Fnv1("TEXT_" + name), out string banked) && banked.Length != 0)
		{
			name = banked;
		}

		// A tag nothing holds stays the tag, as the item page leaves it.

		string descriptionTag = ItemString(record.RecordId, DescriptionField);
		string description = Strings is { } book && descriptionTag.Length != 0 && book.TryGetString(descriptionTag, out string text)
			? text
			: string.Empty;
		int value = TryComponent(record.RecordId, ShopField, out GdbCursor shop) &&
			Globals!.TryFindInherited(shop, PriceField, (int)GdbFieldType.Int, out _, out uint price, out _)
				? (int)price
				: 0;
		int category = TryItemNumber(record.RecordId, CategoryField, out uint stated) ? (int)stated : -1;
		// An outfit states no model of its own: it is the pieces it names.
		var pieces = new List<string>();
		foreach (uint piece in Engine_UiItemStats.SuitPieces(record.RecordId))
		{
			if (ModelOf(piece) is { Length: > 0 } worn)
			{
				pieces.Add(worn);
			}
		}

		// The pose the item page turns it in. An outfit states none, the mannequin showing it there, and
		// a worn piece's front is away from the camera until it is turned half about.
		ItemDetail? detail = Engine_ItemDetails.BuildOne(Globals!, record.RecordId);
		Vector3 rotation = pieces.Count != 0 ? new Vector3(Mathf.Pi, 0f, 0f) : detail?.PreviewRotation ?? Vector3.Zero;
		var facts = new ModItemFacts(name, description, IconOf(record), ModelOf(record.RecordId), pieces.ToArray(), category, value, WeaponClassOf(record.RecordId),
			Engine_UiItemStats.For(record.RecordId, Strings), UsableFromInventory(record.RecordId), rotation, detail?.PreviewRotationAxis ?? 0);
		// Without the string book an item has no description and its stats no labels: kept only once it has.
		if (Strings is not null)
		{
			ModFacts[record.RecordId] = facts;
		}

		return facts;
	}

	/// <summary>The path of the mesh the item page's own preview spins (Engine_UiItemPage.Preview), empty for none.</summary>
	private static string ModelOf(uint record)
	{
		ItemDetail? detail = Engine_ItemDetails.BuildOne(Globals!, record);
		uint mesh = detail is null ? 0u : detail.PreviewMesh != 0 ? detail.PreviewMesh
			: detail.WornModelPathHash != 0 ? detail.WornModelPathHash : detail.ModelPathHash;
		return mesh != 0 ? Refii.Bnk.AssetHashIndex.PathForHash(mesh) ?? string.Empty : string.Empty;
	}

	/// <summary>
	/// The pump's action for what a screen asks of an item: <c>use</c>, <c>equip</c>, <c>wear</c> and
	/// <c>outfit</c> name the record, as the game's rows do; <c>remove</c> empties the slot a clothing
	/// category is (the pages' "none" rows), which an outfit is not: its "none" row's event undresses
	/// nothing here, so its pieces come off one by one. Null for anything else.
	/// <paramref name="player"/> 0 is the hero whose menus are open (the pages' own action); 1 or 2 names a
	/// hero whose menus are not (<see cref="PlayerInventoryAction"/>), acted on directly.
	/// </summary>
	public static string? ModAction(string operation, uint record, int category, int player = 0)
	{
		string prefix = player is 1 or 2
			? string.Create(CultureInfo.InvariantCulture, $"{PlayerInventoryAction}{player}:")
			: InventoryAction;
		switch (operation)
		{
			case "use":
			case "equip":
			case "wear":
			case "outfit":
				return string.Create(CultureInfo.InvariantCulture, $"{prefix}{operation}:{record}");
			case "remove":
				// The slot's None row's event, 91 (Hats, 17) .. 97 (Masks, 23): `sub_826CC340(type − 91)` maps it
				// back to the category (0x826D3F6C); the Outfits' 90 strips every piece and is not a slot.
				return category is >= Category.Hats and <= Category.Accessories
					? string.Create(CultureInfo.InvariantCulture, $"{prefix}unequip:{category + 74}")
					: null;
			default:
				return null;
		}
	}

	/// <summary>
	/// <c>InventoryFor:&lt;player&gt;:&lt;op&gt;:&lt;record&gt;</c> — an inventory action on player 1's or 2's own
	/// hero when their menus are not the open ones (the pump's <c>kActionInventoryForPrefix</c>): local co-op only.
	/// </summary>
	public const string PlayerInventoryAction = "InventoryFor:";
}
