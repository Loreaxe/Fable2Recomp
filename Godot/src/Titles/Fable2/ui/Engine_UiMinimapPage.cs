using System.Collections.Generic;
using Godot;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The world map's REGION MAP page — screen 23's `Page2`, the one a stick-right turns to from
/// the region readout. Owned: the guest's own page sub-state (`screen+128`, refresh
/// `sub_829ABFC0`) ticks only while ITS page index is 1, and the host owns the book, so this
/// states everything the guest would have written.
///
/// What the guest writes (all from `sub_829ABFC0`):
/// <list type="bullet">
/// <item>`Page2.Contents.Bowerstone` — the map image: texture = the region's
/// `MinimapBackgroundImage`, `rotation.z = MinimapDegreesRotationToNorth·π/180`.</item>
/// <item>`Page2.Contents.Legend.KeyName{1..13}` / `KeyImage{1..13}` — a legend of up to nine
/// rows, chosen by whether the hero is in this region and whether it has a proper minimap;
/// the rest hidden.</item>
/// <item>Markers — not elements but 24-unit sprites on the GUI draw list, riding
/// `Page2.Contents`: hero (`minimap16`, turned to face), dog (`minimap11`), the primary quest's
/// trail target (`minimap1`, or `minimap17` when the quest is in another region), other quests'
/// objectives (`minimap3`), travelling merchants (`minimap10`).</item>
/// </list>
///
/// The record: `LevelData`'s `MinimapInfo` table (alias `Fnv1("MinimapInfo")` in globals.gdb),
/// child `regionmap_&lt;firstLevel&gt;_&lt;scenario&gt;` lower-cased with ` ( ) / \` turned to `_`
/// (`sub_821E6848` passes at 0x829AC3xx), falling back to `&lt;firstLevel&gt;` alone. Its fields:
/// `MinimapBackgroundImage` (a resource path whose string hash IS the texture hash),
/// `MinimapDegreesRotationToNorth`, `IconTransformRotation`, `IconTransformX/Y/Z`,
/// `IconTransformScale`, `HasProperMinimap` — inherited through `parent`, which the guest's
/// NGDB read does too.
///
/// The projection, `sub_829AEAB8` (0x829AEAE4..0x829AEC00), world metres to element units:
/// <code>
///   n      = world / extent                       extent = max(maxX − minX, maxY − minY) of the
///                                                 CURRENT level (L+152..164); raw, no min term
///   m      = 240 · (n − 0.5)                      (x from world.x, y from world.y)
///   θ      = −(DegreesToNorth + IconRotation)·π/180
///   r      = Scale · (m.x·cosθ + m.y·sinθ, −m.x·sinθ + m.y·cosθ)
///   screen = Bowerstone.position + (IconTransformX, IconTransformZ) + r
/// </code>
/// Markers are plotted only when the current level's key equals the selected region's and the
/// region has a proper minimap. The hero sprite's turn is
/// `(270 − (IconRotation + DegreesToNorth))·π/180 − atan2(fwd.y, fwd.x)`, fwd the hero's
/// rotation applied to (0, 1, 0).
/// </summary>
public static class Engine_UiMinimapPage
{
	/// <summary>The page, as the world map's Active channel names it.</summary>
	public const string Page = "Foreground.Book.Pages.Page2";

	public const string Contents = Page + ".Contents";

	/// <summary>The map image element — the guest's `dword_8349D70C`.</summary>
	public const string MapImage = "Page2.Contents.Bowerstone";

	private const string LegendName = "Page2.Contents.Legend.KeyName";

	private const string LegendImage = "Page2.Contents.Legend.KeyImage";

	/// <summary>`KeyName1..13` / `KeyImage1..13` are authored; the guest hides the rest.</summary>
	public const int LegendSlots = 13;

	/// <summary>The guest's page-turn cooldown (`*(float*)(a1+44) > 0.5`).</summary>
	private const float FlipCooldownSeconds = 0.5f;

	/// <summary>Half the marker sprite: corners at ±12 (`flt_82100FE0` / `flt_8209BE0C`).</summary>
	public const float MarkerHalfExtent = 12f;

	/// <summary>`240.0`, the map's half-span in element units in `sub_829AEAB8`.</summary>
	private const float MapHalfSpan = 240f;

	private const string IconFolder = @"art\gui\wheelicons\minimap\minimap";

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

