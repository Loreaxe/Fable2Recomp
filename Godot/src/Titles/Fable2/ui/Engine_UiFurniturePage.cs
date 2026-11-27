using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE UPGRADE-FURNITURE PAGES (screen 26), as the screen's highlight edge `sub_826B64D8` had its two
/// page fills write them for a level (<see cref="Engine_UiPageFills"/>, scope "row", keyed by the
/// level): Page1 `sub_8299B6D8` and its value line `sub_8299BDA0`, Page2 `sub_8299C710` and its
/// value line `sub_8299CD30`. A row is a LEVEL: `Float1 + (Float2 &lt;&lt; 16)`.
///
/// <list type="bullet">
/// <item>`ItemName` (both pages) and `ItemDescription` (Page2): the guest's texts.</item>
/// <item>`HouseValueAmount` (both pages): the value text and its colour (`sub_82425E58`).</item>
/// <item>`Select.icon_button_a`, `Select.Replace` (both pages): their opacity, 50 on the installed
/// level and 100 otherwise (`sub_82426140`, 0x8299bc5c / 0x8299bc64), read as 0..1.</item>
/// <item>The preview: the record the page's preview was set to (the level's unlock item,
/// `sub_828C3B30` 0x8299bc00), else the card the fills took (`sub_825CC7A8`), whose entry
/// `sub_828C39F0` previews (0x8299bb3c).</item>
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

	/// <summary>The level a row names, or null for a row that is not one.</summary>
	public static int? LevelOf(Engine_UiExpandableMenu.Item? row) =>
		row is null || row.IdLow < 0 ? null : row.IdLow + (row.IdHigh << 16);

	/// <summary>What the two page fills wrote for this row's level, or null before they have.</summary>
	private static Engine_UiPageFills.Fill? Filled(Engine_UiExpandableMenu.Item? row) =>
		LevelOf(row) is { } level ? Engine_UiPageFills.ForRow(ScreenId, (uint)level) : null;

	/// <summary>The record the page's 3D preview shows for a level, or null.</summary>
	public static uint? PreviewRecord(Engine_UiExpandableMenu.Item? row) =>
		Filled(row) is not { } fill
			? null
			: fill.Records.TryGetValue("preview", out uint item)
				? item
				: fill.Records.TryGetValue("card", out uint card) ? card : null;

	/// <summary>Everything the two page fills state for one highlighted level.</summary>
	public sealed class Statements
	{
		public readonly Dictionary<string, string> Values = new();
		public readonly Dictionary<string, Color> Tints = new();
		public readonly Dictionary<string, float> Opacities = new();
	}

	private static Engine_UiPageFills.Fill? _from;

	private static Statements? _state;

	/// <summary>The page's statements for the highlighted level; null while its fills have not run for it.</summary>
	public static Statements? State(Engine_UiExpandableMenu.Item? row)
	{
		Engine_UiPageFills.Fill? fill = Filled(row);
		if (!ReferenceEquals(fill, _from))
		{
			_from = fill;
			_state = fill is null ? null : From(fill);
		}

		return _state;
	}

	/// <summary>The fills' writes as the page's three channels.</summary>
	public static Statements From(Engine_UiPageFills.Fill fill)
	{
		var said = new Statements();
		foreach ((string path, string text) in fill.Texts)
		{
			said.Values[path] = text;
		}

		foreach ((string path, Color colour) in fill.Colours)
		{
			said.Tints[path] = colour;
		}

		foreach ((string path, float opacity) in fill.Opacities)
		{
			said.Opacities[path] = opacity;
		}

		return said;
	}
}
