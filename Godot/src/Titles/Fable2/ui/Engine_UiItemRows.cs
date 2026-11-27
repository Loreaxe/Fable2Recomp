using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;
using Row = Refii.Ui.Engine_UiMenuSources.Row;

/// <summary>
/// THE ITEM BOOKS' ROWS, AS THE GUEST STATES THEM — items and weapons (15), clothing and the dye targets
/// (16), the gift (25). Every row is the guest's own spawn (the capture), filed under the parent its
/// populate named; an item row carries the item its row builder `sub_826CE230` (TU1; disc
/// `sub_82407D20`) was handed — record, instance, count, category, viewed (item_rows_stated.cpp) — its
/// icon (`String3`), its decorated label (`String2`: the ` xN` count, " (BEST)", `&lt;exclamation.img&gt;`)
/// and the mark the guest stated for it (`SetItemEmphasisByIndex` from `sub_826D0890`: worn, held, the
/// weapon just chosen; `SetItemEmphasis` by name for the None rows). Its id is the population ticket.
///
/// What is stamped on: the row's own A as the public command mods read — `Inventory:&lt;op&gt;:&lt;record&gt;`
/// for a row of type 3 (the books' `use` / `equip` / `wear` / `gift`), `Inventory:outfit:&lt;record&gt;` for
/// an outfit (89), `Inventory:unequip:&lt;type&gt;` for a None row (91..97); the book's lever carries the
/// row's ticket (<see cref="Fable2PortedScreens.ItemLever"/>). Every other row — a dye, an augment, a
/// remover (80 / 83 / 310), their targets (84, 86..88, 85, 311), the Outfits' None row (90), the gift's
/// Gold (7) — commits as itself, the guest's own event on its own (type, ticket).
/// </summary>
public static class Engine_UiItemRows
{
	/// <summary>`items:&lt;op&gt;:&lt;parent&gt;` — the rows the guest spawned under the parent; `items:gift:` the
	/// gift list, grouped under the guest's own category rows, then its root rows (the Gold).</summary>
	public const string SourceName = "items";

	/// <summary>The category header's event type (`sub_826CF098`, 0x826CF180).</summary>
	private const int Header = (int)Engine_UiMenuConstants.EventType.Submenu;

	/// <summary>A row's own A — what `sub_826CEEF0` passes the books' item rows (`li r7,3` 0x829B1E44).</summary>
	private const int RowAnswer = 3;

	/// <summary>The clothing book's outfit rows (`li r9,0x59` 0x829B326C).</summary>
	private const int Outfit = 89;

	/// <summary>The items the last reads stated, by the row's ticket: what the item page reads for a row whose id
	/// is a ticket. Dropped when the rows are another screen's (a ticket is a population's).</summary>
	private static readonly Dictionary<uint, Engine_UiModel.MenuRowItem> Items = new();

	private static uint _itemsScreen = Engine_UiMenuSources.AnyScreen;

	public static IReadOnlyList<Row> Rows(string argument)
	{
		int colon = argument.IndexOf(':');
		string operation = colon < 0 ? string.Empty : argument[..colon];
		string parent = colon < 0 ? argument : argument[(colon + 1)..];
		var rows = new List<Row>();
		if (parent.Length != 0)
		{
			foreach (Engine_UiModel.MenuRow captured in Engine_UiMenuSources.CapturedUnder(parent))
			{
				rows.Add(RowOf(captured, operation, string.Empty, string.Empty));
			}

			return rows;
		}

		// The gift's shape: `lhGuiGiftGoldScreen_PopulateMenu` spawns a header only for a group it lists
		// (0x82430778), the group's rows under it, then the Gold row on the root (0x82430AD0).
		var headers = new List<Engine_UiModel.MenuRow>();
		var root = new List<Engine_UiModel.MenuRow>();
		foreach (Engine_UiModel.MenuRow captured in Engine_UiMenuSources.CapturedUnder(Engine_UiExpandableMenu.RootName))
		{
			(captured.EventType == Header ? headers : root).Add(captured);
		}

		foreach (Engine_UiModel.MenuRow header in headers)
		{
			foreach (Engine_UiModel.MenuRow captured in Engine_UiMenuSources.CapturedUnder(header.Name))
			{
				rows.Add(RowOf(captured, operation, header.Name, header.Label));
			}
		}

		foreach (Engine_UiModel.MenuRow captured in root)
		{
			rows.Add(RowOf(captured, operation, string.Empty, string.Empty));
		}

		return rows;
	}