	/// <summary>The record for one sampled region, or null when the GDB names none for it.</summary>
	public static Info? Resolve(in Refii.Host.HostBridge.WorldMapRegion region) =>
		Resolve(region.LevelName, region.Scenario, region.Name);

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

	/// <summary>
	/// The sample is for the highlighted row (the pump answers a tick late). The row is the guest's own
	/// region row, `sub_829A3A30`'s `index + 999` (0x829A3B4C), and the sample is asked for by that
	/// index into the same walk `sub_82518330` (<see cref="Engine_UiWorldMapPage.RegionOf"/>).
	/// </summary>
	private static bool SampleIsFor(Engine_UiExpandableMenu.Item? row, in Refii.Host.HostBridge.WorldMapRegion region) =>
		region.IsFor(Engine_UiWorldMapPage.RegionOf(row));

	/// <summary>Whether the hero stands in the sampled region — the gate on every marker and on
	/// the long legend (`*(L+180) == key(selected)`).</summary>
	public static bool HeroHere(in Refii.Host.HostBridge.WorldMapRegion region, in Fable2Bridge.MinimapLive live) =>
		live.Known && region.LevelKey != 0u && live.LevelKey == region.LevelKey;

	// The legend rows, in the guest's order — (Babel tag, icon number).
	private static readonly (string Tag, int Icon)[] LegendHere =
	{
		("GUI_SCREEN_MAP_KEY_HERO", 16),
		("GUI_SCREEN_MAP_KEY_DOG", 11),
		("GUI_SCREEN_MAP_KEY_CURRENT_QUEST", 1),      // 17 and _EXIT when the quest is elsewhere
		("GUI_SCREEN_MAP_KEY_QUEST", 3),
		("GUI_SCREEN_MAP_KEY_TRAVELLING_MERCHANT", 10),
		("GUI_SCREEN_MAP_KEY_REGION_EXIT", 18),
		("SHOP_WEAPONS_CATEGORY", 22),
		("GUI_SCREEN_MAP_MISCELLANOUS", 24),
		("GUI_SCREEN_MAP_APPEARANCE", 23),
	};

	private static readonly (string Tag, int Icon)[] LegendElsewhere =
	{
		("GUI_SCREEN_MAP_KEY_REGION_EXIT", 18),
		("SHOP_WEAPONS_CATEGORY", 22),
		("GUI_SCREEN_MAP_MISCELLANOUS", 24),
		("GUI_SCREEN_MAP_APPEARANCE", 23),
	};

	/// <summary>The legend rows the guest lists for a record — shared with the pause root's
	/// `sub_826A3658`, which lists the same nine in the same order.</summary>
	public static IReadOnlyList<(string Tag, int Icon)> Legend(Info info, bool heroHere, bool questHere)
	{
		if (!info.Proper)
		{
			return System.Array.Empty<(string, int)>();
		}

		if (!heroHere)
		{
			return LegendElsewhere;
		}

		var rows = new List<(string, int)>(LegendHere);
		if (!questHere)
		{
			rows[2] = ("GUI_SCREEN_MAP_KEY_CURRENT_QUEST_EXIT", 17);
		}

		return rows;
	}

	public static uint IconHash(int icon) =>
		Refii.Core.Engine_GameHash.ResourcePath(IconFolder + icon + ".tex");

	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row, in Refii.Host.HostBridge.WorldMapRegion region,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>();
		if (!Standing(row) || !SampleIsFor(row, region) || Resolve(region) is not { } info)
		{
			return values;
		}

		Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();
		IReadOnlyList<(string Tag, int Icon)> legend = Legend(info, HeroHere(region, live), live.PrimaryQuestHere);
		for (int i = 0; i < legend.Count && i < LegendSlots; ++i)
		{
			values[LegendName + (i + 1)] =
				strings is not null && strings.TryGetString(legend[i].Tag, out string text) && text.Length != 0
					? text
					: legend[i].Tag;
		}

