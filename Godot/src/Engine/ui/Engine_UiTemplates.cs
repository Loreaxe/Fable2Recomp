using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// One menu entry — a tab, a sub-tab, a command — as data: <see cref="Name"/> is the identity a
/// dispatch recognises, <see cref="Tag"/> localises the label, <see cref="Command"/> is what
/// activating it does, <see cref="Children"/> nest sub-entries in place and <see cref="Opens"/>
/// names another surface. The AUTHORING form a JSON file and the editor hold; it maps one-to-one
/// onto the runtime model <see cref="Engine_UiExpandableMenu.Item"/>.
/// </summary>
public sealed class Engine_UiMenuEntry
{
	public string Name = string.Empty;
	public string Tag = string.Empty;
	public string? Command;
	public string? Opens;
	public bool HostOnly;

	/// <summary>
	/// A row the GUEST spawns that we do not show. The population tables are the guest's own
	/// spawn lists, so an authored list is not free to simply omit a row — the row arrives from
	/// the guest's `g_Menu` statements whatever the template says. This is the statement that it
	/// goes: `Engine_UiScreenMenu` runs `RemoveChild` on it, the guest's own call, and the row
	/// and everything under it leave the list.
	///
	/// Use it for a row whose action belonged to the console rather than to the game — the
	/// logbook's `Achievements`, whose answer was `XamShowAchievementsUI`.
	/// </summary>
	public bool Drop;

	/// <summary>
	/// The row's icon texture, as `SpawnButton` takes it — a `.tex` path relative to the game's
	/// art root, or empty for a row that draws none.
	/// </summary>
	public string Icon = string.Empty;

	/// <summary>
	/// The ROW CONDITION this entry is built under (<see cref="Engine_UiRowConditions"/>): a name,
	/// <c>!name</c>, or several joined by <c>,</c> that must all hold. Empty always holds. Siblings
	/// may share a name when their conditions never hold together — the pause root states the hero's
	/// <c>Map</c> and player two's <c>Map</c> that way — so one record serves both and a mod patches
	/// it once.
	/// </summary>
	public string When = string.Empty;

	/// <summary>
	/// The event this entry pushes, as `g_Menu.EventConst` numbers it. `Submenu` (301) is the
	/// marker a category carries, and it is the DEFAULT for an entry with children — the guest's
	/// own category rows are spawned with exactly that.
	/// </summary>
	public int EventType;

	/// <summary>
	/// The star or pip rating — see <see cref="Engine_UiExpandableMenu.Item.RatingSlide"/>.
	/// Defaults to the empty slide, which is what a menu of commands wants.
	/// </summary>
	public int Rating = Engine_UiMenuConstants.EmptyRatingSlide;

	/// <summary>
	/// Where this entry's children come from at RUNTIME, or empty for a hand-authored list: the
	/// guest supplies the list, the template the shape (<see cref="Engine_UiMenuSources"/>).
	/// Authored children and a source compose — the authored ones spawn first, in order, then the
	/// source's.
	/// </summary>
	public string Source = string.Empty;

	/// <summary>
	/// Whether this category's caption carries `" (N)"` for how many rows it holds.
	///
	/// The guest has TWO header spawners and the difference is exactly this field.
	/// `sub_826CF098` takes a pointer to the category's count pair and appends
	/// `" (" .. count .. ")"` when the first is above zero (0x826CF0D8), plus a second marker when
	/// the second is; `sub_826CF020` takes no count at all. The clothing screen calls the first
	/// for its seven garment slots and the second for `Suits` — which is why the real screen
	/// reads `Headgear (6)` but plain `Outfits`.
	/// </summary>
	public bool Counted = true;

	/// <summary>
	/// MOD CONTENT: an entry a mod's or the player's file states that restates no game entry (see
	/// <see cref="Engine_UiTemplateSet.ApplyText"/>). Its row has no record of the guest's behind
	/// its `(EventType, id)`, so it is never stated as the guest's highlight
	/// (<see cref="Engine_UiExpandableMenu.Item.FromMod"/>). Set by composition, never read from
	/// or written to a file.
	/// </summary>
	public bool FromMod;

	/// <summary>
	/// The rows <see cref="Source"/> supplies were put on this list by mod content — the entry is
	/// a mod's, or a mod changed the game's source — so they are <see cref="FromMod"/> however
	/// real their ids are: an id is only meaningful to the screen whose handlers it was made for.
	/// </summary>
	public bool SourceFromMod;

	/// <summary>
	/// The event this entry is SPAWNED with: its own, else `Submenu` (301) for an entry with
	/// children or a source, else 0. A category is a submenu row; a source makes it one too,
	/// because its rows arrive after the build and a command row never folds however many rows
	/// land under it later.
	/// </summary>
	public int SpawnEventType => EventType != 0
		? EventType
		: Children.Count > 0 || Source.Length != 0
			? (int)Engine_UiMenuConstants.EventType.Submenu
			: 0;

	public List<Engine_UiMenuEntry> Children = new();
}

/// <summary>A named menu — its entries and which one starts highlighted.</summary>
public sealed class Engine_UiMenuTemplate
{
	public string Name = string.Empty;
	public string Highlight = string.Empty;

	/// <summary>
	/// `SetRootTitle` — the heading a screen shows above this list, as a Babel tag or literal. It
	/// lives on the root item, which is never drawn and outlives every repopulation.
	/// </summary>
	public string Title = string.Empty;

	/// <summary>
	/// Where the ROOT's rows come from at runtime, or empty for a hand-authored list — the same
	/// contract as <see cref="Engine_UiMenuEntry.Source"/> one level up. A host-owned list screen
	/// is a list, not a folder: its rows are headings, so a source hung on an entry would nest
	/// them one level too deep and make the whole screen a fold.
	///
	/// Authored entries and a source compose, the authored ones first.
	/// </summary>
	public string Source = string.Empty;

	/// <summary>The root's <see cref="Source"/> was stated by mod content — see <see cref="Engine_UiMenuEntry.SourceFromMod"/>.</summary>
	public bool SourceFromMod;

	public List<Engine_UiMenuEntry> Entries = new();

	/// <summary>
	/// A host action every SOURCED row on this menu performs, unless the row names its own.
	///
	/// Some screens do not answer a row by its identity at all. The furniture screens are the
	/// case: their rows carry event type 0, and the press handler at the screen's `vtable+20`
	/// (`sub_826B65F0`) has cases for 140 and 141 and falls straight out of everything else — an
	/// identity push there does nothing whatever. The commit runs from the INPUT handler on code
	/// 36 instead, which reads the highlighted row out of Lua
	/// (`sub_826C1B50` -&gt; `g_Menu:GetHighlightedMenuItemData()`) rather than off the event.
	///
	/// So the row's action is "press A on the screen", stated once for the whole menu.
	/// </summary>
	public string RowCommand = string.Empty;

