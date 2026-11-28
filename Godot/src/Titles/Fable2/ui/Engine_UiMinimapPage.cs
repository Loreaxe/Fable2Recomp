using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The world map's REGION MAP page — screen 23's `Page2`, the one a stick-right turns to from the region
/// readout. EVERYTHING ON IT IS ITS OWN FILL'S STATEMENT: the page's vf +12 `sub_829ABFC0` (disc
/// `sub_828FE480`), run by the world map's Update for the highlighted region row under the page-fill scope
/// "row.minimap" (gui_screen_stub_world_map.cpp; page_fills_stated.cpp, minimap_page_stated.cpp), keyed by
/// the row's spawned id (`index + 999`, `addi r31,r10,-0x3E7` 0x829AC05C). The fill resolves the region's
/// `MinimapInfo` record itself and states:
/// <list type="bullet">
/// <item>`Page2.Contents.Bowerstone` — the map image: its texture (the record's `MinimapBackgroundImage`,
/// the inline store 0x829AC798..0x829AC7A0) and its rotation (`sub_824254C8`, z = degrees to north ·
/// π/180, 0x829AC7C4).</item>
/// <item>`Page2.Contents.Legend.KeyName{1..13}` / `KeyImage{1..13}` — the legend rows it shows, their
/// texts and icons, and the ones it hides.</item>
/// <item>The projection (`map`: north, icon rotation, icon offset, icon scale, proper, extent — the page's
/// own fields at the first icon) and each icon it hands its builder `sub_829AE1C8` (`icon`: texture,
/// world point, facing): hero, dog, the trail target, other quests, travelling merchants — 24-unit
/// sprites riding `Page2.Contents`.</item>
/// </list>
/// The fill is edge-triggered (0x829AC064..0x829AC074); the Update re-arms it each time the cursor lands
/// on a region row, so a statement is the page as it stood then. Placement is Godot's: an icon's world
/// point goes through <see cref="Project"/>, the builder's own projection `sub_829AEAB8`
/// (0x829AEAE4..0x829AEC00), with the stated fields:
/// <code>
///   n      = world / extent
///   m      = 240 · (n − 0.5)                      (x from world.x, y from world.y)
///   θ      = −(DegreesToNorth + IconRotation)·π/180
///   r      = Scale · (m.x·cosθ + m.y·sinθ, −m.x·sinθ + m.y·cosθ)
///   screen = Bowerstone.position + (IconTransformX, IconTransformZ) + r
/// </code>
/// and an icon with a facing turns by `(270 − (IconRotation + DegreesToNorth))·π/180 − atan2(f.y, f.x)`
/// (0x829AE34C..0x829AE380); one with none does not turn.
///
/// The record lookup (<see cref="Resolve(string, string, string)"/>) stays for the pause root's map and
/// the minimap mod, which key the CURRENT level the same way (`sub_826A4388`).
/// </summary>
public static class Engine_UiMinimapPage
{
	/// <summary>The page, as the world map's Active channel names it.</summary>
	public const string Page = "Foreground.Book.Pages.Page2";

	public const string Contents = Page + ".Contents";

	/// <summary>The map image element — the guest's `dword_8349D70C`.</summary>
	public const string MapImage = "Page2.Contents.Bowerstone";

	private const uint ScreenId = 23u;

	/// <summary>The page fill's scope (gui_screen_stub_world_map.cpp `kMinimap`).</summary>
	private const string Scope = "row.minimap";

	/// <summary>The guest's page-turn cooldown (`*(float*)(a1+44) > 0.5`).</summary>
	private const float FlipCooldownSeconds = 0.5f;

	/// <summary>Half the marker sprite: corners at ±12 (`flt_82100FE0` / `flt_8209BE0C`).</summary>
	public const float MarkerHalfExtent = 12f;

	/// <summary>`240.0`, the map's half-span in element units in `sub_829AEAB8`.</summary>
	private const float MapHalfSpan = 240f;

	/// <summary>The region readout is `Page1`; `Page2` stands in its place while the player has
	/// turned to the map. Host-owned, because the host owns the book.</summary>
	public static bool Shown { get; private set; }

	private static float _flipCooldown;