		return values;
	}

	public static IReadOnlyDictionary<string, uint> Textures(
		Engine_UiExpandableMenu.Item? row, in Refii.Host.HostBridge.WorldMapRegion region)
	{
		var textures = new Dictionary<string, uint>();
		if (!Standing(row) || !SampleIsFor(row, region) || Resolve(region) is not { } info)
		{
			return textures;
		}

		if (info.Background != 0u)
		{
			textures[MapImage] = info.Background;
		}

		Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();
		IReadOnlyList<(string Tag, int Icon)> legend = Legend(info, HeroHere(region, live), live.PrimaryQuestHere);
		for (int i = 0; i < legend.Count && i < LegendSlots; ++i)
		{
			textures[LegendImage + (i + 1)] = IconHash(legend[i].Icon);
		}

		return textures;
	}

	public static IReadOnlyDictionary<string, bool> Active(
		Engine_UiExpandableMenu.Item? row, in Refii.Host.HostBridge.WorldMapRegion region)
	{
		var active = new Dictionary<string, bool>();
		if (!Standing(row))
		{
			return active;
		}

		int shown = 0;
		if (SampleIsFor(row, region) && Resolve(region) is { } info)
		{
			Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();
			shown = Legend(info, HeroHere(region, live), live.PrimaryQuestHere).Count;
		}

		for (int i = 1; i <= LegendSlots; ++i)
		{
			active[LegendName + i] = i <= shown;
			active[LegendImage + i] = i <= shown;
		}

		return active;
	}

	public static IReadOnlyDictionary<string, float> Rotations(
		Engine_UiExpandableMenu.Item? row, in Refii.Host.HostBridge.WorldMapRegion region)
	{
		var rotations = new Dictionary<string, float>();
		if (Standing(row) && SampleIsFor(row, region) && Resolve(region) is { } info)
		{
			rotations[MapImage] = Mathf.DegToRad(info.DegreesToNorth);
		}

		return rotations;
	}

	/// <summary>One marker to draw: its icon, its place in `Page2.Contents` units, its turn.</summary>
	public readonly record struct Marker(uint Texture, Vector2 Position, float Rotation, float Depth);

	/// <summary>
	/// The markers for this frame, or an empty list — the guest's set, from the guest's state:
	/// hero and dog from the entity feed, the trail target from the hero's components, other
	/// quests from the log's objective entities. Nothing when the hero is elsewhere or the
	/// region has no proper minimap.
	/// </summary>
	public static List<Marker> Markers(
		Engine_UiExpandableMenu.Item? row, in Refii.Host.HostBridge.WorldMapRegion region,
		Vector3? mapImagePosition)
	{
		var markers = new List<Marker>();
		if (!Standing(row) || !SampleIsFor(row, region) || Resolve(region) is not { } info ||
			!info.Proper || mapImagePosition is not { } mapAt)
		{
			return markers;
		}

		Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();
		if (!HeroHere(region, live) || live.Extent <= 0f)
		{
			return markers;
		}

		float depth = 0f;
		Vector2 origin = new(mapAt.X, mapAt.Y);

		// The hero — the menu owner's (`live.Hero`), so player two's map plots player two: the
		// sprite is turned to face the way that hero does.
		if (Place(info, origin, live.Hero, live.Extent) is { } heroAt)
		{
			float facing = Forward(live.Hero) is { } forward ? Mathf.Atan2(forward.Y, forward.X) : 0f;
			float turn = Mathf.DegToRad(270f - (info.IconRotation + info.DegreesToNorth)) - facing;
			markers.Add(new Marker(IconHash(16), heroAt, turn, depth));
			depth += 0.1f;
		}

		if (Place(info, origin, live.Dog, live.Extent) is { } dogAt)
		{
			markers.Add(new Marker(IconHash(11), dogAt, 0f, depth));
			depth += 0.1f;
		}

		if (live.HasTrailTarget)
		{
			markers.Add(new Marker(
				IconHash(live.PrimaryQuestHere ? 1 : 17),
				Project(info, origin, live.TrailTarget, live.Extent), 0f, depth));
			depth += 0.1f;
		}

		foreach (Fable2Bridge.QuestEntry quest in Fable2Bridge.GetQuestLog())
		{
			if (quest.Primary || quest.ObjectiveEntity == 0u ||
				!Refii.Game.GameEntities.TryGuestPosition(quest.ObjectiveEntity, out Vector3 at))
			{
				continue;
			}

			markers.Add(new Marker(IconHash(3), Project(info, origin, at, live.Extent), 0f, depth));
			depth += 0.1f;
		}

		foreach (uint merchant in live.Merchants ?? System.Array.Empty<uint>())
		{
			if (!Refii.Game.GameEntities.TryGuestPosition(merchant, out Vector3 at))
			{
				continue;
			}

			markers.Add(new Marker(IconHash(10), Project(info, origin, at, live.Extent), 0f, depth));
			depth += 0.1f;
		}

		return markers;
	}

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
