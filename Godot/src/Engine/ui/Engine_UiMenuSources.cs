using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// Menu-entry child sources: the template keeps the shape, a named source supplies the rows.
/// `capture` = spawn-capture rows whose parent is the entry's name; `capture:&lt;name&gt;` =
/// the same under another parent name; anything else supplies nothing and warns once.
/// <see cref="Register"/> adds providers without this file knowing them.
/// </summary>
public static class Engine_UiMenuSources
{
	/// <summary>One row a source supplies: what a spawn states, plus whether it commits by identity.</summary>
	/// <param name="Committable">True when the name/id pair is the guest's own; invented rows dispatch by name or command.</param>
	public readonly record struct Row(
		string Name,
		string Label,
		string Icon,
		int EventType,
		uint Id,
		int Rating,
		bool Committable,
		bool Ghosted = false,

		/// <summary>
		/// A HOST ACTION this row performs instead of dispatching to the guest — see
		/// <see cref="Engine_UiScreenActions"/>. A data-area row carries its own, because the
		/// operation is a property of the item and the screen, not of a template entry.
		/// </summary>
		string? Command = null,

		/// <summary>
		/// A GROUP this row belongs under, or empty to sit directly beneath the entry that
		/// sourced it. The builder creates a header row per distinct group, in first-seen order,
		/// and only for groups that actually have rows.
		///
		/// This is the guest's own shape: `sub_826CE8D0` files an item under the category name it
		/// passes as `String4`, and the headers are spawned separately by `sub_826CF098`. A list
		/// whose categories depend on what the player is carrying — the gift screen, whose
		/// generic builder `sub_82430468` walks 27 slots and emits a header only where the count
		/// is non-zero — cannot be authored as fixed entries, and this is how it is expressed.
		/// </summary>
		string Group = "",

		/// <summary>The group header's Babel tag, used only when this row creates the header.</summary>
		string GroupTag = "",

		/// <summary>True for a header the builder synthesised for a group, not a real row.</summary>
		bool IsHeader = false,

		/// <summary>
		/// The row draws with `g_Menu.EmphasisConst` set rather than plain — the guest's
		/// `SetItemEmphasis` (`sub_823DFA98`, which states `String1 = name`, `Float1 = 1` and
		/// runs `g_Menu:SetItemEmphasis()`).
		///
		/// The clothing screen uses it to mark the CURRENT state of a slot: `sub_829B3340`
		/// emphasises the slot's `None` row when nothing is worn there, and clears that emphasis
		/// (`sub_826D0EF8`) when something is.
		/// </summary>
		bool Emphasised = false,

		/// <summary>
		/// Whether this row is one of the things its category's header COUNTS.
		///
		/// The guest's count is not "how many rows are under this header" — it comes from a
		/// separate array `sub_826CF6A8` fills by walking the inventory, so it counts INVENTORY
		/// RECORDS. The clothing screen's `None` row is not one, and counting it put every
		/// equipment header exactly one above the guest's (`Headgear (7)` against `(6)`).
		/// </summary>
		bool Counted = true,

		/// <summary>
		/// The row is a thing the player has not looked at yet — `*(item + 20) == 0`. A header
		/// whose category holds any of these carries `&lt;exclamation.img&gt;` after its count, which
		/// is `sub_826CF098`'s second suffix (`word_820AEFF0`, appended when the count pair's
		/// `[1]` is above zero, and `[1]` counts exactly these).
		/// </summary>
		bool Unviewed = false,

		/// <summary>
		/// WHICH emphasis, when a row wears one of the three the guest's `g_Menu.EmphasisConst`
		/// names rather than the one <see cref="Emphasised"/> stands for. The world map's quest
		/// rows: `sub_829A3BA0` emphasises a PRIMARY quest with 1 and an ACTIVE one with 2
		/// (0x829a3e2c / 0x829a3e4c), and the re-emphasis `sub_829A3260` demotes the primary to
		/// 2 while a sale or family member holds the trail. None defers to <see cref="Emphasised"/>.
		/// </summary>
		Engine_UiMenuConstants.Emphasis Emphasis = Engine_UiMenuConstants.Emphasis.None,

		/// <summary>
		/// The guest has no row for this and activating it is complete host-side
		/// (<see cref="Engine_UiExpandableMenu.Item.HostOnly"/>); it is never stated as the
		/// highlighted row either.
		/// </summary>
		bool HostOnly = false,

		/// <summary>
		/// MOD CONTENT: a row a mod's list states (its script's rows, its published values, its
		/// settings pages, the per-player lists a mod's menu shows) or that a script-registered
		/// source supplied. Never stated as the guest's highlight
		/// (<see cref="Engine_UiExpandableMenu.Item.FromMod"/>): a guest handler resolves a stated
		/// id and some act on the result without checking it — see Engine_UiAbilitySource.
		/// Everything else about the row — its command, its press — is as stated.
		/// </summary>
		bool FromMod = false,

		/// <summary>
		/// The guest OBJECT this row stands for when its id names only a kind — an inventory
		/// row's item instance, where <see cref="Id"/> is the record and two copies of one record
		/// are two objects with their own instance data (a dye). 0 when the row names none.
		/// Copied to <see cref="Engine_UiExpandableMenu.Item.Instance"/>.
		/// </summary>
		uint Instance = 0);