	/// <summary>
	/// The stick's page turn, as the guest's `sub_829907F0` reads it: right (`x &gt; 0.5`) turns to
	/// the map, left turns back, half a second between turns, and only while a REGION row is
	/// highlighted — any other row drops the page.
	/// </summary>
	public static void DriveFlip(bool regionRow, int horizontal, float delta)
	{
		if (!regionRow)
		{
			Shown = false;
			_flipCooldown = 0f;
			return;
		}

		_flipCooldown -= delta;
		if (_flipCooldown > 0f || horizontal == 0)
		{
			return;
		}

		if (horizontal > 0 && !Shown)
		{
			Shown = true;
			_flipCooldown = FlipCooldownSeconds;
		}
		else if (horizontal < 0 && Shown)
		{
			Shown = false;
			_flipCooldown = FlipCooldownSeconds;
		}
	}

	public static void Reset()
	{
		Shown = false;
		_flipCooldown = 0f;
	}

	/// <summary>One region's `MinimapInfo` record, as the guest reads it.</summary>
	public sealed record Info(
		uint Background, float DegreesToNorth, float IconRotation, Vector3 IconOffset,
		float IconScale, bool Proper);

	private static readonly uint TableAlias = Refii.Core.Engine_GameHash.Fnv1("MinimapInfo");
	private static readonly uint BackgroundField = Refii.Core.Engine_GameHash.Fnv1("MinimapBackgroundImage");
	private static readonly uint DegreesField = Refii.Core.Engine_GameHash.Fnv1("MinimapDegreesRotationToNorth");
	private static readonly uint IconRotationField = Refii.Core.Engine_GameHash.Fnv1("IconTransformRotation");
	private static readonly uint IconXField = Refii.Core.Engine_GameHash.Fnv1("IconTransformX");
	private static readonly uint IconYField = Refii.Core.Engine_GameHash.Fnv1("IconTransformY");
	private static readonly uint IconZField = Refii.Core.Engine_GameHash.Fnv1("IconTransformZ");
	private static readonly uint IconScaleField = Refii.Core.Engine_GameHash.Fnv1("IconTransformScale");
	private static readonly uint ProperField = Refii.Core.Engine_GameHash.Fnv1("HasProperMinimap");

	private static readonly Dictionary<string, Info?> _resolved = new();

	private static string _said = string.Empty;

	/// <summary>The record for a level by name and scenario — the pause root's lookup
	/// (`sub_826A4388`) keys the CURRENT level the same way.</summary>
	public static Info? Resolve(string levelName, string? scenarioName, string label)
	{
		if (levelName.Length == 0)
		{
			return null;
		}

		string scenario = string.IsNullOrEmpty(scenarioName) ? "DefaultScenario" : scenarioName;
		string key = levelName + "\u0001" + scenario;
		if (_resolved.TryGetValue(key, out Info? cached))
		{
			return cached;
		}

		Info? info = null;
		if (Engine_GameGdb.Database is { } gdb && gdb.TryLookup(TableAlias, out GdbCursor table))
		{
			string full = Sanitise("regionmap_" + levelName + "_" + scenario);
			string bare = Sanitise(levelName);
			if (!TryChild(gdb, table, full, out GdbCursor record) &&
				!TryChild(gdb, table, bare, out record))
			{
				Say($"[minimap] no MinimapInfo child '{full}' or '{bare}' for '{label}'");
			}
			else
			{
				info = new Info(
					Number(gdb, record, BackgroundField, GdbFieldType.String),
					Float(gdb, record, DegreesField),
					Float(gdb, record, IconRotationField),
					new Vector3(
						Float(gdb, record, IconXField),
						Float(gdb, record, IconYField),
						Float(gdb, record, IconZField)),
					FloatOr(gdb, record, IconScaleField, 1f),
					Number(gdb, record, ProperField, GdbFieldType.Bool) != 0u);
				Say($"[minimap] '{label}' -> '{full}': background={info.Background:X8} " +
					$"north={info.DegreesToNorth} iconRot={info.IconRotation} offset={info.IconOffset} " +
					$"scale={info.IconScale} proper={info.Proper}");
			}
		}

		_resolved[key] = info;
		return info;
	}

	/// <summary>The guest's record-name clean-up: lower case, and each of ` ( ) / // \ \\` to `_`.</summary>
	private static string Sanitise(string name)
	{
		string lower = name.ToLowerInvariant();
		foreach (string from in new[] { " ", "(", ")", "/", "//", "\\", "\\\\" })
		{
			lower = lower.Replace(from, "_");
		}

		return lower;
	}

