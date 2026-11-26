using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE UPGRADE-FURNITURE PAGES (screen 26), stated from the targeted piece
/// (<see cref="Fable2Bridge.GetFurniture"/>) by the page fillers' own rules —
/// `sub_8299B6D8` (Page1) and `sub_8299C710` (Page2). A row is a LEVEL: `Float1 + (Float2 &lt;&lt; 16)`.
///
/// <list type="bullet">
/// <item>The value delta `sub_825CBFC8(piece, level)`: the first value when the piece's gate is
/// clear; otherwise `values[level] − values[installed]` when the list reaches the level, else 1.
/// The page caller remaps a kind-3 piece's level 1→2, 2→4 (0x8299bb74) and the callee maps any
/// non-zero n to `(n == 4) + 1`. The house value is `(int)(multiplier × delta)` (`fctiwz`).</item>
/// <item>The unlock item `sub_825CC2F0`: the hero's category-15 item whose
/// `FurnitureUnlockComponent` has `Type` = the piece's type and `Level − 1` = the level.</item>
/// <item>The card `sub_825CC7A8`: GDB record `&lt;Type&gt;Level&lt;installed + 1&gt;`
/// (`ObjectInventoryCarbonatedBed` at installed level 5), its `InventoryItemComponent` naming.</item>
/// <item>Page1: name = the unlock item's, else the card's; value `&lt;gold_img&gt;±n` (n forced 0
/// without an item), green (0,160,0) / red (192,0,0) / black. Page2: name and description the
/// card's on the installed level, else the item's; value `+ $n` / `- $n` / `$0` (0 on the installed
/// level), green / red / white. Both pages' A and Replace at 50 on the installed level.</item>
/// </list>
/// </summary>
public static class Engine_UiFurniturePage
{
	public const uint ScreenId = 26;

	/// <summary>The verb prefix; posted with the acting player as the pump's `Furniture:&lt;p&gt;:…`
	/// (<see cref="Fable2PortedScreens.RowLever"/>, `DispatchFurnitureAction`).</summary>
	public const string FurnitureAction = "Furniture:";

	/// <summary>
	/// The screen's row action (<see cref="Engine_UiScreenTemplate.RowAction"/>): the row's level,
	/// upgraded through `sub_826B6A18`'s own transaction (`RefiiFurnitureUpgrade`), never its
	/// highlight read.
	/// </summary>
	public const string UpgradeAction = FurnitureAction + "upgrade:" + Engine_UiScreenTemplate.RowIdToken;

	private const string Page1 = "Foreground.Book.Pages.Page1.Contents.";

	private const string Page2 = "Foreground.Book.Pages.Page2.Contents.";

	private const string Gold = "<gold_img>";

	/// <summary>`off_8331910C`, indexed by the piece's type (+44).</summary>
	private static readonly string[] TypeNames =
	{
		"BookcaseLevel", "ChildBedLevel", "DoubleBedLevel", "ClosetLevel", "ComfyChairLevel",
		"CupboardLevel", "DrawersLevel", "DresserLevel", "SinkLevel", "StoveLevel", "TableLevel",
		"BedsideTableLevel", "DeskTableLevel",
	};

	private const int FurnitureCategory = 15;

	/// <summary>The level a row names, or null for a row that is not one.</summary>
	public static int? LevelOf(Engine_UiExpandableMenu.Item? row) =>
		row is null || row.IdLow < 0 ? null : row.IdLow + (row.IdHigh << 16);

	/// <summary>`sub_825CBFC8` with the page caller's kind-3 remap.</summary>
	public static int Delta(in Refii.Host.HostBridge.Furniture piece, int level)
	{
		if (!piece.Gate)
		{
			return piece.Values.Length > 0 ? piece.Values[0] : 0;
		}

		if (piece.Kind == 3 && level != 0)
		{
			int remapped = level == 1 ? 2 : level == 2 ? 4 : level;
			level = (remapped == 4 ? 1 : 0) + 1;
		}

		int count = piece.Values.Length;
		return count != 0 && count >= level + 1 && piece.Installed >= 0 && piece.Installed < count
			? piece.Values[level] - piece.Values[piece.Installed]
			: 1;
	}

	public static int HouseValue(in Refii.Host.HostBridge.Furniture piece, int level) =>
		(int)(piece.Multiplier * Delta(piece, level));

	/// <summary>The hero's unlock item for a level, or null.</summary>
	private static GdbCursor? UnlockItem(GdbMulti multi, in Refii.Host.HostBridge.Furniture piece, int level) =>
		UnlockRecord(multi, piece, level) is { } id && multi.TryLookup(id, out GdbCursor at) ? at : null;

	/// <summary>The unlock item's record id for a level, or null.</summary>
	private static uint? UnlockRecord(GdbMulti multi, in Refii.Host.HostBridge.Furniture piece, int level)
	{
		foreach (Fable2Bridge.InventoryRecord item in Fable2Bridge.GetHeroInventory())
		{
			if (item.Category != FurnitureCategory || item.Count <= 0 ||
				!multi.TryLookup(item.RecordId, out GdbCursor at) ||
				!Engine_AbilityRecords.TryRef(multi, at, "FurnitureUnlockComponent", out GdbCursor unlock))
			{
				continue;
			}

			if (Engine_AbilityRecords.IntOrEnumOn(unlock, "Type") == piece.Type &&
				Engine_AbilityRecords.IntOrEnumOn(unlock, "Level") - 1 == level)
			{
				return item.RecordId;
			}
		}

		return null;
	}

