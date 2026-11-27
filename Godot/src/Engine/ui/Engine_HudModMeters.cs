using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Refii.Ui;

/// <summary>
/// A MOD'S HUD METERS (<c>meter</c> / <c>meter_remove</c> on every surface: <c>refii_mod_meter</c>,
/// <c>Refii.meter</c>, <c>RefiiHost.meter</c>): a labelled bar at a value of a maximum — a timer,
/// a boss bar — shown to one player or every player, without the mod building a widget. Each
/// HUD view builds the meters its player sees as mod HUD pieces (<see cref="Engine_HudModPieces"/>:
/// the HUD's own fonts for the label, the meter primitive Souls Combat's bars ship with for the bar),
/// stacked under the top centre of that player's screen — their half on split screen — in the order
/// first shown. The value and maximum are read live every frame (<see cref="Resolve"/>); a label,
/// style, player or set change rebuilds the stack (<see cref="Generation"/>).
/// </summary>
/// <remarks>
/// <para>THE GAME'S OWN METER ART IS NOT USED YET. <c>hud.bgf</c> ships <c>GenericMeter</c> and
/// <c>CrucibleMeter</c> (slidefader2 pieces authored dark: their fader is never raised by the
/// shipped game, memory refii-hud-field-map), and a copy of a shipped piece follows the original's
/// live element state, so a clone of either is dark too until its fader is stated per copy on the
/// SlideFader2 channel. That is the open step; the primitives here are the HUD's own meter kind.</para>
/// <para>Pure composition (no Godot node): held by tests.</para>
/// </remarks>
public static class Engine_HudModMeters
{
	/// <summary>
	/// A meter's look: a PRIMITIVE (bar / boss / timer — its bar size in design units, 720-high,
	/// colours and label size) or a COPY OF THE GAME'S OWN ART (generic / crucible / price):
	/// <paramref name="CloneOf"/> the HUD piece the copy is cut from (the fader element's piece,
	/// <c>slidefader2.&lt;Name&gt;.&lt;Element&gt;</c> as <see cref="Engine_HudLayout.FromBindings"/> cuts
	/// it), <paramref name="MeterName"/> the <c>MeterName</c> the copy's bar scrubs under (remapped to
	/// the meter's value; "" for art with no bar), <paramref name="ValueText"/> whether the value is
	/// drawn over the art as a whole number in the HUD's own font (the price widget's bowl: its
	/// authored text element carries no string and so no mesh to write into), and
	/// <paramref name="RaiseSlide"/> the fader's raise — the SlideFader2 channel's 2, the owner's
	/// slide named 'On' in hud.bgf — stated for the copy alone. <paramref name="Size"/> is then the
	/// art's footprint for stacking.
	/// </summary>
	public sealed record Style(
		string Name, Vector2 Size, Color Fill, Color Back, Color Chip, Color Border, int TextSize,
		string CloneOf = "", string MeterName = "", bool ValueText = false, int RaiseSlide = 0)
	{
		/// <summary>A copy of the game's own art rather than a primitive.</summary>
		public bool IsCopy => CloneOf.Length != 0;
	}

	/// <summary>The fader's raise on the SlideFader2 channel: slide 2 ('On' in hud.bgf for GenericMeter, CrucibleMeter and PriceWidget; read 2026-10-22 in the HUD meter lab).</summary>
	public const int FaderRaise = 2;