	private static bool TryChild(GdbMulti gdb, GdbCursor table, string name, out GdbCursor child)
	{
		child = default;
		uint field = Refii.Core.Engine_GameHash.Fnv1(name);
		return gdb.TryFindInherited(table, field, (int)GdbFieldType.Reference, out _, out uint target, out _) &&
			target != 0u && gdb.TryLookup(target, out child);
	}

	private static uint Number(GdbMulti gdb, GdbCursor record, uint field, GdbFieldType type) =>
		gdb.TryFindInherited(record, field, (int)type, out _, out uint value, out _) ? value : 0u;

	private static float Float(GdbMulti gdb, GdbCursor record, uint field) => FloatOr(gdb, record, field, 0f);

	/// <summary>A float field, or an int/enum one (`MinimapDegreesRotationToNorth` is authored as
	/// an enum in the base record), or the fallback.</summary>
	private static float FloatOr(GdbMulti gdb, GdbCursor record, uint field, float fallback)
	{
		if (gdb.TryFindInherited(record, field, (int)GdbFieldType.Float, out _, out uint bits, out _))
		{
			float f = System.BitConverter.Int32BitsToSingle(unchecked((int)bits));
			return float.IsFinite(f) ? f : fallback;
		}

		if (gdb.TryFindInherited(record, field, (int)GdbFieldType.Int, out _, out uint whole, out _) ||
			gdb.TryFindInherited(record, field, (int)GdbFieldType.Enum, out _, out whole, out _))
		{
			return unchecked((int)whole);
		}

		return fallback;
	}

	private static void Say(string line)
	{
		if (_said != line)
		{
			_said = line;
			GD.Print(line);
		}
	}

	/// <summary>Whether the page stands this frame: a region row highlighted, turned to the map.</summary>
	public static bool Standing(Engine_UiExpandableMenu.Item? row) =>
		Shown && Engine_UiWorldMapPage.RegionOf(row) >= 0;