	/// <summary>
	/// The record the page's 3D preview shows for a level: the unlock item (`sub_828C3B30` on the
	/// item), else the installed card (`sub_828C39F0`). Null when neither resolves.
	/// </summary>
	public static uint? PreviewRecord(Engine_UiExpandableMenu.Item? row)
	{
		if (LevelOf(row) is not { } level || Engine_GameGdb.Database is not { } multi)
		{
			return null;
		}

		Refii.Host.HostBridge.Furniture piece = Fable2Bridge.GetFurniture();
		if (!piece.Known)
		{
			return null;
		}

		return UnlockRecord(multi, piece, level) ??
			(CardName(piece) is { Length: > 0 } name ? Refii.Core.Engine_GameHash.Fnv1(name) : null);
	}

	private static string CardName(in Refii.Host.HostBridge.Furniture piece) =>
		piece.Installed == 5
			? "ObjectInventoryCarbonatedBed"
			: piece.Type >= 0 && piece.Type < TypeNames.Length
				? TypeNames[piece.Type] + (piece.Installed + 1).ToString(CultureInfo.InvariantCulture)
				: string.Empty;

	/// <summary>The card record for the installed level, or null.</summary>
	private static GdbCursor? Card(GdbMulti multi, in Refii.Host.HostBridge.Furniture piece)
	{
		string name = piece.Installed == 5
			? "ObjectInventoryCarbonatedBed"
			: piece.Type >= 0 && piece.Type < TypeNames.Length
				? TypeNames[piece.Type] + (piece.Installed + 1).ToString(CultureInfo.InvariantCulture)
				: string.Empty;
		return name.Length != 0 && multi.TryLookup(Refii.Core.Engine_GameHash.Fnv1(name), out GdbCursor at)
			? at
			: null;
	}

	/// <summary>An `InventoryItemComponent` text tag, localised.</summary>
	private static string Text(GdbMulti multi, GdbCursor? record, string field, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (record is not { } at ||
			!Engine_AbilityRecords.TryRef(multi, at, "InventoryItemComponent", out GdbCursor inv) ||
			!multi.TryFindInherited(inv, Refii.Core.Engine_GameHash.Fnv1(field), (int)GdbFieldType.String,
				out _, out uint tagHash, out _) ||
			!inv.View.Names.TryGetValue(tagHash, out string? tag) || tag.Length == 0)
		{
			return string.Empty;
		}

		return strings is not null && strings.TryGetString(tag, out string text) && text.Length != 0 ? text : tag;
	}

	/// <summary>Everything the two page fillers state for one highlighted level.</summary>
	public sealed class Statements
	{
		public readonly Dictionary<string, string> Values = new();
		public readonly Dictionary<string, Color> Tints = new();
		public readonly Dictionary<string, float> Opacities = new();
	}

	private static ulong _stateFrame = ulong.MaxValue;

	private static Engine_UiExpandableMenu.Item? _stateRow;

	private static Statements? _state;

	/// <summary>The page's statements for the highlighted level — computed once a frame: the
	/// provider asks for values, tints and opacities separately.</summary>
	public static Statements? State(Engine_UiExpandableMenu.Item? row, Refii.Text.Engine_BabelDatabase? strings)
	{
		ulong frame = Engine.GetProcessFrames();
		if (frame == _stateFrame && ReferenceEquals(row, _stateRow))
		{
			return _state;
		}

		_stateFrame = frame;
		_stateRow = row;
		_state = Compute(row, strings);
		return _state;
	}

	private static Statements? Compute(Engine_UiExpandableMenu.Item? row, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (LevelOf(row) is not { } level || Engine_GameGdb.Database is not { } multi)
		{
			return null;
		}

		Refii.Host.HostBridge.Furniture piece = Fable2Bridge.GetFurniture();
		if (!piece.Known)
		{
			return null;
		}

		var said = new Statements();
		GdbCursor? item = UnlockItem(multi, piece, level);
		GdbCursor? card = Card(multi, piece);
		bool installed = level == piece.Installed;
		int value = HouseValue(piece, level);

		// Page1 (`sub_8299B6D8` / `sub_8299BDA0`).
		said.Values[Page1 + "ItemName"] = item is not null
			? Text(multi, item, "NameTag", strings)
			: Text(multi, card, "NameTag", strings);
		int first = item is not null ? value : 0;
		said.Values[Page1 + "HouseValueAmount"] = Gold + (first > 0 ? "+" + Number(first) : first < 0 ? "-" + Number(-first) : "0");
		said.Tints[Page1 + "HouseValueAmount"] = first > 0
			? Bytes(0, 160, 0)
			: first < 0 ? Bytes(192, 0, 0) : Bytes(0, 0, 0);

		// Page2 (`sub_8299C710` / `sub_8299CD30`).
		said.Values[Page2 + "ItemName"] = installed
			? Text(multi, card, "NameTag", strings)
			: Text(multi, item, "NameTag", strings);
		said.Values[Page2 + "ItemDescription"] = installed
			? Text(multi, card, "DescriptionTag", strings)
			: Text(multi, item, "DescriptionTag", strings);
		int second = installed ? 0 : value;
		said.Values[Page2 + "HouseValueAmount"] = second > 0
			? "+ $" + Number(second)
			: second < 0 ? "- $" + Number(-second) : "$0";
		said.Tints[Page2 + "HouseValueAmount"] = second > 0
			? Bytes(0, 255, 0)
			: second < 0 ? Bytes(255, 0, 0) : Bytes(255, 255, 255);

		float dim = installed ? 0.5f : 1f;
		foreach (string page in new[] { Page1, Page2 })
		{
			said.Opacities[page + "Select.icon_button_a"] = dim;
			said.Opacities[page + "Select.Replace"] = dim;
		}

		return said;
	}

	private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

	private static Color Bytes(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