	/// <summary>
	/// The styles a call may name; "bar" is the default. The copies name the pieces hud.bgf binds:
	/// <c>slidefader2.GenericMeter.GenericMeter</c> (ordinal 537, its bar <c>meter.GenericMeter.Bar</c>
	/// at (-260, 1), 520 wide), <c>slidefader2.CrucibleMeter.CrucibleMeter</c> (532, bar
	/// <c>meter.CrucibleMeter.Bar</c> at (-240, -250)) and <c>slidefader2.PriceWidget.PriceWidget</c>
	/// (580, a Text element in its bowl, no meter) — each authored INACTIVE on its master slide, lit
	/// by its 'On' slide, which the shipped game never plays.
	/// </summary>
	public static readonly IReadOnlyList<Style> Styles = new[]
	{
		new Style("bar", new Vector2(240f, 12f), new Color(0.93f, 0.74f, 0.33f), new Color(0f, 0f, 0f, 0.7f),
			new Color(0.85f, 0.76f, 0.48f), new Color(0.54f, 0.52f, 0.47f), 18),
		new Style("boss", new Vector2(440f, 16f), new Color(0.72f, 0.11f, 0.11f), new Color(0f, 0f, 0f, 0.75f),
			new Color(0.85f, 0.45f, 0.35f), new Color(0.6f, 0.52f, 0.4f), 20),
		new Style("timer", new Vector2(160f, 8f), new Color(0.55f, 0.8f, 1f), new Color(0f, 0f, 0f, 0.6f),
			new Color(0.8f, 0.9f, 1f), new Color(0.5f, 0.55f, 0.6f), 16),
		new Style("generic", new Vector2(540f, 44f), new Color(0.93f, 0.74f, 0.33f), new Color(0f, 0f, 0f, 0f),
			new Color(0.85f, 0.76f, 0.48f), new Color(0.54f, 0.52f, 0.47f), 18,
			CloneOf: "slidefader2.GenericMeter.GenericMeter", MeterName: "GenericMeter", RaiseSlide: FaderRaise),
		new Style("crucible", new Vector2(500f, 36f), new Color(0.93f, 0.74f, 0.33f), new Color(0f, 0f, 0f, 0f),
			new Color(0.85f, 0.76f, 0.48f), new Color(0.54f, 0.52f, 0.47f), 18,
			CloneOf: "slidefader2.CrucibleMeter.CrucibleMeter", MeterName: "CrucibleMeter", RaiseSlide: FaderRaise),
		new Style("price", new Vector2(260f, 60f), new Color(0.93f, 0.74f, 0.33f), new Color(0f, 0f, 0f, 0f),
			new Color(0.85f, 0.76f, 0.48f), new Color(0.54f, 0.52f, 0.47f), 18,
			CloneOf: "slidefader2.PriceWidget.PriceWidget", ValueText: true, RaiseSlide: FaderRaise),
	};

	/// <summary>
	/// One copy of the game's own meter art a view builds for a meter (<see cref="CopiesFor"/>):
	/// the meter, its style, the clone record (the copy's id, what it is cut from, its meter
	/// remapped to the meter's value, its fader's raise slide stated for the copy alone) and where
	/// it stands in the stack (design units from the top centre: the slot's top edge, which the view puts the art's measured top on).
	/// </summary>
	public readonly record struct Copy(Meter Meter, Style Style, Engine_HudModPieces.Clone Record, Vector2 Offset);

	/// <summary>The id a meter's copy is built under: a group piece (its nested bar scrubs its own tracks), named after the art and the meter.</summary>
	public static string CopyId(Style style, string meterId) => "group." + style.CloneOf.Split('.')[^1] + ".meter." + meterId;

	/// <summary>
	/// Whether a view is a shared couch screen for the meters: the whole window, drawn for this
	/// machine's player one, in local co-op. Such a view shows both local players' meters
	/// (<see cref="Engine_HudModMarkers.ShownTo"/>); a half, an online window and single player show
	/// their own player's.
	/// </summary>
	public static bool SharedScreenView(bool wholeWindow, bool localPlayerTwo, bool localCoop) =>
		wholeWindow && !localPlayerTwo && localCoop;

	/// <summary>
	/// Whether a view rebuilds its meters: the set changed (<paramref name="generation"/> against the
	/// one built from), or the view became or stopped being a shared screen — a co-op join on a shared
	/// screen, or a live switch between split and shared.
	/// </summary>
	public static bool Rebuilds(int generation, int builtGeneration, bool shared, bool builtShared) =>
		generation != builtGeneration || shared != builtShared;