	/// <summary>The authored entry with this name, at any depth, or null when none is authored.</summary>
	public Engine_UiMenuEntry? Entry(string name)
	{
		foreach (Engine_UiMenuEntry entry in Entries)
		{
			if (Entry(entry, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static Engine_UiMenuEntry? Entry(Engine_UiMenuEntry entry, string name)
	{
		if (string.Equals(entry.Name, name, System.StringComparison.Ordinal))
		{
			return entry;
		}

		foreach (Engine_UiMenuEntry child in entry.Children)
		{
			if (Entry(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>
	/// Re-syncs every SOURCED row against what its source says now. True when anything moved.
	/// Separate from `Build` because the shape is authored once at adoption while a source (the
	/// spawn capture above all) fills in later and keeps changing.
	///
	/// Authored children are never touched and the parent's fold survives: only rows carrying
	/// <see cref="Engine_UiExpandableMenu.Item.Sourced"/> are replaced, and only when the set
	/// differs — otherwise a steady source would rebuild its subtree every frame, cursor and all.
	/// </summary>
	public bool RefreshSources(Engine_FrontEndMenu menu)
	{
		bool changed = false;
		foreach (Engine_UiMenuEntry entry in Entries)
		{
			changed |= RefreshEntry(menu, entry, RowCommand);
		}

		if (Source.Length != 0)
		{
			changed |= RefreshUnder(
				menu, menu.Model.MenuRoot, Engine_UiExpandableMenu.RootName, Source, Name,
				RowCommand, SourceFromMod);
		}

		return changed;
	}

	private static bool RefreshEntry(
		Engine_FrontEndMenu menu, Engine_UiMenuEntry entry, string rowCommand)
	{
		bool changed = false;
		foreach (Engine_UiMenuEntry child in entry.Children)
		{
			changed |= RefreshEntry(menu, child, rowCommand);
		}

		if (entry.Source.Length == 0 || menu.Find(entry.Name) is not { } item)
		{
			return changed;
		}

		return changed |
			RefreshUnder(menu, item, entry.Name, entry.Source, entry.Name, rowCommand, entry.SourceFromMod);
	}

	/// <summary>
	/// Re-syncs the sourced rows directly under one parent — an entry or the root. Only rows
	/// carrying <see cref="Engine_UiExpandableMenu.Item.Sourced"/> are touched, so an authored
	/// sibling keeps its place and the parent's fold survives.
	/// </summary>
	private static bool RefreshUnder(
		Engine_FrontEndMenu menu,
		Engine_UiExpandableMenu.Item item,
		string parentName,
		string source,
		string argument,
		string rowCommand,
		bool placedByMod)
	{
		List<Engine_UiMenuSources.Row> rows =
			Expanded(Engine_UiMenuSources.Rows(source, argument), placedByMod);

		// `current` in the order `Expanded` states: each sourced child, and under a group
		// HEADER its own sourced rows — `SpawnSourced` hangs a grouped row under its header, so
		// counting direct children alone never matched and a grouped list respawned, fold and
		// cursor lost, on every sample.
		var direct = new List<Engine_UiExpandableMenu.Item>();
		var current = new List<Engine_UiExpandableMenu.Item>();
		foreach (Engine_UiExpandableMenu.Item child in item.Children)
		{
			if (!child.Sourced)
			{
				continue;
			}

			direct.Add(child);
			current.Add(child);
			if (child.EventType == (int)Engine_UiMenuConstants.EventType.Submenu)
			{
				foreach (Engine_UiExpandableMenu.Item grouped in child.Children)
				{
					if (grouped.Sourced)
					{
						current.Add(grouped);
					}
				}
			}
		}

		if (SameRows(current, rows))
		{
			// THE SAME ROWS, restated in place — re-emphasised (the world map's primary quest
			// moving after `SetAsPrimary`), relabelled (an ability's rank in its name, a shop
			// price), re-rated, re-counted. Written onto the standing items rather than respawned,
			// so the fold and the cursor survive and the view does not run its arrival cascade;
			// a respawn on every relabel snapped group folds shut and left the cursor on the
			// same index of a different list.
			bool restated = false;
			for (int i = 0; i < rows.Count; ++i)
			{
				Engine_UiMenuConstants.Emphasis wanted =
					rows[i].Emphasis != Engine_UiMenuConstants.Emphasis.None ? rows[i].Emphasis
					: rows[i].Emphasised ? Engine_UiMenuConstants.Emphasis.PrimaryQuest
					: Engine_UiMenuConstants.Emphasis.None;
				if (current[i].Emphasis != wanted)
				{
					// A CHANGE THE VIEW MUST DRAW: the weapon just chosen (a pending switch) and the
					// primary quest moving are emphasis alone, and unreported they waited for the
					// list to be rebuilt — on leaving the menu.
					current[i].Emphasis = wanted;
					restated = true;
				}

				// A row renamed in place (same id): the name is what a commit and the stated
				// highlight carry, so it follows the source.
				if (!string.Equals(current[i].Name, rows[i].Name, System.StringComparison.Ordinal))
				{
					current[i].Name = rows[i].Name;
					restated = true;
				}

				// Provenance, not drawn: carried so a remount that changes it reaches the statement.
				current[i].FromMod = rows[i].FromMod;

				// Not drawn either: the object the row stands for, which a split stack moves.
				current[i].Instance = rows[i].Instance;

				// The rest of what a row states can change under an unchanged name and label
				// (a viewed flag, a new ticket in its command); restated in place. Not `Ghosted`:
				// the guest's by-name ghosting writes that after the source has spoken.
				Engine_UiMenuSources.Row row = rows[i];
				if (row.IsHeader)
				{
					continue;
				}

				// The label against what the SPAWN would have set: a header is spawned with no
				// stated label (the view resolves its tag) and the bound list writes its
				// "Caption (N)" count onto it afterwards, so headers are not relabelled here.
				string? command = row.Command ?? (rowCommand.Length != 0 ? rowCommand : null);
				if (current[i].Unviewed != row.Unviewed ||
					current[i].Counted != row.Counted ||
					current[i].RatingSlide != row.Rating ||
					!string.Equals(current[i].Command, command, System.StringComparison.Ordinal) ||
					!string.Equals(current[i].StatedLabel, row.Label, System.StringComparison.Ordinal))
				{
					current[i].Unviewed = row.Unviewed;
					current[i].Counted = row.Counted;
					current[i].RatingSlide = row.Rating;
					current[i].Command = command;
					current[i].StatedLabel = row.Label;
					restated = true;
				}
			}

			return restated;
		}

		// A REAL CHANGE KEEPS THE FOLD: the last of a stack given away removes its row, and the
		// headers the player had open stay open across the respawn, by name.
		var open = new HashSet<string>(System.StringComparer.Ordinal);
		foreach (Engine_UiExpandableMenu.Item stale in direct)
		{
			if (stale.IsOpen && stale.Name.Length != 0)
			{
				open.Add(stale.Name);
			}

			item.Children.Remove(stale);
			menu.Model.Forget(stale);
		}

		SpawnSourced(menu, rows, parentName, rowCommand);
		foreach (Engine_UiExpandableMenu.Item spawned in item.Children)
		{
			if (spawned.Sourced && spawned.Children.Count > 0 && open.Contains(spawned.Name))
			{
				spawned.IsOpen = true;
			}
		}

		menu.Model.UpdateAfterSpawning();
		return true;
	}

	/// <summary>
	/// Spawns a source's rows under <paramref name="parentName"/>, creating a HEADER row for each
	/// distinct <see cref="Engine_UiMenuSources.Row.Group"/> as it is first seen.
	///
	/// A group header is a submenu row exactly like an authored category, so the fold, the
	/// ghosting and the cursor all behave the same — the only difference is that it exists
	/// because the data had rows for it rather than because a template listed it. Groups appear
	/// in first-seen order, which for the inventory is the order the guest's own list walks.
	/// </summary>
	internal static void SpawnSourced(
		Engine_FrontEndMenu menu,
		IReadOnlyList<Engine_UiMenuSources.Row> rows,
		string parentName,
		string rowCommand = "")
	{
		foreach (Engine_UiMenuSources.Row row in rows)
		{
			string under = row.Group.Length != 0 && !row.IsHeader ? row.Group : parentName;

			Engine_UiExpandableMenu.Item sourced = menu.Model.SpawnButton(
				row.Name, row.Label, row.Icon, row.EventType,
				idLow: (int)(row.Id & 0xFFFF), idHigh: (int)((row.Id >> 16) & 0xFFFF),
				row.Rating, under);
			sourced.StatedLabel = row.IsHeader ? null : row.Label;
			sourced.Committable = row.Committable;
			sourced.Sourced = true;
			sourced.HostOnly = row.HostOnly;
			sourced.FromMod = row.FromMod;
			sourced.Ghosted = row.Ghosted;
			sourced.Emphasis = row.Emphasis != Engine_UiMenuConstants.Emphasis.None
				? row.Emphasis
				: row.Emphasised ? Engine_UiMenuConstants.Emphasis.PrimaryQuest : Engine_UiMenuConstants.Emphasis.None;
			sourced.Counted = row.Counted;
			sourced.Unviewed = row.Unviewed;
			sourced.Instance = row.Instance;
			// The row's own action wins; the menu's stands in for a screen whose rows all commit
			// the same way — see `Engine_UiMenuTemplate.RowCommand`. Never on a HEADER: a header
			// folds, it does not activate.
			sourced.Command = row.Command ??
				(!row.IsHeader && rowCommand.Length != 0 ? rowCommand : null);
		}
	}

	/// <summary>
	/// A source's rows with a HEADER inserted before the first row of each group, in first-seen
	/// order. One sequence, used both to spawn and to decide whether anything changed — if the
	/// two disagreed the list would rebuild every frame, because what is in the model after a
	/// spawn is this and not the source's rows.
	///
	/// A header is a submenu row with the group's Babel tag and no stated label, so the view
	/// resolves the tag exactly as it does for an authored category.
	/// </summary>
	/// <param name="placedByMod">The rows were put on this list by mod content
	/// (<see cref="Engine_UiMenuEntry.SourceFromMod"/>): every row and header is
	/// <see cref="Engine_UiMenuSources.Row.FromMod"/>.</param>
	private static List<Engine_UiMenuSources.Row> Expanded(
		IReadOnlyList<Engine_UiMenuSources.Row> rows, bool placedByMod)
	{
		// IN THE ORDER THE MODEL HOLDS THEM after `SpawnSourced`, which is the order `RefreshUnder`
		// walks: a header, then every row of its group wherever in the source those rows fell. The
		// inventory is in pickup order (push_back in `CECInventory_AddOneOfRecord`, 0x824F09AC), so a
		// group's rows are rarely adjacent — compared in source order the gift list respawned, its
		// fold shut, on every sample.
		var members = new Dictionary<string, List<Engine_UiMenuSources.Row>>(System.StringComparer.Ordinal);
		var order = new List<(Engine_UiMenuSources.Row Row, List<Engine_UiMenuSources.Row>? Members)>(rows.Count);

		foreach (Engine_UiMenuSources.Row source in rows)
		{
			Engine_UiMenuSources.Row row = placedByMod && !source.FromMod ? source with { FromMod = true } : source;
			if (row.Group.Length != 0 && !members.ContainsKey(row.Group))
			{
				var group = new List<Engine_UiMenuSources.Row>();
				members[row.Group] = group;
				order.Add((new Engine_UiMenuSources.Row(
					Name: row.Group,
					Label: row.GroupTag.Length != 0 ? row.GroupTag : row.Group,
					Icon: string.Empty,
					EventType: (int)Engine_UiMenuConstants.EventType.Submenu,
					Id: 0u,
					Rating: Engine_UiMenuConstants.EmptyRatingSlide,

					// A header carries no identity of the guest's and performs nothing itself.
					Committable: false,
					Ghosted: false,
					Command: null,
					Group: row.Group,
					GroupTag: row.GroupTag,
					IsHeader: true,
					FromMod: row.FromMod), group));
			}

			// A grouped row hangs under its header (SpawnSourced); anything else stands in place.
			if (row.Group.Length != 0 && !row.IsHeader)
			{
				members[row.Group].Add(row);
			}
			else
			{
				order.Add((row, null));
			}
		}

		var expanded = new List<Engine_UiMenuSources.Row>(rows.Count + members.Count);
		foreach ((Engine_UiMenuSources.Row row, List<Engine_UiMenuSources.Row>? group) in order)
		{
			expanded.Add(row);
			if (group is not null)
			{
				expanded.AddRange(group);
			}
		}

		return expanded;
	}

	/// <summary>The same rows in the same order, by NAME: everything else a row states is restated in place.</summary>
	/// <summary>
	/// Whether the source states the SAME ROWS the list stands on, in order: each by its name, or
	/// by its id where the name alone moved. A row the guest renames in place is still that row —
	/// the shop names one it sold out `SOLD` (`sub_826B0B90`, 0x826b0ca4) under its own ticket —
	/// and treating the rename as a new row set respawned the whole group on every sale, which
	/// ran the view's arrival and left the list a row off its cursor until the next move.
	/// </summary>
	private static bool SameRows(
		List<Engine_UiExpandableMenu.Item> current,
		IReadOnlyList<Engine_UiMenuSources.Row> rows)
	{
		if (current.Count != rows.Count)
		{
			return false;
		}

		for (int i = 0; i < rows.Count; ++i)
		{
			if (string.Equals(current[i].Name, rows[i].Name, System.StringComparison.Ordinal))
			{
				continue;
			}

			if (rows[i].Id == 0u || current[i].EventType != rows[i].EventType ||
				!Engine_UiScreenMenu.RowId(current[i], out uint id) || id != rows[i].Id)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Builds the runtime model this template describes, through `SpawnButton` — the shipped
	/// construction the guest's own C++ makes for every row, taking the icon, the event, the id
	/// pair and the rating together.
	/// </summary>
	public Engine_FrontEndMenu Build(IReadOnlyDictionary<string, bool>? facts = null)
	{
		var menu = new Engine_FrontEndMenu();
		BuildInto(menu, committable: false, facts);
		return menu;
	}

	/// <summary>
	/// The same construction into an EXISTING model — for a list whose view is already bound to
	/// it, which cannot be handed a new object.
	/// </summary>
	/// <param name="committable">
	/// Whether these rows carry a guest identity. FALSE for a hand-authored list, where
	/// `(EventType, 0)` would resolve row zero and the row must dispatch by name or command.
	/// TRUE for a POPULATION table, where the name and the event type are transcribed from the
	/// guest's own spawn script and `(name, type, 0)` is exactly what its `SelectMenuItem`
	/// pushes — so the row commits by identity like any row the guest spawned itself.
	/// </param>
	/// <param name="facts">The row conditions the BUILDER knows (the pause list's <c>player-two</c>),
	/// asked before the registered ones — see <see cref="Engine_UiMenuEntry.When"/>.</param>
	public void BuildInto(Engine_FrontEndMenu menu, bool committable, IReadOnlyDictionary<string, bool>? facts = null)
	{
		menu.Clear();
		foreach (Engine_UiMenuEntry entry in Entries)
		{
			Spawn(entry, Engine_UiExpandableMenu.RootName);
		}

		// The ROOT's own source, after the authored entries so a fixed row keeps the top.
		SpawnSourced(menu, Expanded(Engine_UiMenuSources.Rows(Source, Name), SourceFromMod),
			Engine_UiExpandableMenu.RootName, RowCommand);

		// `UpdateAfterSpawning` — the population is complete, so the visible list and the
		// automatic ghosting are recomputed once rather than per entry.
		menu.Model.UpdateAfterSpawning();
		menu.Model.UpdateAllGhosting();

		// `SetRootTitle` where the record names one; the root is never drawn itself, but the
		// title is what a screen shows above the list.
		if (Title.Length != 0)
		{
			menu.Model.SetRootTitle(Title);
		}

		if (Highlight.Length != 0)
		{
			menu.Highlight(Highlight);
		}

		void Spawn(Engine_UiMenuEntry entry, string parent)
		{
			if (!Engine_UiRowConditions.Holds(entry.When, facts))
			{
				return;
			}

			// A category is a submenu row (301), which is what makes `SelectMenuItem` treat the
			// press as a fold — see Engine_UiMenuEntry.SpawnEventType.
			Engine_UiExpandableMenu.Item item = menu.Model.SpawnButton(
				entry.Name, entry.Tag, entry.Icon, entry.SpawnEventType,
				idLow: 0, idHigh: 0, entry.Rating, parent);

			// Host routing is not menu content, so it rides beside the spawn.
			item.Command = entry.Command;
			item.Opens = entry.Opens;
			item.HostOnly = entry.HostOnly;
			item.FromMod = entry.FromMod;
			Engine_UiScreenActions.RefreshSettingLabel(item);

			// See the `committable` parameter: a hand-authored row has no guest identity, a
			// population-table row is the guest's own.
			item.Committable = committable;

			foreach (Engine_UiMenuEntry child in entry.Children)
			{
				Spawn(child, entry.Name);
			}

			// Then whatever the source supplies, after the authored children so a fixed row keeps
			// its place at the top. A source resolving to nothing leaves the entry as authored.
			// ONE SPAWN PATH FOR EVERY SOURCE, so a row that declares a GROUP gets its header
			// wherever it was sourced from, and the identity, ghosting and command channels
			// cannot drift between the three call sites — they already had, this one carrying a
			// `Command` the other two dropped.
			SpawnSourced(menu, Expanded(Engine_UiMenuSources.Rows(entry.Source, entry.Name), entry.SourceFromMod),
				entry.Name, RowCommand);
		}
	}
}

/// <summary>
/// Everything Godot knows about one in-game screen, as one record: whether it owns a list, its
/// panel fields, where the preview hangs, which chrome units an owned book clones. A shipped
/// screen is a record, a modded screen the same record from a file, a new menu a record the
/// editor writes; the runtime that consumes them is written once.
/// </summary>
public sealed class Engine_UiScreenTemplate
{
	public uint ScreenId;
	public string Name = string.Empty;

	/// <summary>Whether Godot owns this screen's ExpandableMenu list.</summary>
	public bool OwnsList;

	/// <summary>
	/// The screen's handler writes the purse — `Foreground.ExpandableMenu.Money.Text`, through
	/// the guest's purse writer (Fable 2 `sub_8246B5E8`) — and that told text stands even under a
	/// list Godot owns.
	/// </summary>
	public bool Purse;

	/// <summary>
	/// The book's chrome stands on its FILE's rest state (Engine_UiRestState) and the host's
	/// channels, never on the guest's presentation sample: every component on its rest slide, the
	/// draw bit the host's (all on, then the provider's switches), the authored pose. Set per screen
	/// once the `rest:` check (debug channel `elements`) shows the screen's rest state and its live
	/// sample agree but for what the host drives.
	/// </summary>
	public bool RestFromFile;

	/// <summary>
	/// THE GUI GLINTS the book draws (Engine_UiParticles): the title's opacity channel, null for
	/// none. The glint elements must stand in the book's units (<see cref="ChromeUnits"/>).
	/// </summary>
	public Engine_UiParticles.OpacityChannel? Glints;

	/// <summary>
	/// Where the guest's open leaves an element that its rest slide does not: path → the six
	/// transform slots (position x/y/z, scale x/y/z), null keeping the slid value. Measured with
	/// the `rest:` check; only read while <see cref="RestFromFile"/> stands the book.
	/// </summary>
	public Dictionary<string, float?[]> RestPose = new(System.StringComparer.Ordinal);

	/// <summary>The stated opacity (0..100) the guest's open leaves an element at, for a book stood by <see cref="RestFromFile"/>.</summary>
	public Dictionary<string, float> RestOpacity = new(System.StringComparer.Ordinal);

	/// <summary>
	/// The screen's handler chooses the row from the HIGHLIGHTED ROW, never from a pushed event:
	/// options (`sub_826DA228` asks `sub_826C1B50` @0x826da3ec and takes the page as its type − 70)
	/// and decoration (`sub_826B7550`). (The logbook's A reaches no C++ —
	/// `sub_826D96F0` 36 fires `OnPressA` only, which no Lua receives now — and its page follows
	/// the stated highlighted row.) A press on such a screen states the row as highlighted
	/// (`StateHighlightedRow`, which `sub_826C1B50` answers from) and then sends the screen's own
	/// A (code 36). An identity push at these handlers answers only the scroll sounds.
	/// </summary>
	public bool CommitsByHighlight;

	/// <summary>The token <see cref="RowAction"/> replaces with the pressed row's full id.</summary>
	public const string RowIdToken = "{id}";

	/// <summary>
	/// The verb a press on this screen's rows runs (`rowAction`), <see cref="RowIdToken"/> replaced by
	/// the row's full id (`IdLow + (IdHigh &lt;&lt; 16)`, as the guest's populate spawned it): the
	/// screen's stated call by argument, never its handler reading the guest's highlight. A row's
	/// own command wins. Upgrade furniture (26) states `Furniture:upgrade:{id}`: its rows are levels
	/// (`sub_8299D910`), and the verb runs `sub_826B6A18`'s transaction on that level.
	/// </summary>
	public string RowAction = string.Empty;

	/// <summary>
	/// The menu template this screen's list is built from when the WIDGET NAMES NONE.
	///
	/// Most screens state a `MenuPopulationScript` and the list is looked up by that name. The
	/// gift family does not: `sub_82430468` builds screen 15's rows in native C++ and the only
	/// Lua it issues is `g_Menu:DeleteAllSpawnedMenuItems()` and `g_Menu:SpawnButton()`, so
	/// neither the attribute nor the `RepopulateMenuFromPopulationScript` fallback has anything
	/// to offer and the list dropped onto the raw model. This is the third way to say it, and it
	/// is a template field so a mod can rebind a screen without a rebuild.
	/// </summary>
	public string MenuScript = string.Empty;

	/// <summary>
	/// The menu template per SCREEN MODE, for a screen that is really several lists behind one
	/// id. Keyed by the guest's own mode value; <see cref="MenuScript"/> stands in when the mode
	/// is unknown or unlisted.
	///
	/// Screen 15 is the case and the only one: `sub_829B0A58` reads `screen+372` and spawns the
	/// item headers on 1, the `Melee`/`Ranged` pair on 3, and the shared row builder then keeps
	/// only the bucket that matches. Binding the id to one script would put the item categories
	/// on the weapons list.
	/// </summary>
	public Dictionary<int, string> MenuScriptByMode = new();

	/// <summary>
	/// How far the owned row view stands in FRONT of the mirrored tree, in Z — per screen,
	/// because the books are not z-ordered alike. The pause menu puts its `LeftFrame` cover
	/// INSIDE `ExpandableMenu` at +10 with `Buttons` at 0, so its rows must sit on the pool's own
	/// plane or the tabs draw in front of the cover; other screens carry page art in front of the
	/// pool, where a view on the pool's plane disappears behind it. Default
	/// `Engine_UiPresentationDepth.Step / 4` clears ordinary page art; pause overrides it to 0.
	/// </summary>
	public float ListLift = Engine_UiPresentationDepth.Step / 4f;

	/// <summary>
	/// Info-panel fields, dotted paths in the screen's document. Read, written and validated, and
	/// DRAWN BY NOTHING — see <see cref="Engine_UiScreenPanel"/>; a book's texts are its provider's,
	/// the registry's and the told writers'.
	/// </summary>
	public List<Engine_UiScreenPanel.Field> PanelFields = new();

	/// <summary>Roots whose every text descendant the panel would own. Drawn by nothing, as <see cref="PanelFields"/>.</summary>
	public List<string> PanelSubtrees = new();

	/// <summary>Where the 3D preview hangs, or empty when the screen has none.</summary>
	public string PreviewAnchor = string.Empty;

	/// <summary>
	/// The element whose live slide says a second input LEVEL is up (the options category
	/// page), with the slide value that means "open". Empty when the screen has one level.
	/// </summary>
	public string PageElement = string.Empty;

	public int PageSlide;

	/// <summary>
	/// The `.bgf` this screen is drawn from, without extension — the guest's own mapping,
	/// `std::map<int,string>` at `manager+0xA0`. Not every owned surface is the current screen:
	/// the spell wheel (id 2) is opened by its own flag and simply appears in the presentation
	/// registry, so keying the owned build on the live presentation reaches those. Three ids
	/// share `GiftGivingScreen` (15, 25, 27), so a stated id wins and this is the fallback.
	/// </summary>
	public string Presentation = string.Empty;

	/// <summary>Chrome units an OWNED build clones from the pattern book, authored spots kept.</summary>
	public List<string> ChromeUnits = new();

	/// <summary>
	/// Which chrome unit is the PAGE STACK — the parent anything page-local hangs under (map
	/// tokens, quest markers). Empty when the screen has no page-local content.
	/// </summary>
	public string PageRootUnit = string.Empty;

	/// <summary>
	/// THE RIGHT-HAND PAGES THIS SCREEN HAS, as element paths — the set one of which is shown
	/// and the rest switched off.
	///
	/// A RECORD, NOT A CONSTANT, because the books do not agree and a mod's book agrees with
	/// none of them. The shipped ones alone run Page1+Page2, Page1+Page2+Page3 (shop),
	/// Page1+Page2+Page4 (abilities — there is no Page3) and Page1..Page4 (options).
	/// Omitting a page is not neutral: one nobody switches off keeps drawing over whichever is
	/// up.
	/// </summary>
	public List<string> Pages = new();

	/// <summary>
	/// Where the page stack sits when a page is up, or null to leave it where the file put it.
	///
	/// THE BOOKS DISAGREE, and it cost a whole debugging pass: `Foreground.Book.Pages` is
	/// authored at x = 0 in `giftgivingscreen` and `equipmentscreen` — already on the leaf — but
	/// at **x = 400** in `abilitiesscreen`, `shopscreen` and `worldmapscreen`, which is off a
	/// 960-wide canvas whose right edge is 480. Identical, correct page-selection code therefore
	/// showed a page on the items screen and an empty leaf on the abilities screen.
	///
	/// Godot owns the layout, so this is stated per screen rather than recovered from the file's
	/// own slides — and a modder's screen states its own.
	/// </summary>
	public Vector3? PageStackAt;

	/// <summary>
	/// A full-canvas backdrop tint to stand behind the book, by art name. Empty for none. Cloned
	/// OVERSIZED: the shipped quad is exactly 1280x720 and leaves the revealed edges untinted at
	/// any other aspect.
	/// </summary>
	public string BackdropUnit = string.Empty;

	/// <summary>
	/// For a modal BOX drawn by its own view rather than as a book: the `.bgf` it is cloned
	/// from. Separate from <see cref="Presentation"/>, which names a SCREEN's live file.
	/// </summary>
	public string BoxPresentation = string.Empty;

	/// <summary>The art units a box's view clones, by ROLE — the position is the role, stated
	/// on each built-in record.</summary>
	public List<string> BoxUnits = new();

	/// <summary>
	/// Element paths inside a box's art that its view reads or writes (texts, button rows,
	/// prompts), keyed by the built-in path. See <see cref="Engine_UiTemplates.BoxPath"/>.
	/// </summary>
	public Dictionary<string, string> BoxPaths = new(System.StringComparer.Ordinal);

	/// <summary>
	/// THE WIDE LAYOUT — what the screen does on a window wider than 16:9, as element paths.
	/// The books are authored to the 16:9 edges; wider than that the pieces in
	/// <see cref="WideLeft"/> ride out to the left edge and those in <see cref="WideRight"/> to
	/// the right (by <see cref="Engine_UiSceneBuilder.WideShift"/>), so the covers stay on the
	/// screen edges and the backdrop shows between the pages. A piece in neither list stays
	/// where it is — a centred card states nothing. The owned row list rides with
	/// <see cref="WideLeft"/> when that list is not empty, and a page's clip window follows
	/// its page.
	/// </summary>
	public List<string> WideLeft = new();

	/// <summary>See <see cref="WideLeft"/>.</summary>
	public List<string> WideRight = new();

	/// <summary>
	/// Page PAPER that widens instead of riding: it keeps its gutter edge and reaches the
	/// same distance further out, for a screen with no backdrop to show between the pages
	/// (the co-op screen), so the paper still meets in the middle under frames that rode out.
	/// </summary>
	public List<string> WideStretch = new();

	/// <summary>
	/// Chrome UNITS that are the screen's ground rather than its furniture (the world map's
	/// tiles): scaled about the canvas centre with the window's width, so the picture keeps
	/// its 16:9 framing and still reaches both edges.
	/// </summary>
	public List<string> WideGround = new();

	/// <summary>
	/// THE TEXT PAGE'S EXTENT, when it ends short of the 16:9 edge: the x of its inner and outer
	/// limits in the presentation's canvas units, 16:9 layout. A single-line field anchored
	/// between them is kept between them (<see cref="Engine_UiOwnedBook"/>, "text stays on its
	/// page": drawn smaller about its anchor, down to the floor), where the default keeps a line
	/// inside the covers at the 16:9 edges only. Null: the page reaches the cover.
	/// </summary>
	public Vector2? PageSpan;

	/// <summary>
	/// Elements INSIDE a cloned unit that a Godot-owned view already draws, by element name — not
	/// the same statement as leaving a unit out of <see cref="ChromeUnits"/>, since these are
	/// nested (`Book` carries the page stack, which carries `loadgame`). The subtree is dropped
	/// at bind time, root and all; the guest keeps its own copies live.
	///
	/// An entry may be a dotted tail, and for `Buttons` it must be: that name has six bearers in
	/// `abilitiesscreen.bgf`, so `ExpandableMenu.Buttons` names as much of the path as it takes
	/// to be unambiguous. A bare name matches every bearer.
	/// </summary>
	public List<string> OwnedElsewhere = new();

	/// <summary>
	/// Units whose TEXT the host takes from the guest's own live writes, as data. The owned
	/// books keep the guest out of the text path on purpose (a field the guest stopped
	/// writing went quietly stale); a unit named here is one the guest fills natively, once,
	/// at its screen's show — the shop's front page — and the host has nothing better than
	/// what the guest computed. A host value on a field under the unit still wins.
	/// </summary>
	public List<string> GuestTextUnits = new();

	/// <summary>The static menu this screen offers, or empty when the guest supplies rows.</summary>
	public string Menu = string.Empty;

	/// <summary>
	/// The guest stat ids this screen's page shows, each with the element path it fills.
	///
	/// PURE DATA, and it was a 93-entry array inside the page class. It is the screen's own
	/// content — which stat appears where — so it belongs with the screen, where the editor can
	/// see it and a new row is a record rather than a recompile.
	/// </summary>
	public List<(int Id, string Path)> StatFields = new();

	/// <summary>
	/// Whether this screen takes input directly once revealed, rather than through the row list.
	/// The one-off panels do — they are reached only through the GameAction chain, which is dead
	/// under Godot's cursor.
	/// </summary>
	public bool OwnsRevealedInput;

	/// <summary>The action the accept button runs while revealed — see
	/// <see cref="Engine_UiScreenActions"/>. Empty for a screen with none.</summary>
	public string RevealedAccept = string.Empty;

	/// <summary>The action the back button runs while revealed.</summary>
	public string RevealedBack = string.Empty;

	/// <summary>
	/// The action the flip button (Y) runs on this screen (`flipAction`), a mod's hook. The
	/// shipped abilities screen has none: its Y is the unlearn by type, GameUi.
	/// </summary>
	public string FlipAction = string.Empty;

	/// <summary>
	/// The action X runs on this screen (`xAction`): the guest's code 38 — the villager card's
	/// rename (`sub_826CCBA8`: `cmpwi r4, 0x26` → `sub_826CDCD8`), its authored prompt "Set Nickname X".
	/// </summary>
	public string XAction = string.Empty;

	/// <summary>Whether this screen carries the spell-assignment blade.</summary>
	public bool HasSpellBlade;

	/// <summary>
	/// The screen's SCROLL REGION, as `sub_828C3250` registers one: a content group whose rows
	/// the right stick slides, the mask element whose band is the window, and the scrollbar
	/// element whose arrows and thumb say where the view stands. Empty means no region.
	/// The guest's own scroll (`sub_829ED788`) moves each registered row's Y inside the mask's
	/// clip; the owned book moves the group, which is the same transform.
	/// </summary>
	public string ScrollContent = string.Empty;

	public string ScrollMask = string.Empty;

	public string ScrollBar = string.Empty;

	/// <summary>
	/// The measured-text REFLOW, as `sub_829AB8A8` runs one: the element whose wrapped height is
	/// measured, the authored base marking where the block below starts, and every element that
	/// moves by the overflow delta. The guest never clips or shortens the measured text — the
	/// rows below it move down.
	/// </summary>
	public string FlowMeasured = string.Empty;

	public string FlowBase = string.Empty;

	public List<string> FlowMoved = new();

	/// <summary>
	/// The reflow runs only while the measured element draws — the pause root moves
	/// `Legend.Fixed` under the objective inside its quest branch, and leaves it where it stands
	/// when there is no quest (0x826a2018..0x826a20b0 vs the else at 0x826a2168).
	/// </summary>
	public bool FlowWhenDrawn;

	/// <summary>
	/// The reflow's tail — `0x829abcdc`: the rows here stand at the anchor row's Y, less 30
	/// while the anchor itself draws (Mystery Island's gargoyle line over the silver keys).
	/// </summary>
	public string FlowTailAnchor = string.Empty;

	public List<string> FlowTailMoved = new();

	/// <summary>
	/// Whether the row list fades its TOP end. Per-screen and not Lua: `FadeOutTopItemEarly`
	/// (`expandablemenuformatting.dis main.0`) is run as `DoString` source from the screen
	/// contract vtables, and options (18, `0x826D9D40`) is the one screen that asks for the top
	/// end only — so real estate is the one that does NOT fade its top.
	/// </summary>
	public bool FadesTopEnd = true;

	/// <summary>
	/// Whether the row list fades its BOTTOM end. Real estate (28, `0x82998300`) asks for the
	/// bottom end only, so options is the one that does NOT fade its bottom.
	/// </summary>
	public bool FadesBottomEnd = true;

	/// <summary>
	/// The detail-page provider this screen uses, by name — see
	/// <see cref="Engine_UiPageProviders"/>. Empty means the screen has no host-filled page and
	/// every field stays on the guest's own channel.
	///
	/// THIS IS WHAT MAKES A SCREEN DATA. It used to be six switch statements on the screen id in
	/// `Engine_UiDetailPages`, one per question asked of a page, so a screen's behaviour was
	/// spread across six places and each screen could break its own way. Naming the provider here
	/// puts every screen on one path.
	/// </summary>
	public string Provider = string.Empty;

	/// <summary>
	/// THE DATA PROVIDER'S PAGE (<see cref="Engine_UiPageProviders.Data"/>): element path to what
	/// the field says — a literal, a Babel tag, <c>mod:&lt;key&gt;</c>, <c>player:&lt;key&gt;</c> or
	/// <c>row:label</c>/<c>row:name</c> (<see cref="Engine_UiValueSource"/>); <c>{row}</c> in a
	/// binding is the highlighted row's name. How a mod fills a screen of its own without C#.
	/// </summary>
	public Dictionary<string, string> Fields = new(System.StringComparer.Ordinal);

	/// <summary>
	/// The data provider's switches: element path to a binding that resolves on (<c>true</c>, a
	/// non-zero value) or off — see <see cref="Fields"/>.
	/// </summary>
	public Dictionary<string, string> Switches = new(System.StringComparer.Ordinal);

	/// <summary>A member-wise copy; <see cref="Engine_UiTemplateJson.Clone"/> deepens it.</summary>
	public Engine_UiScreenTemplate ShallowCopy() => (Engine_UiScreenTemplate)MemberwiseClone();
}

/// <summary>
/// How a MODAL answers — the input codes an owned box sends back through the guest's router, one
/// row per box id.
///
/// <see cref="Confirm"/> is A and <see cref="Cancel"/> is B; a null Cancel means B does nothing,
/// which the shop's purchased box genuinely wants (it answers 36 to continue and 75 to EQUIP,
/// and the X action stays unmasked and reaches the guest directly).
/// </summary>
public sealed class Engine_UiModalTemplate
{
	/// <summary>The guest's modal id, at `guiOwner+44`.</summary>
	public uint ModalId;

	/// <summary>
	/// The screen underneath, when the answer depends on it — `uint.MaxValue` for "any". The same
	/// box answers differently per screen: query id 0 dismisses on 36 at the shop and 46 at real
	/// estate, and photo mode's copy has no dismiss at all, so the id alone is not the key.
	/// </summary>
	public uint ScreenId = uint.MaxValue;

	public uint Confirm;

	public uint? Cancel;

	/// <summary>
	/// A query's third answer, on X — the shop's "Equip and continue" (query 20, `XAChoices`): its
	/// answer `sub_826AF888` buys on 36 and buys then equips on 75 (`cmpwi r31, 0x4B` at
	/// 0x826AF8EC, `lhGuiInventory_ActivateItemByCategory` at 0x826AF94C; disc `sub_82593DA8`).
	/// Null when X answers nothing.
	/// </summary>
	public uint? Alternate;

	/// <summary>
	/// True for a MessageBox (one dismiss code) rather than a QueryBox (confirm and cancel).
	/// Explicit, not inferred from `Cancel == null`: "B does nothing" and "this box has only a
	/// dismiss" are different statements.
	/// </summary>
	public bool Dismissal;
}

/// <summary>
/// The template registry: the guest's population tables, then the GAME'S OWN FILE
/// (<see cref="Engine_UiGameTemplates"/>, the records below generated into the format a mod's file
/// takes), then every enabled mod's <c>ui/templates.json</c> in load order, then the player's
/// <c>user://ui/templates.json</c> — each composed by the same <see cref="Engine_UiTemplateSet"/> — and
/// snapshotted to <c>user://ui/templates.snapshot.json</c> so the editor and modders start from what
/// actually runs.
///
/// The authored records are the decoded contracts as records — entry names the dispatch strcmps on,
/// event params, tab ids — so an override renaming an entry's LABEL keeps working and one renaming
/// its NAME stops dispatching, exactly as the guest behaves.
/// </summary>
public static class Engine_UiTemplates
{
	public const string OverridePath = "user://ui/templates.json";
	public const string SnapshotPath = "user://ui/templates.snapshot.json";

	/// <summary>Where a mod ships its templates, inside its package.</summary>
	public const string ModTemplatesPath = "ui/templates.json";

	/// <summary>
	/// Two mods stating the same template piece: the higher-priority one won, the other is named.
	/// For the mod tooling's conflict view.
	/// </summary>
	public readonly record struct ModConflict(string Piece, string Winner, string Loser);

	/// <summary>The composition that runs: the game's file, the enabled mods', the player's.</summary>
	private static Engine_UiTemplateSet? _set;

	private static Engine_UiTemplateSet Set
	{
		get
		{
			EnsureLoaded();
			return _set!;
		}
	}

	/// <summary>Every screen record, for a caller that works across the whole set — the component
	/// exporter and the editor's listing.</summary>
	public static IEnumerable<Engine_UiScreenTemplate> Screens => Set.Screens.Values;

	/// <summary>Every menu record by name, for the Studio's listings and the validator.</summary>
	public static IEnumerable<string> MenuNames => Set.Menus.Keys;

	/// <summary>Every modal row as it runs, by key — the editor's and the Studio's listing.</summary>
	public static IEnumerable<KeyValuePair<string, Engine_UiModalTemplate>> ModalRows => Set.Modals.Rows;

	/// <summary>The row actions a mod states for a screen list's menu template (<c>"rowActions"</c>); empty for none.</summary>
	public static IReadOnlyList<Engine_UiRowActions.RowAction> RowActionsFor(string menu) => Set.RowActions.For(menu);

	/// <summary>
	/// The answer for a box, or null when it is not owned. A screen-specific row wins over the
	/// any-screen one, so id 0 can be per-screen while every other id states itself once.
	/// </summary>
	public static Engine_UiModalTemplate? Modal(uint modalId, uint screenId, bool dismissal) =>
		Set.Modals.Find(modalId, screenId, dismissal);

	/// <summary>
	/// The mod that stated a menu row (<c>menu:&lt;menu&gt;/&lt;row&gt;</c>, a patch), else the mod that
	/// stated the whole menu (<c>menu:&lt;menu&gt;</c>); empty for a shipped row or the player's own.
	/// </summary>
	public static string MenuOwner(string menu, string row)
	{
		Dictionary<string, string> owners = Set.Owners;
		if (row.Length != 0 && owners.TryGetValue($"menu:{menu}/{row}", out string? rowOwner))
		{
			return rowOwner;
		}

		return owners.TryGetValue("menu:" + menu, out string? owner) ? owner : string.Empty;
	}

	/// <summary>One screen's record, or null for a screen Godot has no template for.</summary>
	public static Engine_UiScreenTemplate? Screen(uint screenId) =>
		Set.Screens.TryGetValue(screenId, out Engine_UiScreenTemplate? template) ? template : null;
	/// <summary>
	/// The screen drawn from a given `.bgf`; null when no template names it, and null when
	/// several GUEST screens do — an ambiguous file cannot say which screen is up.
	///
	/// A HOST-ONLY record never makes a guest file ambiguous. This is asked of a LIVE
	/// presentation — "the guest has this document up, whose screen is it" — and a host-only
	/// surface is one Godot stands for itself out of the file-only pattern book; it is never
	/// what the guest put in the registry. The achievements screen borrows `logbookscreen`, and
	/// without this rule adding it would have made the logbook itself answer "ambiguous" and go
	/// bookless on any frame the guest did not state its id.
	///
	/// So: a single guest-range record wins outright; a host-only one answers only when no guest
	/// screen names the file at all, which is how the front end's three (1010-1012) resolve.
	/// </summary>
	public static Engine_UiScreenTemplate? ScreenFor(string presentation)
	{
		Engine_UiScreenTemplate? guest = null;
		Engine_UiScreenTemplate? owned = null;
		bool guestAmbiguous = false;
		bool ownedAmbiguous = false;

		foreach (Engine_UiScreenTemplate template in Set.Screens.Values)
		{
			if (!string.Equals(template.Presentation, presentation,
					System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (template.ScreenId >= Engine_UiBookSet.HostOnlyScreenId)
			{
				ownedAmbiguous |= owned is not null;
				owned = template;
			}
			else
			{
				guestAmbiguous |= guest is not null;
				guest = template;
			}
		}

		if (guest is not null)
		{
			return guestAmbiguous ? null : guest;
		}

		return ownedAmbiguous ? null : owned;
	}

	/// <summary>
	/// One row a menu template drops, by BOTH the identities a live list can carry.
	///
	/// <see cref="Name"/> is the guest's spawn identity and is what the STATED build has. The
	/// position solve usually has no name at all — the logbook joined 0 of 5 rows against the
	/// spawn capture in a real run, leaving every row called `Row{n}` — and there the only
	/// identity is the DISPLAYED TEXT. <see cref="Tag"/> is the Babel tag the population table
	/// gives the row, which resolves to exactly that text in whatever language is installed, so
	/// matching on it is a join rather than a guess at an English string.
	/// </summary>
	public readonly record struct DroppedRow(string Name, string Tag);

	/// <summary>A named menu template, or null.</summary>
	public static Engine_UiMenuTemplate? Menu(string name) =>
		Set.Menus.TryGetValue(name, out Engine_UiMenuTemplate? menu) ? menu : null;

	/// <summary>
	/// Row names a menu template says the guest may spawn but we do not show — see
	/// <see cref="Engine_UiMenuEntry.Drop"/>. Empty for a template that drops nothing, and empty
	/// for a name no template carries.
	///
	/// Keyed by the MENU's name, which for a guest list is its `MenuPopulationScript`: every
	/// population table is registered as a template under that name, so any list the guest builds
	/// is addressable here whether or not Godot authored it.
	/// </summary>
	public static IReadOnlyList<DroppedRow> DroppedRows(string menuName)
	{
		Engine_UiTemplateSet set = Set;
		if (_dropped!.TryGetValue(menuName, out List<DroppedRow>? cached))
		{
			return cached;
		}

		var rows = new List<DroppedRow>();
		if (set.Menus.TryGetValue(menuName, out Engine_UiMenuTemplate? menu))
		{
			Collect(menu.Entries);
		}

		_dropped[menuName] = rows;
		return rows;

		void Collect(List<Engine_UiMenuEntry> entries)
		{
			foreach (Engine_UiMenuEntry entry in entries)
			{
				if (entry.Drop && (entry.Name.Length != 0 || entry.Tag.Length != 0))
				{
					rows.Add(new DroppedRow(entry.Name, entry.Tag));
				}

				Collect(entry.Children);
			}
		}
	}

	/// <summary>Memoised <see cref="DroppedRows"/>, cleared whenever the templates reload.</summary>
	private static Dictionary<string, List<DroppedRow>>? _dropped;

	private static void EnsureLoaded()
	{
		if (_set is not null)
		{
			return;
		}

		var set = new Engine_UiTemplateSet
		{
			Warn = warning => GD.PushWarning("[ui-templates] " + warning),
			Log = line => GD.Print("[ui-templates] " + line),
		};

		// THE GAME IS THE FIRST FILE. The title's own records (its population tables, then its
		// templates file through the same reader every mod's file takes), then the mods in load
		// order, then the player's own file.
		Refii.Core.Engine_TitleGame.Active.ApplyUiTemplates(set);
		int modded = 0;
		foreach ((string mod, string text) in _composing ?? Refii.Mods.ModEntries.ReadEnabledText(ModTemplatesPath))
		{
			modded += set.ApplyText(text, mod, $"{mod}:{ModTemplatesPath}");
		}

		int overridden = _composing is null ? ApplyOverrides(set, OverridePath) : 0;
		_dropped = new Dictionary<string, List<DroppedRow>>(System.StringComparer.Ordinal);
		_set = set;
		if (_composing is null)
		{
			SaveSnapshot(SnapshotPath);
		}

		GD.Print($"[ui-templates] {set.Screens.Count} screen(s), {set.Menus.Count} menu(s)" +
			(modded == 0 ? string.Empty : $"; {modded} piece(s) from mods") +
			(overridden == 0 ? string.Empty : $"; {overridden} record(s) overridden"));
	}

	/// <summary>
	/// The mods' files a <see cref="Compose"/> stands in for the enabled ones; null for the
	/// registry as it runs.
	/// </summary>
	private static IReadOnlyList<(string Mod, string Text)>? _composing;

	/// <summary>
	/// The registry as it composes with <paramref name="modFiles"/> (each mod's
	/// <see cref="ModTemplatesPath"/> text, LOWEST priority first) in place of the enabled mods'
	/// files, and without the player's file: the Mod Studio's view of a mod's templates as it is
	/// being edited, enabled or not. The records come back in the snapshot's shape (menus,
	/// screens, modals) with the conflicts that composition recorded, through the same
	/// <see cref="Engine_UiTemplateSet.ApplyText"/> every run composes with. No snapshot is
	/// written, and the running registry is rebuilt from the enabled mods on its next use.
	/// </summary>
	public static (System.Text.Json.Nodes.JsonObject Records, IReadOnlyList<ModConflict> Conflicts) Compose(
		IReadOnlyList<(string Mod, string Text)> modFiles)
	{
		_composing = modFiles;
		try
		{
			_set = null;
			_dropped = null;
			Engine_UiTemplateSet set = Set;
			return (set.Snapshot(), new List<ModConflict>(set.Conflicts));
		}
		finally
		{
			_composing = null;
			_set = null;
			_dropped = null;
		}
	}

	/// <summary>
	/// A menu record in the file shape (<c>{ highlight, title, source, rowCommand, entries }</c>) as
	/// the reader takes it, for an editor's preview of a record it has not written yet.
	/// </summary>
	public static Engine_UiMenuTemplate? ParseMenuRecord(string name, System.Text.Json.Nodes.JsonObject record) =>
		Engine_UiTemplateJson.ParseMenu(name, record);

	/// <summary>
	/// Drops every record and builds them again — the game's, mods, then the player's own file.
	/// For the editor, after a mod's templates change.
	/// </summary>
	public static void Reload()
	{
		_set = null;
		_dropped = null;
		EnsureLoaded();
	}

	/// <summary>
	/// The game's records as the reader composes them from the active title's own
	/// (<see cref="Refii.Core.Engine_TitleGame.ApplyUiTemplates"/>), with no mod and no player
	/// file. Pure: what a test holds against the title's authored form.
	/// </summary>
	public static Engine_UiTemplateSet ComposeGame()
	{
		var set = new Engine_UiTemplateSet();
		Refii.Core.Engine_TitleGame.Active.ApplyUiTemplates(set);
		return set;
	}

	/// <summary>Host-only template ids for the modal boxes (the guest's box ids are modal
	/// codes, not screens).</summary>
	public const uint MessageBoxId = 1020u;
	public const uint QueryBoxId = 1021u;
	public const uint NumericBoxId = 1022u;
	public const uint MenuBoxId = 1023u;

	/// <summary>The `.bgf` a box is drawn from, off its record.</summary>
	public static string BoxPresentation(uint id, string fallback) =>
		Screen(id) is { BoxPresentation.Length: > 0 } box ? box.BoxPresentation : fallback;

	/// <summary>
	/// An element path inside a box's art. The built-in path is the KEY; the record's
	/// `boxPaths` maps it to the path in the art actually loaded, and an unmapped key is itself.
	/// </summary>
	public static string BoxPath(uint id, string path) =>
		Screen(id) is { } box && box.BoxPaths.TryGetValue(path, out string? mapped) &&
		mapped.Length != 0
			? mapped
			: path;

	/// <summary>A box's art unit by ROLE — its position in the record's `boxUnits` list.</summary>
	public static string BoxUnit(uint id, int role, string fallback) =>
		Screen(id) is { } box && role < box.BoxUnits.Count && box.BoxUnits[role].Length != 0
			? box.BoxUnits[role]
			: fallback;

	/// <summary>How far the owned row list of a screen rides left on a wide window.</summary>
	public static float WideListShift(uint screenId) =>
		Screen(screenId) is { WideLeft.Count: > 0 } ? Engine_UiSceneBuilder.WideShift : 0f;

	// -----------------------------------------------------------------------------------
	// Files — the player's own and the snapshot. Every record is read and written by
	// Engine_UiTemplateJson, composed by Engine_UiTemplateSet.
	// -----------------------------------------------------------------------------------

	/// <summary>The player's own file, applied after every mod.</summary>
	private static int ApplyOverrides(Engine_UiTemplateSet set, string path)
	{
		if (!FileAccess.FileExists(path))
		{
			return 0;
		}

		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		return file is null ? 0 : set.ApplyText(file.GetAsText(), owner: string.Empty, path);
	}

	/// <summary>Writes every record as it runs — the editor's starting point and the override
	/// file's documentation.</summary>
	/// <summary>
	/// Writes the composed templates for authors to read. A side product of loading: a failure here
	/// is reported and the templates stand — it once threw from the HUD build and took the whole UI
	/// down with it.
	/// </summary>
	private static void SaveSnapshot(string path)
	{
		try
		{
			string text = _set!.SnapshotText();
			DirAccess.MakeDirRecursiveAbsolute("user://ui");
			using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			file?.StoreString(text);
		}
		catch (System.Exception exception)
		{
			GD.PushError($"[ui-templates] the snapshot was not written ({path}): {exception.Message}");
		}
	}
}