	/// <summary>A provider: given the source's argument, the rows it supplies.</summary>
	public delegate IReadOnlyList<Row> Provider(string argument);

	private static readonly Dictionary<string, Provider> Providers =
		new(System.StringComparer.OrdinalIgnoreCase)
		{
			["capture"] = CaptureRows,
			["spawn"] = SpawnRows,
			[Engine_UiAchievementPage.SourceName] = Engine_UiAchievementPage.Rows,

			// The title's own sources (the hero's inventory, its quests, its shop…) are added when
			// it installs (RegisterGame).

			// The shell's own screen: mods from the load order on disk, the rest from the live
			// RexNet node. No guest screen backs any of them.
			[Engine_UiNetworkSource.ModsSource] = Engine_UiNetworkSource.Mods,
			[Engine_UiNetworkSource.FriendsSource] = Engine_UiNetworkSource.Friends,
			[Engine_UiNetworkSource.RequestsSource] = Engine_UiNetworkSource.Requests,
			[Engine_UiNetworkSource.InvitesSource] = Engine_UiNetworkSource.Invites,

			// The options screen's Mods fold: one row per enabled mod that ships settings, each
			// opening that mod's host options page.
			[Refii.Mods.ModSettingsUi.SourceName] = Refii.Mods.ModSettingsUi.Rows,

			[Engine_UiNetworkSource.GamesSource] = Engine_UiNetworkSource.Games,
			[Engine_UiNetworkSource.AllSource] = Engine_UiNetworkSource.All,
			[Engine_UiNetworkSource.HostingSource] = Engine_UiNetworkSource.Hosting,

			// A mod's rows: those a script stated (set_menu_rows) and published values (Engine_UiModRows).
			[Engine_UiModRows.RowsSource] = Engine_UiModRows.Rows,
			[Engine_UiModRows.ValuesSource] = Engine_UiModRows.Values,
		};

	/// <summary>
	/// Rows of a STATED list (one read off the guest's own `g_Menu`) that unfold into a source's
	/// rows here, keyed by population script and row name: the guest's row is a leaf whose action
	/// the console's guide answered, and the host answers it in the book instead.
	///
	/// EMPTY, and that is not a gap. The logbook's `Achievements` row was the one entry; the
	/// achievements are their own screen now (<see cref="Engine_UiAchievementPage.ScreenId"/>),
	/// reached from their own pause tab, because there are far more of them than a fold inside
	/// a screen the guest also drives can carry. The mechanism stays for the next stated leaf
	/// whose answer the console owned.
	/// </summary>
	private static readonly Dictionary<(string Script, string Row), string> StatedFolders = new();

	/// <summary>The source a stated row unfolds from, or null when it stays a leaf.</summary>
	public static string? StatedFolderSource(string populationScript, string rowName) =>
		StatedFolders.TryGetValue((populationScript, rowName), out string? source) ? source : null;

	private static readonly HashSet<string> Unknown = new(System.StringComparer.OrdinalIgnoreCase);

	/// <summary>The names the game registers at start, which a script may not replace.</summary>
	private static readonly HashSet<string> GameSources = new(Providers.Keys, System.StringComparer.OrdinalIgnoreCase);

	/// <summary>Whether <paramref name="name"/> is one of the game's own sources.</summary>
	public static bool IsGameSource(string name)
	{
		Refii.Core.Engine_TitleGame.EnsureInstalled();
		return GameSources.Contains(name);
	}

	/// <summary>Adds or replaces a source; the name is the `source` field before the colon, the rest is the argument.</summary>
	public static void Register(string name, Provider provider) => Providers[name] = provider;

	/// <summary>A title's own source, installed with the title: one of the game's, which a script may not replace.</summary>
	public static void RegisterGame(string name, Provider provider)
	{
		Providers[name] = provider;
		GameSources.Add(name);
	}