	/// <summary>
	/// The copies of the game's own art a view drawn for <paramref name="viewPlayer"/> builds beside
	/// its primitives, in stack order at their stack places — every meter of a copy style shown to
	/// that player. The clone's <see cref="Engine_HudModPieces.Clone.Slides"/> names the fader
	/// element (the piece's own element) on its raise slide, so the copy stands lit while the
	/// shipped original keeps its authored, dark state.
	/// </summary>
	public static List<Copy> CopiesFor(int viewPlayer, bool sharedScreen)
	{
		var copies = new List<Copy>();
		float y = Top;
		foreach (Meter meter in All())
		{
			if (!Engine_HudModMarkers.ShownTo(meter.Player, viewPlayer, sharedScreen))
			{
				continue;
			}

			Style style = StyleNamed(meter.Style);
			if (meter.Label.Length != 0)
			{
				y += style.TextSize + 4f;
			}

			if (style.IsCopy)
			{
				string source = SourcePrefix + meter.Id;
				var values = new Dictionary<string, string>(StringComparer.Ordinal);
				if (style.MeterName.Length != 0)
				{
					values[style.MeterName] = source + ".value/" + source + ".max";
				}

				string element = style.CloneOf.Split('.')[^1];
				var clone = new Engine_HudModPieces.Clone(
					CopyId(style, meter.Id), "refii", style.CloneOf, Vector2.Zero, 0f, new List<string>(), string.Empty,
					new List<string>(), values, new Dictionary<string, string>(StringComparer.Ordinal),
					new Dictionary<string, int>(StringComparer.Ordinal) { [element] = style.RaiseSlide });
				// The stack's top edge for this art; the view puts the art's measured top on it.
				copies.Add(new Copy(meter, style, clone, new Vector2(0f, y)));
			}

			y += style.Size.Y + Gap;
		}

		return copies;
	}