	/// <summary>The item the stated row with this ticket stands for; null for a row with none.</summary>
	public static Engine_UiModel.MenuRowItem? ItemOf(uint ticket) =>
		Items.TryGetValue(ticket, out Engine_UiModel.MenuRowItem item) ? item : null;

	/// <summary>The guest is gone: so are its rows.</summary>
	public static void Reset() => Items.Clear();

	private static Row RowOf(Engine_UiModel.MenuRow captured, string operation, string group, string groupLabel)
	{
		Engine_UiModel.MenuRowItem? item = captured.Item;
		if (Engine_UiMenuSources.BoundScreen != _itemsScreen)
		{
			Items.Clear();
			_itemsScreen = Engine_UiMenuSources.BoundScreen;
		}

		if (item is { } stated)
		{
			Items[captured.Id] = stated;
		}

		return new Row(
			captured.Name,
			captured.Label.Length != 0 ? captured.Label : captured.Name,
			captured.Icon,
			captured.EventType,
			captured.Id,
			captured.Rating,
			Committable: true,
			Command: CommandOf(captured, operation, item),
			Group: group,

			// The guest's header caption (`sub_826CF098`: the caption, " (N)", `<exclamation.img>`), drawn as
			// stated: it resolves as no tag.
			GroupTag: groupLabel,
			Emphasis: (Engine_UiMenuConstants.Emphasis)System.Math.Clamp(captured.Emphasis, 0, 3),

			// An inventory record is what the guest's header counts (`sub_826CF6A8`); the None and hair rows
			// stand for none.
			Counted: item is { Counted: true },

			// "New" is the guest's own mark in the label (`<exclamation.img>`, 0x826CE390), which the page
			// fill's `RefreshButtonText` takes off when the row is seen (0x829B55AC).
			Unviewed: captured.Label.Contains(Engine_UiTextTruncate.Exclamation, System.StringComparison.Ordinal),
			Instance: item?.Instance ?? 0u);
	}

	/// <summary>The public command of a row whose A is a book lever; null for a row that commits as itself.</summary>
	private static string? CommandOf(Engine_UiModel.MenuRow captured, string operation, Engine_UiModel.MenuRowItem? item)
	{
		const string prefix = Engine_UiInventorySource.InventoryAction;
		if (captured.EventType is >= 91 and <= 97)
		{
			return $"{prefix}unequip:{captured.EventType}";
		}

		if (item is not { Record: not 0u } stated)
		{
			return null;
		}

		if (captured.EventType == Outfit)
		{
			return $"{prefix}outfit:{stated.Record}";
		}

		if (captured.EventType != RowAnswer || operation.Length == 0)
		{
			return null;
		}

		// A dog item on the clothing book is activated, not worn (`sub_826D4D58` case 3 on Category 24,
		// 0x826D5050..0x826D506C); the clothing lever runs that answer either way.
		string op = operation == "wear" && stated.Category == Engine_UiInventorySource.Category.Dog ? "equip" : operation;

		// The use is refused by the guest for an item that is not `UsableFromInventory` (`sub_827649C8`,
		// 0x826D19B8); its row is listed with no action, as the guest's A does nothing there.
		return op == "use" && !Engine_UiInventorySource.UsableFromInventory(stated.Record)
			? null
			: $"{prefix}{op}:{stated.Record}";
	}
}
