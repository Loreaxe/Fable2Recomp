using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The detail page of every owned-list screen, routed by screen id.
///
/// A book screen fills its detail page on a highlight-change edge, keyed on the GUEST's Lua
/// highlight (`g_Menu:GetHighlightedMenuItemData()`) against a per-screen "last resolved" dword
/// — `screen+624` on abilities, its own offset on each of the others. Godot owns the cursor, so
/// none of those keys ever move and the guest's fill never runs.
///
/// A screen is a PROVIDER, and the TEMPLATE names it. This file used to switch on the screen id
/// six times over — `Owns`, `Values`, `Textures`, `Active`, `Positions`, `Slides` — so one
/// screen's behaviour was spread across six places and each screen could break its own way.
/// Now every screen takes the same path and adding one is a template record.
/// </summary>
public static class Engine_UiDetailPages
{
	/// <summary>The world map — its region page is sampled a tick ahead, see the provider.</summary>
	public const uint WorldMapScreenId = 23u;

	/// <summary>The options screen — all four pages; 3 and 4 place pips as well as text.</summary>
	public const uint OptionsScreenId = 18u;

	/// <summary>The provider this screen's template names, or null when it names none.</summary>
	private static Engine_UiPageProvider? Provider(uint screenId) =>
		Engine_UiTemplates.Screen(screenId) is { } template && template.Provider.Length != 0
			? Engine_UiPageProviders.Find(template.Provider)
			: null;

	/// <summary>
	/// Fills a box page's book from its provider as it draws — the pages no screen feed reaches
	/// (<see cref="Engine_UiPageProvider.FillsOnDraw"/>). False for every other screen.
	/// </summary>
	public static bool FillOnDraw(uint screenId, Engine_UiOwnedBook book, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (Provider(screenId) is not { FillsOnDraw: true } provider)
		{
			return false;
		}

		// The game's text book: the info card's tags (`AcceptText` "GUI_DIALOG_ACCEPT") are drawn
		// resolved, as its Open resolves them (`T displaybox 11 'Accept'`), not as the raw tag.
		Engine_UiPageContext page = Row(screenId, null) with { Strings = strings };
		book.HostValues = provider.Values(page);
		book.HostActive = provider.Active(page);
		book.HostSlides = provider.Slides(page) is { Count: > 0 } slides ? slides : null;
		book.HostTextures = provider.Textures(page);
		book.HostOpacity = provider.Opacities(page);
		return true;
	}

	/// <summary>Screens whose detail page Godot fills.</summary>
	public static bool Owns(uint screenId) => Provider(screenId) is not null;

	/// <summary>Whether this screen's provider needs the guest's option sliders this frame.</summary>
	public static bool NeedsOptionSliders(uint screenId) =>
		Provider(screenId)?.NeedsOptionSliders ?? false;

	/// <summary>
	/// Lets this screen's provider ask the host for what its fill will need. Called before the
	/// values are gathered, on whatever screen is up.
	/// </summary>
	public static void Prepare(uint screenId, Engine_UiExpandableMenu.Item? row) =>
		Provider(screenId)?.Prepare(Row(screenId, row));

	/// <summary>
	/// The host-owned field values for this screen's detail page, as final display text. Empty
	/// for a screen with no provider, whose fields show the file's own captions.
	/// </summary>
	public static IReadOnlyDictionary<string, string> Values(
		uint screenId,
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroVitals vitals,
		Refii.Host.HostBridge.HeroDerived hero,
		List<Refii.Host.HostBridge.CompletedQuest> quests,
		Refii.Host.HostBridge.CrimeScreen crime,
		Refii.Host.HostBridge.WorldMapRegion worldMap,
		Refii.Host.HostBridge.OptionSliders sliders,
		Refii.Text.Engine_BabelDatabase? strings,
		bool optionsPageOpen = false)
	{
		Engine_UiPageContext page = Context(
			screenId, row, vitals, hero, quests, crime, worldMap, sliders, strings,
			optionsPageOpen, false);
		return Provider(screenId)?.Values(page) ?? Engine_UiPageProviders.NoValues;
	}