	private static readonly Row[] None = System.Array.Empty<Row>();

	/// <summary>A script-registered source's rows, each <see cref="Row.FromMod"/> whatever the provider said.</summary>
	private static IReadOnlyList<Row> AsModRows(IReadOnlyList<Row> rows)
	{
		var marked = new List<Row>(rows.Count);
		foreach (Row row in rows)
		{
			marked.Add(row with { FromMod = true });
		}

		return marked;
	}

	/// <summary>Rows for one entry's source; <paramref name="entryName"/> is the default argument.</summary>
	public static IReadOnlyList<Row> Rows(string source, string entryName)
	{
		Refii.Core.Engine_TitleGame.EnsureInstalled();
		if (source.Length == 0)
		{
			return None;
		}

		int colon = source.IndexOf(':');
		string name = colon < 0 ? source : source[..colon];
		string argument = colon < 0 || colon + 1 >= source.Length
			? entryName
			: source[(colon + 1)..];

		if (Providers.TryGetValue(name, out Provider? provider))
		{
			IReadOnlyList<Row> rows = provider(argument);
			return GameSources.Contains(name) ? rows : AsModRows(rows);
		}

		// Once per unknown name; a silent empty category is indistinguishable from no stock.
		if (Unknown.Add(name))
		{
			Godot.GD.PushWarning($"[menu-source] '{name}' is not a registered source — " +
				$"'{source}' supplies nothing. Registered: {string.Join(", ", Providers.Keys)}");
		}

		return None;
	}

	/// <summary>
	/// The presentation the `capture` source describes, or 0 for none; set per sample by the
	/// owning drive. Only says whether a capture-sourced menu is live.
	/// </summary>
	public static uint StatedPresentation { get; set; }

	/// <summary>"No screen" — a caller that does not know which screen it is sourcing for, and
	/// the capture's own answer when the population belongs to no in-game screen (the front
	/// end). Deliberately not 0: 0 is the pause root, a real screen id.</summary>
	public const uint AnyScreen = 0xFFFFFFFFu;

	/// <summary>
	/// The screen the rows being sourced are FOR; set per sample by the owning drive and left
	/// at <see cref="AnyScreen"/> by callers that have no screen (the front end).
	///
	/// THIS IS THE KEY THE CAPTURE WAS MISSING. `capture:Root` asks for every row spawned under
	/// `Root`, and the capture is one list — so binding the real-estate screen that way put the
	/// front end's `New Game`, `Continue`, `Language` and `Subtitles` above its own `Sell` and
	/// `Adjust Prices`. The payload now records which screen the guest was populating
	/// (`manager+4`, read at the boundary that clears the list), and a bound screen takes only
	/// its own population.
	/// </summary>
	public static uint BoundScreen { get; set; } = AnyScreen;

	private static readonly HashSet<(uint Bound, uint Capture)> ReportedMismatch = new();

	/// <summary>
	/// Whether the published capture is the population <see cref="BoundScreen"/> asked for.
	///
	/// CONSERVATIVE ON PURPOSE: a caller with no screen takes anything, which is what the front
	/// end and every already-working `capture:&lt;own-name&gt;` binding rely on. Only a bound
	/// screen reading a population that states a DIFFERENT screen is refused.
	/// </summary>
	private static bool CaptureIsOurs()
	{
		if (BoundScreen == AnyScreen)
		{
			return true;
		}

		uint captured = Engine_UiSource.Current.MenuScreenId();
		if (captured == BoundScreen)
		{
			return true;
		}

		// Said once per pair: a screen that silently sources nothing is indistinguishable from
		// one whose list is genuinely empty, and that ambiguity cost a whole turn once already.
		if (ReportedMismatch.Add((BoundScreen, captured)))
		{
			Godot.GD.Print($"[menu-source] screen {BoundScreen} asked for the capture, which " +
				$"states screen {(captured == AnyScreen ? "none (front end)" : captured.ToString())}" +
				" — supplying nothing rather than another screen's rows.");
		}

		return false;
	}

	/// <summary>The title sources whose rows are the capture's under the parent their argument names last
	/// (after its last ':'), with something stamped on: their category's caption is the population's
	/// (<see cref="CapturesFor"/>).</summary>
	private static readonly HashSet<string> Capturing = new(System.StringComparer.OrdinalIgnoreCase);

	/// <summary>A title's own source over the capture (see <see cref="Capturing"/>).</summary>
	public static void RegisterGameCapturing(string name, Provider provider)
	{
		RegisterGame(name, provider);
		Capturing.Add(name);
	}

