using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// WHAT A BOOK PAGE'S FILL WROTE, as the guest states it (page_fills_stated.cpp): every element the
/// fill named (`sub_82426798`) and the text, slide, shown flag, opacity, text colour or quad tint it handed it,
/// and the records its preview and furniture card took,
/// held per screen and scope until that fill runs again. Scopes: the shop's front page fill
/// `sub_82991800` ("front", screen 22, key 0), real estate's two page fills ("page1" `sub_82998FB0`,
/// "page2" `sub_8299B068`, screen 28) and its book tick ("tick", page 1's `sub_8299A728`), and the row
/// edges' fills ("row": the shop's `sub_826B13B8`, keyed by the row's ticket; upgrade furniture's
/// `sub_826B64D8`, keyed by the level), and the world map's (23) five row pages, one scope per page
/// ("row.region" `sub_829AB0E0`, "row.quest" `sub_829AF638`, "row.job" `sub_829AFE58`, "row.sale"
/// `sub_829AAB50`, "row.family" `sub_829B04E0`), run for the highlighted row each Update and keyed by
/// the row's spawned id; and the item books' row fills ("row": screen 15 `sub_829B5160` + `sub_829B6B50`,
/// 16 `sub_829B6310` + `sub_829B7368`, 25 `sub_829AA588`), keyed by the row's ticket. A page's own fill drops
/// the screen's row and tick statements. A row scope's
/// statement belongs to the row whose id is its key — the edge ran its fills for the row the highlight
/// reader answered, Godot's, a guest tick behind the cursor.
/// </summary>
public static class Engine_UiPageFills
{
	public sealed class Fill
	{
		public uint Key;
		public readonly Dictionary<string, string> Texts = new(System.StringComparer.Ordinal);
		public readonly Dictionary<string, int> Slides = new(System.StringComparer.Ordinal);
		public readonly Dictionary<string, bool> Shown = new(System.StringComparer.Ordinal);

		/// <summary>Opacities as Godot reads them, 0..1 (the guest writes 0..100).</summary>
		public readonly Dictionary<string, float> Opacities = new(System.StringComparer.Ordinal);
		public readonly Dictionary<string, Color> Colours = new(System.StringComparer.Ordinal);

		/// <summary>Quad colours (`sub_82425C58`: the item books' dye swatch), by path.</summary>
		public readonly Dictionary<string, Color> Tints = new(System.StringComparer.Ordinal);

		/// <summary>GDB records the fill named: "preview" (its preview's Set), "card" (furniture's card).</summary>
		public readonly Dictionary<string, uint> Records = new(System.StringComparer.Ordinal);
	}

	private static readonly Dictionary<(uint Screen, string Scope), Fill> Said = new();

	/// <summary>`@PageFill`: screen, scope, key, then per write its kind, the path the fill named and the value.</summary>
	public static void Filled(Engine_UiModel.GuiNativeCall call)
	{
		if (Parse(call) is { } parsed)
		{
			// A page's own fill is the screen's open: what its rows and its ticks were stated as
			// belongs to the last visit.
			if (!IsRowScope(parsed.Scope) && parsed.Scope != Tick)
			{
				Said.Remove((parsed.Screen, Row));
				Said.Remove((parsed.Screen, Tick));
			}

			Said[(parsed.Screen, parsed.Scope)] = parsed.Fill;
		}
	}

	/// <summary>The statement's parts; null when it does not name a screen and a scope.</summary>
	public static (uint Screen, string Scope, Fill Fill)? Parse(Engine_UiModel.GuiNativeCall call)
	{
		if (!uint.TryParse(call.Argument(0).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint screen) ||
			call.Argument(1).Text is not { Length: > 0 } scope)
		{
			return null;
		}

		var fill = new Fill();
		uint.TryParse(call.Argument(2).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out fill.Key);
		int count = call.Arguments?.Count ?? 0;
		for (int at = 3; at + 2 < count; at += 3)
		{
			string kind = call.Argument(at).Text ?? string.Empty;
			string path = call.Argument(at + 1).Text ?? string.Empty;
			string value = call.Argument(at + 2).Text ?? string.Empty;
			if (path.Length == 0)
			{
				continue;
			}

			switch (kind)
			{
				case "text":
					fill.Texts[path] = value;
					break;
				case "slide" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slide):
					fill.Slides[path] = slide;
					break;
				case "shown":
					fill.Shown[path] = value == "1";
					break;
				case "opacity" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float opacity):
					fill.Opacities[path] = opacity / 100f;
					break;
				case "record" when uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint record):
					fill.Records[path] = record;
					break;
				case "colour" or "tint" when value.Length == 8 &&
					uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgba):
					(kind == "tint" ? fill.Tints : fill.Colours)[path] = new Color(
						((rgba >> 24) & 0xFFu) / 255f, ((rgba >> 16) & 0xFFu) / 255f,
						((rgba >> 8) & 0xFFu) / 255f, (rgba & 0xFFu) / 255f);
					break;
			}
		}

		return (screen, scope, fill);
	}

	/// <summary>What the screen's fill under <paramref name="scope"/> last wrote, or null.</summary>
	public static Fill? Of(uint screen, string scope) =>
		Said.TryGetValue((screen, scope), out Fill? fill) ? fill : null;

	/// <summary>The row scope's statement when it was made for <paramref name="key"/>, else null.</summary>
	public static Fill? ForRow(uint screen, uint key) => ForRow(screen, Row, key);

	/// <summary>A named row scope's statement (a world-map row page) when it was made for <paramref name="key"/>, else null.</summary>
	public static Fill? ForRow(uint screen, string scope, uint key) =>
		Of(screen, scope) is { } fill && fill.Key == key ? fill : null;

	public const string Front = "front";

	public const string Row = "row";

	/// <summary>`row`, or a row page's own scope `row.&lt;page&gt;` (the world map's).</summary>
	public static bool IsRowScope(string scope) =>
		scope == Row || scope.StartsWith(Row + ".", System.StringComparison.Ordinal);

	/// <summary>A book's per-tick page restatement (real estate's `sub_8299A728`), stated when it changes.</summary>
	public const string Tick = "tick";

	/// <summary>The guest is gone: so is every fill.</summary>
	public static void Reset() => Said.Clear();
}