	/// <summary>
	/// Quads this screen's provider textures itself — see
	/// <see cref="Engine_UiOwnedBook.HostTextures"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, uint> Textures(
		uint screenId, Engine_UiExpandableMenu.Item? row) =>
		Provider(screenId)?.Textures(Row(screenId, row)) ?? Engine_UiPageProviders.NoTextures;

	/// <summary>
	/// Elements this screen's provider switches on or off outright — see
	/// <see cref="Engine_UiOwnedBook.HostActive"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, bool> Active(
		uint screenId, Engine_UiExpandableMenu.Item? row) =>
		Provider(screenId)?.Active(Row(screenId, row)) ?? Engine_UiPageProviders.NoActive;

	/// <summary>
	/// Elements whose opacity this screen's provider states outright — see
	/// <see cref="Engine_UiOwnedBook.HostOpacity"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, float> Opacities(
		uint screenId, Engine_UiExpandableMenu.Item? row) =>
		Provider(screenId)?.Opacities(Row(screenId, row)) ?? Engine_UiPageProviders.NoOpacities;

	/// <summary>
	/// Elements this screen's provider PLACES itself — see
	/// <see cref="Engine_UiOwnedBook.HostPositions"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, Godot.Vector3> Positions(
		uint screenId, Refii.Host.HostBridge.OptionSliders sliders,
		Engine_UiExpandableMenu.Item? row = null,
		Refii.Host.HostBridge.HeroDerived hero = default)
	{
		// The row reaches the placements too: the options provider pins the picked page's
		// contents by it (without it every page pinned Page1 and the Controls/Video/Audio
		// pages stayed parked at x 430 / −400, off the leaf), the logbook its subject page.
		// The derived sample too: the logbook places its alignment and attractiveness pips by
		// it, and without it they stay at the authored centre of their bars.
		Engine_UiPageContext page = Row(screenId, row) with { Sliders = sliders, Hero = hero };
		IReadOnlyDictionary<string, Godot.Vector3> stated =
			Provider(screenId)?.Positions(page) ?? Engine_UiPageProviders.NoPositions;

		// THE PAGE STACK IS PLACED FROM THE SCREEN RECORD, here so every screen gets it
		// without each provider forwarding it. The books disagree about where the stack is
		// authored — x = 0 (on the leaf) in the item family, x = 400 (off a 960-wide canvas)
		// in abilities, shop, world map and logbook — so a screen whose record states
		// `PageStackAt` has it placed, and one that states none keeps its file's own layout.
		// A modded screen states its own; nothing here is a table of shipped books.
		if (Engine_UiTemplates.Screen(screenId) is not { PageStackAt: { } at } screen ||
			screen.PageRootUnit.Length == 0)
		{
			return stated;
		}

		var placed = new Dictionary<string, Godot.Vector3>(stated.Count + 1);
		foreach ((string path, Godot.Vector3 position) in stated)
		{
			placed[path] = position;
		}

		placed[$"Foreground.Book.{screen.PageRootUnit}"] = at;
		return placed;
	}

	/// <summary>Elements this screen's provider TURNS — see <see cref="Engine_UiOwnedBook.HostRotations"/>.</summary>
	public static IReadOnlyDictionary<string, float> Rotations(
		uint screenId, Engine_UiExpandableMenu.Item? row = null) =>
		Provider(screenId)?.Rotations(Row(screenId, row)) ?? Engine_UiPageProviders.NoRotations;

	/// <summary>Elements this screen's provider tints — see <see cref="Engine_UiOwnedBook.HostTints"/>.</summary>
	public static IReadOnlyDictionary<string, Godot.Color> Tints(
		uint screenId, Engine_UiExpandableMenu.Item? row = null) =>
		Provider(screenId)?.Tints(Row(screenId, row)) ?? Engine_UiPageProviders.NoTints;

	/// <summary>
	/// The MOVIE this screen's page wants playing, or empty when it wants none. Only the
	/// abilities page states one — see <see cref="Engine_UiAbilityPage.MovieFor"/>.
	/// </summary>
	public static string Movie(
		uint screenId,
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroVitals vitals) =>
		Refii.Core.Engine_TitleGame.Active.ScreenMovie(screenId, row, vitals);

	/// <summary>
	/// Components this screen's provider SWITCHES itself — see
	/// <see cref="Engine_UiOwnedBook.HostSlides"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, int> Slides(
		uint screenId,
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.WorldMapRegion worldMap,
		bool hostRows,
		Refii.Host.HostBridge.HeroVitals vitals = default,
		Refii.Host.HostBridge.HeroDerived hero = default)
	{
		// The vitals ride along because the abilities page's orb slides are a function of the
		// hero's levels and unspent experience, not of the row alone; the derived sample because
		// the logbook's dog star groups are goToSlide(level) off the dog's stats component.
		Engine_UiPageContext page = Row(screenId, row) with
		{
			WorldMap = worldMap,
			HostRows = hostRows,
			Vitals = vitals,
			Hero = hero,
		};

		IReadOnlyDictionary<string, int> own =
			Provider(screenId)?.Slides(page) ?? Engine_UiPageProviders.NoSlides;

		// The book's pages show for a leaf and hide for a folder — `sub_826C3C78` at 0x826c3d0c:
		// Float3 of the highlighted item 301 (submenu) or 23 (spell assign) -> `HidePages`
		// (slide 2), anything else -> `ShowPages` (slide 1). THE GUEST STATES THIS for every row
		// its own highlight reaches — its tick plays the slide and the clone follows it. Only a
		// HOST-ONLY row (attached from the capture, never laid out by the pool) has no guest
		// highlight to key on, and only then does the rule run here. It is the BOOK's rule, not
		// any one screen's, so it stays outside the provider.
		if (!Owns(screenId) || !hostRows)
		{
			return own;
		}

		var slides = new Dictionary<string, int>(own);
		slides["Foreground.Book"] =
			row is not null && row.EventType != SubmenuEventType && row.EventType != SpellAssignEventType
				? 1
				: 2;
		return slides;
	}

	private const int SubmenuEventType = 301;
	private const int SpellAssignEventType = 23;

	/// <summary>A context carrying only what the row-keyed questions need.</summary>
	private static Engine_UiPageContext Row(uint screenId, Engine_UiExpandableMenu.Item? row) =>
		Context(
			screenId, row, default, default, EmptyQuests, default, default, default,
			null, false, false);

	private static Engine_UiPageContext Context(
		uint screenId,
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroVitals vitals,
		Refii.Host.HostBridge.HeroDerived hero,
		List<Refii.Host.HostBridge.CompletedQuest> quests,
		Refii.Host.HostBridge.CrimeScreen crime,
		Refii.Host.HostBridge.WorldMapRegion worldMap,
		Refii.Host.HostBridge.OptionSliders sliders,
		Refii.Text.Engine_BabelDatabase? strings,
		bool optionsPageOpen,
		bool hostRows) =>
		new()
		{
			ScreenId = screenId,
			Row = row,
			Vitals = vitals,
			Hero = hero,
			Quests = quests,
			Crime = crime,
			WorldMap = worldMap,
			Sliders = sliders,
			Strings = strings,
			OptionsPageOpen = optionsPageOpen,
			HostRows = hostRows,
		};

	private static readonly List<Refii.Host.HostBridge.CompletedQuest> EmptyQuests = new();
}