	/// <summary>The highlighted region row's own minimap statement, when it has landed for that row; null
	/// otherwise or while the page does not stand. Says once per statement what it is (SayDrawn), whether
	/// or not the page is turned to.</summary>
	private static Engine_UiPageFills.Fill? Stated(Engine_UiExpandableMenu.Item? row)
	{
		if (Engine_UiWorldMapPage.RegionOf(row) < 0)
		{
			_drawnFrom = null;
			return null;
		}

		uint id = ((uint)row!.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		Engine_UiPageFills.Fill? fill = Engine_UiPageFills.ForRow(ScreenId, Scope, id);
		SayDrawn(id, fill, Shown);
		return Shown ? fill : null;
	}

	/// <summary>The statement last said, and whether the page stood then (one line each).</summary>
	private static Engine_UiPageFills.Fill? _drawnFrom;

	private static bool _drawnStanding;

	/// <summary>
	/// WHAT THE PAGE IS DRAWN FROM, once per statement and once more when the page is turned to: the
	/// region row's own minimap statement (its map image's texture and turn, its legend's texts and rows,
	/// the projection and the icons) — or, before that row's statement has landed, that the page is blank.
	/// A run reads the source of every map it showed from this line (the native side's `the minimap fill
	/// stated` names the same row). No icons and no projection is the fill's own answer for a region the hero
	/// is not in, or one with no proper minimap (`lwz r10,0xB4(r11) ; cmplw cr6,r10,r23` 0x829AC864 / 0x829AC86C,
	/// `beq` 0x829AC888; disc 0x828FED24 / 0x828FED2C / 0x828FED48): the map image and the legend are still the
	/// statement's.
	/// </summary>
	private static void SayDrawn(uint id, Engine_UiPageFills.Fill? fill, bool standing)
	{
		// The test host has no Godot runtime to print through.
		if (Refii.Core.Engine_AppRoot.IsTestHost)
		{
			return;
		}

		if (fill is null)
		{
			Say($"[minimap-page] region row {id}: no statement of its own yet — the page is blank until it lands");
			return;
		}

		if (ReferenceEquals(fill, _drawnFrom) && standing == _drawnStanding)
		{
			return;
		}

		_drawnFrom = fill;
		_drawnStanding = standing;
		uint image = 0u;
		foreach ((string path, uint hash) in fill.Textures)
		{
			if (path.EndsWith(MapImage, System.StringComparison.Ordinal))
			{
				image = hash;
			}
		}

		float turn = 0f;
		foreach ((string path, Vector3 rotation) in fill.Rotations)
		{
			if (path.EndsWith(MapImage, System.StringComparison.Ordinal))
			{
				turn = rotation.Z;
			}
		}

		int shown = 0;
		foreach (bool on in fill.Shown.Values)
		{
			shown += on ? 1 : 0;
		}

		string projection = fill.Projection is { } map
			? string.Create(CultureInfo.InvariantCulture,
				$"north {map.DegreesToNorth:0.###} icon turn {map.IconRotation:0.###} offset ({map.IconOffset.X:0.###}, {map.IconOffset.Y:0.###}, {map.IconOffset.Z:0.###}) scale {map.IconScale:0.###} proper {map.Proper} extent {map.Extent:0.###}")
			: "none (no icons: the hero is not in this region, or it has no proper minimap)";
		string what = string.Create(CultureInfo.InvariantCulture,
			$"map image {image:X8} turned {turn:0.###} rad, {fill.Textures.Count} texture(s), {fill.Texts.Count} text(s)");
		string legend = string.Create(CultureInfo.InvariantCulture,
			$"legend rows {shown} shown / {fill.Shown.Count - shown} hidden, {fill.Icons.Count} icon(s)");
		Say($"[minimap-page] region row {id}: {(standing ? "drawn from" : "stated by")} the guest's fill — {what}, " +
			$"{legend}, projection {projection}" + (standing ? string.Empty : "; the page draws it when the stick turns to the map"));
	}

	/// <summary>
	/// The legend's texts as the fill wrote them, by path; a row it hid is blanked. Until the row's own
	/// statement lands (a guest tick behind the cursor) every text the page's last statement wrote is blank
	/// rather than another region's.
	/// </summary>
	public static IReadOnlyDictionary<string, string> Values(Engine_UiExpandableMenu.Item? row)
	{
		var values = new Dictionary<string, string>(System.StringComparer.Ordinal);
		if (!Standing(row))
		{
			// The statement's line is said while the row is up, turned to or not (SayDrawn).
			Stated(row);
			return values;
		}

		if (Stated(row) is { } fill)
		{
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
			}
		}
		else if (Engine_UiPageFills.Of(ScreenId, Scope) is { } last)
		{
			foreach (string path in last.Texts.Keys)
			{
				values[path] = string.Empty;
			}
		}

		return values;
	}