	/// <summary>The style a name selects: "bar" for "" or an unknown name.</summary>
	public static Style StyleNamed(string name)
	{
		foreach (Style style in Styles)
		{
			if (string.Equals(style.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return style;
			}
		}

		return Styles[0];
	}

	/// <summary>One meter. <paramref name="Player"/> 1 or 2, 0 for every player; <paramref name="Order"/> its place in the stack.</summary>
	public sealed record Meter(string Id, int Player, string Label, float Value, float Max, string Style, int Order);

	/// <summary>The source prefix a meter's pieces read: <c>mod:meter.&lt;id&gt;.value</c> / <c>.max</c>.</summary>
	public const string SourcePrefix = "mod:meter.";

	/// <summary>Where the stack starts under the top of the screen, and the gap between meters, in design units.</summary>
	public const float Top = 44f;
	public const float Gap = 10f;

	private static readonly Dictionary<string, Meter> _meters = new(StringComparer.Ordinal);
	private static int _nextOrder;

	/// <summary>Bumps whenever the set, a label, a style or a player changes; a value change does not.</summary>
	public static int Generation { get; private set; }

	public static int Count => _meters.Count;

	/// <summary>The meters standing, in stack order, for <c>RefiiHost.meters</c> and the debug channel.</summary>
	public static List<Meter> All()
	{
		var list = new List<Meter>(_meters.Values);
		list.Sort((a, b) => a.Order.CompareTo(b.Order));
		return list;
	}

	/// <summary>Shows or restates the meter <paramref name="id"/>. False for no id.</summary>
	public static bool Set(string id, int player, string label, float value, float max, string style)
	{
		id = (id ?? string.Empty).Trim();
		if (id.Length == 0)
		{
			return false;
		}

		int who = player is 1 or 2 ? player : 0;
		string look = StyleNamed(style).Name;
		string text = label ?? string.Empty;
		float v = float.IsFinite(value) ? value : 0f;
		float m = float.IsFinite(max) && max > 0f ? max : Math.Max(v, 1f);
		if (_meters.TryGetValue(id, out Meter? standing))
		{
			if (standing.Player != who || standing.Label != text || standing.Style != look)
			{
				++Generation;
			}

			_meters[id] = standing with { Player = who, Label = text, Value = v, Max = m, Style = look };
			return true;
		}

		_meters[id] = new Meter(id, who, text, v, m, look, _nextOrder++);
		++Generation;
		return true;
	}

	/// <summary>Removes the meter; false when none stands under the id.</summary>
	public static bool Remove(string id)
	{
		if (!_meters.Remove((id ?? string.Empty).Trim()))
		{
			return false;
		}

		++Generation;
		return true;
	}

	/// <summary>Everything goes (the session ends).</summary>
	public static void Clear()
	{
		if (_meters.Count != 0)
		{
			++Generation;
		}

		_meters.Clear();
	}

	/// <summary>
	/// A meter's value or maximum for a piece's source text (<c>mod:meter.&lt;id&gt;.value</c>,
	/// <c>mod:meter.&lt;id&gt;.max</c>); null for any other text, so the readers behind it answer.
	/// </summary>
	public static float? Resolve(string source)
	{
		if (source is null || !source.StartsWith(SourcePrefix, StringComparison.Ordinal))
		{
			return null;
		}

		string rest = source[SourcePrefix.Length..];
		int dot = rest.LastIndexOf('.');
		if (dot <= 0)
		{
			return null;
		}

		string id = rest[..dot];
		string field = rest[(dot + 1)..];
		if (!_meters.TryGetValue(id, out Meter? meter))
		{
			return null;
		}

		return field switch
		{
			"value" => Math.Clamp(meter.Value, 0f, meter.Max),
			"max" => meter.Max,
			_ => null,
		};
	}

	/// <summary>
	/// The pieces a view drawn for <paramref name="viewPlayer"/> (1 or 2) builds: every meter
	/// <see cref="Engine_HudModMarkers.ShownTo"/> that player, stacked under the top centre in
	/// stack order — a label line and, under it, the bar.
	/// </summary>
	public static List<Engine_HudModPieces.Piece> PiecesFor(int viewPlayer, bool sharedScreen)
	{
		var pieces = new List<Engine_HudModPieces.Piece>();
		float y = Top;
		foreach (Meter meter in All())
		{
			if (!Engine_HudModMarkers.ShownTo(meter.Player, viewPlayer, sharedScreen))
			{
				continue;
			}

			Style style = StyleNamed(meter.Style);
			float left = -style.Size.X * 0.5f;
			string source = SourcePrefix + meter.Id;
			if (meter.Label.Length != 0)
			{
				pieces.Add(new Engine_HudModPieces.Piece(
					"meter." + meter.Id + ".label", "refii", Engine_HudModPieces.Kind.Text, Engine_HudLayout.Anchor.TopCentre,
					new Vector2(0f, y), new Vector2(style.Size.X, style.TextSize), string.Empty, string.Empty, style.Fill, style.Back,
					string.Empty, Text: meter.Label, TextSize: style.TextSize, Align: 1));
				y += style.TextSize + 4f;
			}

			// A copy style takes the game's own art instead (CopiesFor, built by the view); the
			// label above it is still this piece, and the stack steps over the art's footprint.
			// A copy that shows its value as a number (the price widget) gets it as a text piece
			// centred on the art, in the HUD's own font.
			if (style.IsCopy && style.ValueText)
			{
				pieces.Add(new Engine_HudModPieces.Piece(
					"meter." + meter.Id + ".value", "refii", Engine_HudModPieces.Kind.Text, Engine_HudLayout.Anchor.TopCentre,
					new Vector2(-24f, y + style.Size.Y * 0.5f - style.TextSize * 0.5f), new Vector2(style.Size.X, style.TextSize),
					string.Empty, string.Empty, style.Fill, style.Back, string.Empty,
					Text: source + ".value", Format: "0", TextSize: style.TextSize, Align: 1));
			}

			if (!style.IsCopy)
			{
				pieces.Add(new Engine_HudModPieces.Piece(
					"meter." + meter.Id, "refii", Engine_HudModPieces.Kind.Meter, Engine_HudLayout.Anchor.TopCentre,
					new Vector2(left, y), style.Size, source + ".value", source + ".max", style.Fill, style.Back, string.Empty,
					Chip: style.Chip, Border: style.Border, BorderWidth: 1.5f, Corner: 1.5f));
			}

			y += style.Size.Y + Gap;
		}

		return pieces;
	}

	/// <summary>A meter as a script dictionary's rows: id, player, label, value, max, style.</summary>
	public static string Describe(Meter meter) =>
		string.Create(CultureInfo.InvariantCulture,
			$"{meter.Id} player={meter.Player} '{meter.Label}' {meter.Value:0.##}/{meter.Max:0.##} {meter.Style}");
}