	/// <summary>Whether <paramref name="source"/>'s rows are the capture's under <paramref name="name"/>:
	/// `capture:&lt;name&gt;`, or a capturing source whose argument ends in that name.</summary>
	public static bool CapturesFor(string source, string name)
	{
		if (string.Equals(source, "capture:" + name, System.StringComparison.Ordinal))
		{
			return true;
		}

		int colon = source.IndexOf(':');
		return colon > 0 && Capturing.Contains(source[..colon]) &&
			string.Equals(source[(source.LastIndexOf(':') + 1)..], name, System.StringComparison.Ordinal);
	}

	/// <summary>
	/// The captured rows under <paramref name="parent"/> as the guest stated them — with the icon, the mark
	/// and the item each carries (RefiiHostGetMenuRowItem) — for a title source that stamps its own commands
	/// on them; empty when the capture is not the bound screen's.
	/// </summary>
	public static IReadOnlyList<Engine_UiModel.MenuRow> CapturedUnder(string parent)
	{
		if (!CaptureIsOurs())
		{
			return System.Array.Empty<Engine_UiModel.MenuRow>();
		}

		var under = new List<Engine_UiModel.MenuRow>();
		foreach (Engine_UiModel.MenuRow captured in SpawnedRows())
		{
			if (captured.Name.Length != 0 &&
				string.Equals(captured.Parent, parent, System.StringComparison.Ordinal))
			{
				under.Add(captured);
			}
		}

		return under;
	}

	/// <summary>`capture`: the children the guest states for <paramref name="parent"/> (raw capture, no hold).</summary>
	public static IReadOnlyList<Row> CaptureRows(string parent) =>
		CaptureIsOurs()
			? SpawnRows(parent, Engine_UiSource.Current.GetMenuRows(out _))
			: None;

	/// <summary>
	/// The caption the screen's own population spawned a top-level row <paramref name="name"/> with
	/// (its `String2`, decorations and all), or null when the capture is not this screen's or holds no
	/// such row.
	/// </summary>
	public static string? CapturedCaption(string name)
	{
		if (!CaptureIsOurs())
		{
			return null;
		}

		foreach (Engine_UiModel.MenuRow captured in SpawnedRows())
		{
			if (captured.IsRoot && captured.Label.Length != 0 &&
				string.Equals(captured.Name, name, System.StringComparison.Ordinal))
			{
				return captured.Label;
			}
		}

		return null;
	}

	/// <summary>The last population seen whole, held across the partial window.</summary>
	private static List<Engine_UiModel.MenuRow>? _wholeSpawn;

	/// <summary>
	/// `spawn`: children of <paramref name="parent"/> from the spawn capture. Every row reaches
	/// `sub_826CE8D0` with `String1` (name), `String2` (resolved label), `String4` (parent),
	/// `(EventType, id)` and rating; spawn order is preserved.
	/// </summary>
	private static IReadOnlyList<Row> SpawnRows(string parent)
	{
		if (!CaptureIsOurs())
		{
			return None;
		}

		List<Engine_UiModel.MenuRow> spawned = SpawnedRows();

		// `DeleteAllSpawnedMenuItems` clears the capture before every repopulation with no end
		// marker; the previous whole population is held so the partial window is never read as
		// an empty menu (population and consumption are synchronous in the original).
		return SpawnRows(parent, spawned);
	}

	/// <summary>The captured rows; the last whole population while one is in flight. The one accessor for capture consumers.</summary>
	public static List<Engine_UiModel.MenuRow> SpawnedRows()
	{
		List<Engine_UiModel.MenuRow> spawned = Engine_UiSource.Current.GetMenuRows(out _);
		if (Engine_UiSource.Current.MenuPopulating())
		{
			return _wholeSpawn ?? spawned;
		}

		if (spawned.Count != 0)
		{
			_wholeSpawn = spawned;
		}

		return spawned;
	}

	private static IReadOnlyList<Row> SpawnRows(
		string parent, List<Engine_UiModel.MenuRow> spawned)
	{
		var rows = new List<Row>();
		foreach (Engine_UiModel.MenuRow captured in spawned)
		{
			if (captured.Name.Length == 0 ||
				!string.Equals(captured.Parent, parent, System.StringComparison.Ordinal))
			{
				continue;
			}

			rows.Add(new Row(
				captured.Name,
				captured.Label.Length != 0 ? captured.Label : captured.Name,
				Icon: string.Empty,
				captured.EventType,
				captured.Id,
				captured.Rating,
				Committable: true));
		}

		return rows;
	}
}