	/// <summary>The map image's and the legend icons' textures as the fill stored them.</summary>
	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row)
	{
		var textures = new Dictionary<string, uint>(System.StringComparer.Ordinal);
		if (Stated(row) is { } fill)
		{
			foreach ((string path, uint hash) in fill.Textures)
			{
				if (hash != 0u)
				{
					textures[path] = hash;
				}
			}
		}

		return textures;
	}

	/// <summary>The legend rows the fill showed and hid; until the row's statement lands, the rows the last
	/// statement showed are hidden.</summary>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row)
	{
		var active = new Dictionary<string, bool>(System.StringComparer.Ordinal);
		if (!Standing(row))
		{
			return active;
		}

		if (Stated(row) is { } fill)
		{
			foreach ((string path, bool shown) in fill.Shown)
			{
				active[path] = shown;
			}
		}
		else if (Engine_UiPageFills.Of(ScreenId, Scope) is { } last)
		{
			foreach (string path in last.Shown.Keys)
			{
				active[path] = false;
			}
		}

		return active;
	}

	/// <summary>The map image's turn as the fill handed it (`sub_824254C8`'s z).</summary>
	public static IReadOnlyDictionary<string, float> Rotations(Engine_UiExpandableMenu.Item? row)
	{
		var rotations = new Dictionary<string, float>(System.StringComparer.Ordinal);
		if (Stated(row) is { } fill)
		{
			foreach ((string path, Vector3 turn) in fill.Rotations)
			{
				rotations[path] = turn.Z;
			}
		}

		return rotations;
	}

	/// <summary>One marker to draw: its icon, its place in `Page2.Contents` units, its turn.</summary>
	public readonly record struct Marker(uint Texture, Vector2 Position, float Rotation, float Depth);

	/// <summary>
	/// The markers for this frame — the icons the fill handed its builder, in its order, each placed by
	/// <see cref="Project"/> through the stated projection around the map image's authored position. None
	/// before the row's statement lands; the fill hands none when the hero is not in the region or it has no
	/// proper minimap (0x829AC888 / 0x829ACAF8).
	/// </summary>
	public static List<Marker> Markers(Engine_UiExpandableMenu.Item? row, Vector3? mapImagePosition)
	{
		var markers = new List<Marker>();
		if (Stated(row) is not { Projection: { } projection } fill || mapImagePosition is not { } mapAt ||
			projection.Extent <= 0f)
		{
			return markers;
		}

		var info = new Info(
			0u, projection.DegreesToNorth, projection.IconRotation, projection.IconOffset, projection.IconScale,
			projection.Proper);
		Vector2 origin = new(mapAt.X, mapAt.Y);
		float depth = 0f;
		foreach (Engine_UiPageFills.MinimapIcon icon in fill.Icons)
		{
			markers.Add(new Marker(
				icon.Texture, Project(info, origin, icon.World, projection.Extent), Turn(info, icon.Facing), depth));
			depth += 0.1f;
		}

		return markers;
	}

	/// <summary>
	/// An icon's turn, `sub_829AE1C8`: none for a zero facing (`vcmpeqfp` against zero, 0x829AE30C..0x829AE31C,
	/// then the identity 0x829AE334), else `(270 − (IconRotation + DegreesToNorth))·π/180 − atan2(f.y, f.x)`.
	/// </summary>
	public static float Turn(Info info, Vector3 facing) =>
		facing == Vector3.Zero
			? 0f
			: Mathf.DegToRad(270f - (info.IconRotation + info.DegreesToNorth)) - Mathf.Atan2(facing.Y, facing.X);

	/// <summary>
	/// An entity's place on a region map, in the map's element units around <paramref name="origin"/>:
	/// its position from the entity feed through <see cref="Project"/>. Null for no entity or no position.
	/// One rule for the pause root, this page and a mod's map.
	/// </summary>
	public static Vector2? Place(Info info, Vector2 origin, uint entity, float extent) =>
		entity != 0u && extent > 0f && Refii.Game.GameEntities.TryGuestPosition(entity, out Vector3 at)
			? Project(info, origin, at, extent)
			: null;

	/// <summary>The hero's forward from the entity feed: its rotation applied to the guest's (0, 1, 0); null with none.</summary>
	public static Vector3? Forward(uint hero) =>
		hero != 0u && Refii.Game.GameEntities.TryGuestRotation(hero, out Quaternion rotation)
			? rotation * Vector3.Up
			: null;

	/// <summary>`sub_829AEAB8`: world metres to `Page2.Contents` units.</summary>
	public static Vector2 Project(Info info, Vector2 mapImageAt, Vector3 world, float extent)
	{
		float nx = world.X / extent;
		float ny = world.Y / extent;
		float mx = MapHalfSpan * (nx - 0.5f);
		float my = MapHalfSpan * (ny - 0.5f);
		float theta = -Mathf.DegToRad(info.DegreesToNorth + info.IconRotation);
		float c = Mathf.Cos(theta);
		float s = Mathf.Sin(theta);
		float rx = info.IconScale * ((mx * c) + (my * s));
		float ry = info.IconScale * ((-mx * s) + (my * c));
		return new Vector2(mapImageAt.X + info.IconOffset.X + rx, mapImageAt.Y + info.IconOffset.Z + ry);
	}

	/// <summary>Two channel dictionaries as one; the second wins a shared key.</summary>
	public static IReadOnlyDictionary<string, T> Merge<T>(
		IReadOnlyDictionary<string, T> first, IReadOnlyDictionary<string, T> second)
	{
		if (second.Count == 0)
		{
			return first;
		}

		if (first.Count == 0)
		{
			return second;
		}

		var merged = new Dictionary<string, T>(first.Count + second.Count);
		foreach (var pair in first)
		{
			merged[pair.Key] = pair.Value;
		}

		foreach (var pair in second)
		{
			merged[pair.Key] = pair.Value;
		}

		return merged;
	}
}
