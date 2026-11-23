using System.Collections.Generic;
using System.Globalization;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE ITEM EACH SCREEN'S PREVIEW SHOWS, as the guest's preview set and hide state it
/// (preview_item_stated.cpp): the record the set was handed, per screen, until that screen's hide.
/// The page fills that call the set run for the row the highlight reader answers — Godot's — so on
/// a list Godot owns this is Godot's row, a guest tick behind the cursor.
/// </summary>
public static class Engine_UiPreviewItems
{
	private static readonly Dictionary<uint, uint> Shown = new();

	/// <summary>`@PreviewItem.Set`: screen, record (hex).</summary>
	public static void Set(Engine_UiModel.GuiNativeCall call)
	{
		if (uint.TryParse(call.Argument(0).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint screen) &&
			uint.TryParse(call.Argument(1).Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint record))
		{
			Shown[screen] = record;
		}
	}

	/// <summary>`@PreviewItem.Hide`: screen.</summary>
	public static void Hide(Engine_UiModel.GuiNativeCall call)
	{
		if (uint.TryParse(call.Argument(0).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint screen))
		{
			Shown.Remove(screen);
		}
	}

	/// <summary>The guest is gone: so is every preview.</summary>
	public static void Reset() => Shown.Clear();

	/// <summary>The record the screen's preview shows, or 0.</summary>
	public static uint Record(uint screen) => Shown.TryGetValue(screen, out uint record) ? record : 0u;
}
