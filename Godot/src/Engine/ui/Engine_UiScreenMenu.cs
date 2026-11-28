using Godot;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The list on an in-game screen — abilities, items, weapons, clothing, logbook, options, crime,
/// shop, world map, gift, selection, decoration — read out of the guest and handed to the view the
/// front end and pause menu draw with. One model for all eleven: they share the
/// <c>ExpandableMenu/Buttons</c> pool, the <c>expandablemenuformatting.lua</c> slot table and the
/// <c>sub_826D58F0</c>/<c>sub_826D5AF0</c> commit pair (contract in
/// <see cref="Engine_ExpandableMenuItems"/>).
///
/// The guest owns the row CONTENTS and the HIGHLIGHT: a commit reads Lua's
/// <c>g_Menu.CurrentHighlightIdx</c>, never a C++ index (<c>sub_826C1B50</c> runs
/// <c>"g_Menu:GetHighlightedMenuItemData()"</c>; screen 27's A press is gated on it not being the
/// <c>0x0FFFFFFF</c> "nothing selected" sentinel). A move is made on the guest's own stick router
/// (<c>sub_82404ED0</c> / <c>sub_82405030</c>) so the shipped repeat cadence comes with it. The
/// host owns LAYOUT and ART: the row components are hidden and
/// <see cref="Engine_FrontEndMenuView"/> draws over them.
///
/// The highlight is solved, not assumed: <c>onAddChild</c> writes
/// <c>VisibleIndex = HIGHLIGHT_INDEX + DisplayIndex - CurrentHighlightIdx</c> and the formatter
/// turns that into the slot's authored y, so any laid-out row solves back for the highlight. Rows
/// that disagree mean a sample taken mid-slide — keep the previous answer.
/// </summary>
public sealed class Engine_UiScreenMenu
{
	/// <summary>The model, ready for <see cref="Engine_FrontEndMenuView.Bind"/>.</summary>
	public Engine_FrontEndMenu Menu { get; } = new();

	/// <summary>The shipped model, ported — see <see cref="Engine_UiExpandableMenu"/>. The same
	/// object the view draws, never a second one beside it.</summary>
	public Engine_UiExpandableMenu Script => Menu.Model;

	/// <summary>Whether the C++'s `SetInitiallySelectedItem` has placed this list's cursor.</summary>
	private bool _initiallySelectedApplied;

	/// <summary>How many rows the list shows.</summary>
	public int Count => Menu.Visible.Count;

	private readonly List<string> _labels = new();
	private readonly List<int> _ordinals = new();

	/// <summary>Each row's stated indent, kept for the report — see <see cref="Describe"/>.</summary>
	private readonly List<float?> _indents = new();

	/// <summary>
	/// The `MenuPopulationScript` the screen's widget names — see
	/// <see cref="Engine_ExpandableMenuItems.PopulationScriptOf"/>. Empty when it names none.
	/// </summary>
	public string PopulationScript { get; private set; } = string.Empty;

	/// <summary>
	/// Which in-game screen this list belongs to, set by the caller from the screen manager's own
	/// presentation-to-id map. 0 when it is not one of the manager's screens.
	///
	/// Only used to find a template for a widget that names no population script — see
	/// <see cref="Engine_UiScreenTemplate.MenuScript"/>.
	/// </summary>
	public uint ScreenId { get; set; }

	/// <summary>
	/// <see cref="ScreenId"/> as a capture key: 0 means "not one of the manager's screens" here,
	/// so it becomes <see cref="Engine_UiMenuSources.AnyScreen"/> rather than the pause root,
	/// which really is screen 0. A list with no screen takes the capture as it always did.
	/// </summary>
	private uint CaptureKey => ScreenId != 0 ? ScreenId : Engine_UiMenuSources.AnyScreen;

	/// <summary>The presentation this list's Lua lives in — one `lua_State` per `.bgf`.</summary>
	public uint Presentation { get; private set; }

	/// <summary>True while the list is built from the guest's STATED `g_Menu` model.</summary>
	public bool Stated => _stated;

	/// <summary>
	/// The menu model built from the C++'s own statements — rows, tree, ghosting and emphasis from
	/// the 28 `g_Menu:` statements. When it carries rows for this presentation it wins and the Lua
	/// VM is not read; null is legal and leaves the list to its template.
	/// </summary>
	public Engine_UiMenuModel? Model { get; set; }

	/// <summary>
	/// The string table, for rows the guest is not currently drawing: a folded row has no pool
	/// button, so `ComponentIdx` is -1 and the tree carries only a Babel tag
	/// (`INV_ITEM_SILVER_KEY_NAME`). Pool text wins where it exists — it carries the decorations
	/// the population appended.
	/// </summary>
	public Refii.Text.Engine_BabelDatabase? Strings;

	/// <summary>
	/// Our fold, keyed by PATH — a name is only unique among its siblings. The guest's own
	/// `SelectMenuItem` is unreachable: every host Lua chunk goes through `sub_82426470`, which
	/// defers rather than runs once `guiObject+0x64` is cleared (the GUI constructor sets it, the
	/// tail of the frame update clears it and nothing sets it back).
	/// </summary>
	private readonly System.Collections.Generic.HashSet<string> _open =
		new(System.StringComparer.Ordinal);

	/// <summary>Bumped whenever the fold changes, so the signature test rebuilds.</summary>
	private int _foldGeneration;

	private bool _stated;

	private bool _saidModel;

	/// <summary>What the last stated read produced, so an unchanged model costs no rebuild.</summary>
	private readonly List<string> _signature = new();

	/// <summary>
	/// How many rows the guest said were showing when the model was last built from it. The
	/// trigger is a change in this, not a disagreement with our own visible count.
	/// </summary>
	private int _statedVisible = -1;

	/// <summary>
	/// Whether a template's root source answers anything for THIS screen — the same read
	/// `BuildInto` makes, bound to the same screen, without building. See the dry-template
	/// note in <see cref="Rebuild"/>.
	/// </summary>
	private bool SourceHasRows(Engine_UiMenuTemplate template)
	{
		uint boundWas = Engine_UiMenuSources.BoundScreen;
		Engine_UiMenuSources.BoundScreen = CaptureKey;
		try
		{
			return Engine_UiMenuSources.Rows(template.Source, template.Name).Count != 0;
		}
		finally
		{
			Engine_UiMenuSources.BoundScreen = boundWas;
		}
	}

	/// <summary>
	/// Builds the tree from the C++'s own statements. True when anything changed.
	/// </summary>
	/// <remarks>
	/// The parent is STATED: each row carries `String4`, the parent's name, so nothing depends on
	/// display order or on the list being laid out yet. Rows can arrive before their parents
	/// (spawn order is population order) and `Add` fixes the parent at construction, so rows go in
	/// waves until nothing new resolves; whatever still names an unspawned parent goes to the root.
	///
	/// Ghosting is the two-term rule via <see cref="Engine_UiMenuModel.EffectivelyGhosted"/> — the
	/// explicit flag OR an empty submenu while automatic submenu ghosting is on. The pool ordinal
	/// is left −1: it exists only for the position solve, and this path owns the cursor.
	/// </remarks>
	/// <summary>
	/// Builds the list from its POPULATION TEMPLATE and joins the guest's live state onto it.
	/// True when anything changed.
	/// </summary>
	/// <remarks>
	/// The row set, its order and its nesting are the template's; nothing here reads a live
	/// position or a pool button to decide WHAT the list contains. What the guest still supplies
	/// is what only it knows: the decorated label (`Abilities &lt;s4&gt;(2)&lt;/s4&gt;` when
	/// there are points to spend, `&lt;exclamation.img&gt;` when something inside is new) and the
	/// ghosting. Both are joined by <see cref="Engine_UiMenuModel.ApplyStatedLabels"/>, which is
	/// the same authored-list-against-the-guest's-pool join the pause menu has always used —
	/// name first, then position, and an empty read treated as no news rather than no rows.
	///
	/// A row here IS committable: see <see cref="Engine_UiMenuTemplate.BuildInto"/>.
	/// </remarks>
	/// <summary>
	/// Re-reads the bound template's sources NOW, in place (fold and cursor kept): what the guest's
	/// own handler does after a purchase or a use (`sub_829A5568` restates every ability row and
	/// the category counts after `sub_8246C970`), which our pump's direct call never runs. The
	/// sampled rebuild runs only when the guest's hidden presentation changes, and a host action
	/// changes none of it. Called on the bridge's data generation (a level, experience, an item,
	/// gold, a drawn weapon changed).
	/// </summary>
	public bool Resource()
	{
		if (_authoredFrom is not { } template)
		{
			return false;
		}

		return RebuildFromTemplate(template);
	}

	private bool RebuildFromTemplate(Engine_UiMenuTemplate template)
	{
		// WHOSE POPULATION THE CAPTURE HOLDS. `capture:Root` asks for every row spawned under
		// `Root` and the capture is one list, so without this a screen took whatever was
		// populated last — the front end's rows once came up on the real-estate screen. Set for
		// the whole build so every source this template resolves is answered for THIS screen.
		uint boundWas = Engine_UiMenuSources.BoundScreen;
		Engine_UiMenuSources.BoundScreen = CaptureKey;
		try
		{
			return BuildBound(template);
		}
		finally
		{
			Engine_UiMenuSources.BoundScreen = boundWas;
		}
	}

	/// <summary>
	/// Every non-root row named in <paramref name="stated"/> takes the stated label; true when one
	/// changed. Roots are <see cref="Engine_UiMenuModel.ApplyStatedLabels"/>'s.
	/// </summary>
	/// <remarks>
	/// A row takes the label stated for its own name AND id where one is; a name stated once is
	/// that row whatever id our row carries (a template row need not carry the guest's). A name
	/// stated for several rows, none with this row's id, says nothing about it: the shop's Buy and
	/// Sell rows for one item share a name, and the Sell row was taking the Buy row's caption.
	/// </remarks>
	public static bool JoinStatedLabels(IEnumerable<Engine_UiExpandableMenu.Item> rows,
		IReadOnlyList<Engine_UiMenuModel.Row> stated)
	{
		Dictionary<string, (string Label, int Count)>? byName = null;
		Dictionary<(string Name, uint Id), string>? byRow = null;
		foreach (Engine_UiMenuModel.Row row in stated)
		{
			if (row.IsRoot || row.Name.Length == 0 || row.Label.Length == 0)
			{
				continue;
			}

			byName ??= new Dictionary<string, (string, int)>(System.StringComparer.Ordinal);
			byName[row.Name] = (row.Label, byName.TryGetValue(row.Name, out (string Label, int Count) known) ? known.Count + 1 : 1);
			(byRow ??= new Dictionary<(string, uint), string>())[(row.Name, row.Id)] = row.Label;
		}

		if (byName is null || byRow is null)
		{
			return false;
		}

		bool changed = false;
		foreach (Engine_UiExpandableMenu.Item item in rows)
		{
			if (item.Parent is null || item.Name.Length == 0 ||
				!byName.TryGetValue(item.Name, out (string Label, int Count) named))
			{
				continue;
			}

			string? label =
				RowId(item, out uint id) && byRow.TryGetValue((item.Name, id), out string? own) ? own
				: named.Count == 1 ? named.Label
				: null;
			if (label is not null && !string.Equals(item.StatedLabel, label, System.StringComparison.Ordinal))
			{
				item.StatedLabel = label;
				changed = true;
			}
		}

		return changed;
	}

	/// <summary>A row's full id, as the guest packs it (`Float1` low, `Float2` high); false for a row with none.</summary>
	public static bool RowId(Engine_UiExpandableMenu.Item item, out uint id)
	{
		if (item.IdLow < 0)
		{
			id = 0u;
			return false;
		}

		id = ((uint)item.IdHigh << 16) | ((uint)item.IdLow & 0xFFFFu);
		return true;
	}

	/// <summary>The row the guest spoke of as selected: the one with that id under either name it has had.</summary>
	private Engine_UiExpandableMenu.Item? FindSelected(Engine_UiMenuModel.SelectedRow selected)
	{
		foreach (Engine_UiExpandableMenu.Item item in Script.SpawnedItems)
		{
			if (RowId(item, out uint id) && selected.Names(item.Name, id))
			{
				return item;
			}
		}

		return null;
	}

	private bool BuildBound(Engine_UiMenuTemplate template)
	{
		// Built once and then only re-joined: the row set cannot change, so rebuilding it every
		// sample would throw the fold and the cursor away for nothing.
		bool built = false;
		if (_authoredFrom != template)
		{
			// THE FOLD AND THE CURSOR SURVIVE A CHANGE OF SOURCE. A list first built from the
			// g_Menu model (its id not yet known) is rebuilt from the template the moment the
			// id lands; the rows are the same rows under the same names, so what the player
			// had open and highlighted is carried across by name — the weapons book was
			// snapping shut on that rebuild.
			var open = new HashSet<string>(System.StringComparer.Ordinal);
			foreach (Engine_UiExpandableMenu.Item was in Script.SpawnedItems)
			{
				if (was.IsOpen && was.Children.Count > 0 && was.Name.Length != 0)
				{
					open.Add(was.Name);
				}
			}

			string? highlighted = Script.Highlighted?.Name;

			_authoredFrom = template;
			template.BuildInto(Menu, committable: true);
			PruneAuthoredDrops();
			Script.UpdateAfterSpawning();
			Script.UpdateAllGhosting();

			if (open.Count != 0)
			{
				foreach (Engine_UiExpandableMenu.Item now in Script.SpawnedItems)
				{
					if (now.Children.Count > 0 && open.Contains(now.Name))
					{
						now.IsOpen = true;
					}
				}

				Script.UpdateAfterSpawning();
			}

			if (!string.IsNullOrEmpty(highlighted))
			{
				IReadOnlyList<Engine_UiExpandableMenu.Item> visible = Script.Visible;
				for (int i = 0; i < visible.Count; ++i)
				{
					if (string.Equals(visible[i].Name, highlighted, System.StringComparison.Ordinal))
					{
						Script.Highlight(i + 1);
						break;
					}
				}
			}

			built = true;
		}
		else if (template.RefreshSources(Menu))
		{
			// A sourced row set that has changed — the guest's data arriving, or changing under
			// us. Authored rows are untouched, so the fold and the cursor survive.
			PruneAuthoredDrops();
			Script.UpdateAfterSpawning();
			Script.UpdateAllGhosting();
			built = true;
		}

		// The guest's own decorated labels and ghosting over our rows, from the spawn capture.
		IReadOnlyList<Engine_UiMenuModel.Row> stated =
			Model?.For(Presentation)?.Rows ?? NoStatedRows;

		// BY NAME ONLY. The positional fallback exists for the pause list, whose rows match the
		// guest's one for one; a template list need not. The logbook drops the guest's dead
		// `Achievements` row, so ours is four against the guest's five — and the fallback then
		// handed our fourth row (Help) the guest's fourth label, putting "Achievements" back on
		// screen with Help's identity underneath it. A template row carries a real name and its
		// own Babel tag, so an unmatched row has a correct label already.
		bool moved = Engine_UiMenuModel.ApplyStatedLabels(Menu, stated, clearUnnamed: false);

		// AND BELOW THE ROOT, by name: `SpawnButton`'s String2 (and `RefreshButtonText`'s) is the
		// label with the guest's decorations at every depth — an unviewed item's "!", an
		// ability's level. The counted category headers are recomputed below and win.
		moved |= JoinStatedLabels(Script.SpawnedItems, stated);

		// THE CATEGORY COUNTS ARE OURS NOW. `sub_826CF098` decorates a category header with
		// `" (" .. count .. ")"` when the count is non-zero, and the guest's own labels carried
		// that — `Headgear (6)`. Those labels reach us only through the spawn capture, and the
		// capture files its statements under the presentation the CHUNK carried, which is not
		// always the screen's: the equipment screen's rows were nowhere in the model, so the
		// name join found nothing and the headers read as bare `Hats`.
		//
		// A list built from the data areas does not need to be told: the count IS the number of
		// children we just sourced. So the decoration is applied here, by the guest's rule, and
		// the join is left to supply only what we genuinely cannot compute.
		foreach (Engine_UiExpandableMenu.Item row in Script.SpawnedItems)
		{
			if (row.EventType != (int)Engine_UiMenuConstants.EventType.Submenu ||
				row.TextTag is not { Length: > 0 } tag || Strings is null)
			{
				continue;
			}

			// A category the template says carries no count of ours — the clothing screen's
			// `Outfits`, whose header the guest spawns through the countless `sub_826CF020`; the world
			// map's five, whose guest header carries its own (Fable2UiTemplates.WorldMapCategory). A
			// category whose rows are its own population's takes that population's caption for it.
			if (template.Entry(row.Name) is { Counted: false } uncounted)
			{
				if (Engine_UiMenuSources.CapturesFor(uncounted.Source, row.Name) &&
					Engine_UiMenuSources.CapturedCaption(row.Name) is { Length: > 0 } guest &&
					!string.Equals(row.StatedLabel, guest, System.StringComparison.Ordinal))
				{
					row.StatedLabel = guest;
					moved = true;
				}

				continue;
			}

			// `Counted`, not merely `Sourced`: the guest's number comes from a separate array
			// that walks the INVENTORY, so a row standing for no inventory record — the clothing
			// screen's `None` — is not in it. Counting those put every equipment header one
			// above the guest's own, which is how the discrepancy was finally pinned.
			int sourcedChildren = 0;
			bool anyUnviewed = false;
			foreach (Engine_UiExpandableMenu.Item child in row.Children)
			{
				if (!child.Sourced || !child.Counted)
				{
					continue;
				}

				++sourcedChildren;
				anyUnviewed |= child.Unviewed;
			}

			if (!Strings.TryGetString(tag, out string caption) || caption.Length == 0)
			{
				continue;
			}

			// A count of zero is the BARE caption (`sub_829A52F8`: `if (a2 <= 0)` copies the
			// caption alone), written rather than skipped: skipping left the spawn-time label up,
			// so buying the last upgradeable ability kept "Will (1)" on the folder.
			// The second suffix, `sub_826CF098`'s `word_820AEFF0` — appended when the count
			// pair's `[1]` is above zero, and that is the number of unviewed items.
			string decorated = sourcedChildren == 0
				? caption
				: $"{caption} ({sourcedChildren})" +
					(anyUnviewed ? "<exclamation.img>" : string.Empty);
			if (!string.Equals(row.StatedLabel, decorated, System.StringComparison.Ordinal))
			{
				row.StatedLabel = decorated;
				moved = true;
			}
		}

		// THE GUEST'S EXPLICIT GHOSTING, by name — its only statement that a row is unavailable
		// right now (the childhood restrictions on the pause tabs, an empty category, a shop
		// item you cannot afford). Applied to `Ghosted`, never to `AutoGhosted`: the two are
		// separate channels and a recompute owns the second.
		if (Model?.For(Presentation)?.Ghosting is { Count: > 0 } ghosting)
		{
			foreach ((string named, bool ghost) in ghosting)
			{
				if (Menu.Find(named) is { } row && row.Ghosted != ghost)
				{
					row.Ghosted = ghost;
					moved = true;
				}
			}
		}

		// AND WHAT THE GUEST SAID ABOUT THE ROW THE PLAYER IS ON — `SetSelectedItemEmphasis`,
		// `RefreshSelectedButtonText`, `RenameSelectedButton`, `UpdateAllGhosting` — by the name
		// Godot stated as highlighted when the guest said it.
		if (Model?.For(Presentation) is { } statedMenu && JoinSelectedStatements(statedMenu))
		{
			moved = true;
		}

		if (!built && !moved)
		{
			return false;
		}

		_labels.Clear();
		_ordinals.Clear();
		_indents.Clear();
		foreach (Engine_UiExpandableMenu.Item shown in Script.Visible)
		{
			_labels.Add(shown.StatedLabel ?? shown.Name);
			_ordinals.Add(shown.Ordinal);
			_indents.Add(null);
		}

		Script.Highlight(System.Math.Clamp(Script.CurrentHighlightIdx, 1,
			System.Math.Max(1, Script.Visible.Count)));
		return true;
	}

	private static readonly List<Engine_UiMenuModel.Row> NoStatedRows = new();

	/// <summary>The model's <see cref="Engine_UiMenuModel.Menu.GhostRefreshes"/> this list last
	/// re-derived its ghosting for.</summary>
	private int _ghostRefreshesSeen;

	/// <summary>
	/// The by-name statements the model holds for rows a template built: emphasis, caption and
	/// rename, then the ghost refresh. Flags are keyed by the name the row had when the guest
	/// spoke, so they land before the rename. True when anything moved.
	/// </summary>
	private bool JoinSelectedStatements(Engine_UiMenuModel.Menu stated)
	{
		bool moved = false;
		foreach ((string named, Engine_UiMenuConstants.Emphasis emphasis) in stated.Emphases)
		{
			if (Menu.Find(named) is { } row && row.Emphasis != emphasis)
			{
				row.Emphasis = emphasis;
				moved = true;
			}
		}

		foreach ((string named, string label) in stated.Labels)
		{
			if (Menu.Find(named) is { } row &&
				!string.Equals(row.StatedLabel, label, System.StringComparison.Ordinal))
			{
				row.StatedLabel = label;
				row.TextTag = label;
				moved = true;
			}
		}

		foreach ((string named, string renamed) in stated.Renames)
		{
			if (Menu.Find(named) is { } row)
			{
				row.Name = renamed;
				moved = true;
			}
		}

		// The same four, for the rows the runner identified by name AND id.
		foreach (Engine_UiMenuModel.SelectedRow selected in stated.Selected)
		{
			if (FindSelected(selected) is not { } row)
			{
				continue;
			}

			if (selected.Ghosted is { } ghost && row.Ghosted != ghost)
			{
				row.Ghosted = ghost;
				moved = true;
			}

			if (selected.Emphasis is { } emphasis && row.Emphasis != emphasis)
			{
				row.Emphasis = emphasis;
				moved = true;
			}

			if (selected.Label is { } label &&
				!string.Equals(row.StatedLabel, label, System.StringComparison.Ordinal))
			{
				row.StatedLabel = label;
				row.TextTag = label;
				moved = true;
			}

			if (!string.Equals(row.Name, selected.Name, System.StringComparison.Ordinal))
			{
				row.Name = selected.Name;
				moved = true;
			}
		}

		if (stated.GhostRefreshes != _ghostRefreshesSeen)
		{
			_ghostRefreshesSeen = stated.GhostRefreshes;
			Script.UpdateAllGhosting();
			moved = true;
		}

		return moved;
	}

	/// <summary>
	/// `g_Menu.UpDownInputDisabled` as the guest last stated it for this list's presentation
	/// (`SetExpandableMenuDisabled`) — the list's up/down lock.
	/// </summary>
	public bool StatedInputLock =>
		Model is { } model && model.Menus.TryGetValue(Presentation, out Engine_UiMenuModel.Menu? menu) &&
		menu.InputDisabled;

	/// <summary>
	/// Puts the cursor back on the row `PopMenuState` restored, by name. True when it moved.
	/// </summary>
	private bool RestorePoppedHighlight()
	{
		if (Model is not { } model ||
			!model.Menus.TryGetValue(Presentation, out Engine_UiMenuModel.Menu? menu) ||
			menu.TakeRestoredHighlight() is not { } name)
		{
			return false;
		}

		IReadOnlyList<Engine_UiExpandableMenu.Item> visible = Script.Visible;
		for (int i = 0; i < visible.Count; ++i)
		{
			if (string.Equals(visible[i].Name, name, System.StringComparison.Ordinal))
			{
				Script.Highlight(i + 1);
				return true;
			}
		}

		return false;
	}

	/// <summary>The template this list was built from, so it is built once and then only joined.</summary>
	private Engine_UiMenuTemplate? _authoredFrom;

	/// <summary>True while the row set is the TEMPLATE's rather than a read of the guest.</summary>
	private bool _authored;

	private bool _saidAuthored;

	private bool _saidTemplateDry;

	private string _saidStatedScript = string.Empty;

	private string _boundScript = string.Empty;

	private int _saidMode = -1;

	private uint _saidUnbound = uint.MaxValue;

	/// <summary>The last orphan report, so it prints on change rather than per rebuild.</summary>
	private string? _orphanReported;

	private bool RebuildFromModel(Engine_UiMenuModel.Menu menu, Engine_UiScreen? screen)
	{
		var signature = new System.Collections.Generic.List<string>(menu.Rows.Count + 1);
		signature.Add($"ghost-refresh|{menu.GhostRefreshes}");
		foreach (Engine_UiMenuModel.Row row in menu.Rows)
		{
			signature.Add($"{row.Name}|{row.Parent}|{row.Label}|{row.Ghosted}|" +
				$"{(int)row.Emphasis}|{_foldGeneration}");
		}

		if (Same(signature, _signature))
		{
			return false;
		}

		_signature.Clear();
		_signature.AddRange(signature);

		Menu.Clear();
		_hostChildren = 0;

		var byName = new System.Collections.Generic.Dictionary<
			string, Engine_UiExpandableMenu.Item>(System.StringComparer.Ordinal);
		var pending = new System.Collections.Generic.List<Engine_UiMenuModel.Row>(menu.Rows);
		var placed = new System.Collections.Generic.List<
			(Engine_UiMenuModel.Row Row, Engine_UiExpandableMenu.Item Item)>(menu.Rows.Count);

		bool IsRoot(Engine_UiMenuModel.Row row) =>
			row.Parent.Length == 0 ||
			string.Equals(row.Parent, "Root", System.StringComparison.Ordinal);

		while (pending.Count != 0)
		{
			var next = new System.Collections.Generic.List<Engine_UiMenuModel.Row>();
			int placedThisWave = 0;

			foreach (Engine_UiMenuModel.Row row in pending)
			{
				Engine_UiExpandableMenu.Item? parent = null;
				if (!IsRoot(row) && !byName.TryGetValue(row.Parent, out parent))
				{
					next.Add(row);
					continue;
				}

				Engine_UiExpandableMenu.Item item = Script.Add(row.Name, parent);
				byName[row.Name] = item;
				placed.Add((row, item));
				++placedThisWave;
			}

			if (placedThisWave == 0)
			{
				// ORPHANS ARE DROPPED, NOT ROOTED — the shipped Lua's own behaviour.
				//
				// `g_Menu:AddChild(item, parent)` (expandablemenu main.6) looks the parent up by
				// name and, when `GetMenuItem` returns nothing, prints
				// "Somethings broken with GetMenuItem...!" and returns WITHOUT linking: the item
				// keeps no `ParentItem` and is in nobody's `ChildItems`, so it is in
				// `SpawnedItems` and in no visible list. Only a nil parent becomes ROOTNAME
				// (pc13-14) — a NAMED parent that does not resolve is not a root row.
				//
				// Rooting them instead put 32 outfit rows on the gift screen beside its nine
				// categories: the guest spawns them naming `Suits`, a category that screen never
				// spawns a header for, and the original therefore never shows them.
				var orphaned = new Dictionary<string, int>(System.StringComparer.Ordinal);
				foreach (Engine_UiMenuModel.Row row in next)
				{
					string named = row.Parent.Length == 0 ? "<none>" : row.Parent;
					orphaned[named] = orphaned.TryGetValue(named, out int seen) ? seen + 1 : 1;
				}

				if (orphaned.Count != 0)
				{
					var parts = new List<string>();
					foreach ((string named, int count) in orphaned)
					{
						parts.Add($"'{named}' x{count}");
					}

					parts.Sort();
					string line = $"{next.Count} row(s) name a parent that was never spawned: " +
						string.Join(", ", parts);
					if (line != _orphanReported)
					{
						_orphanReported = line;
						GD.Print($"[screen-menu] '{PopulationScript}' {line} — DROPPED, as " +
							"`AddChild` drops them");
					}
				}

				break;
			}

			pending = next;
		}

		foreach ((Engine_UiMenuModel.Row row, Engine_UiExpandableMenu.Item item) in placed)
		{
			// `String2` is the resolved, decorated string WHERE THE GUEST SUPPLIED ONE. It does
			// not always: the item lists spawn with `String1` set to the item's BABEL TAG and no
			// `String2` at all, leaving the Lua to resolve it — which is why the gift screen
			// showed 32 rows reading `INV_ITEM_CLOTHES_POOR_FEMALE_SUIT_NAME` verbatim. Leave
			// the tag on `TextTag` and no stated label, and the view resolves it through the
			// same book every other tag goes through (`INV_ITEM_CLOTHES_POOR_FEMALE_SUIT_NAME`
			// → 'Female Lower Class Outfit', checked against the shipped book.babel).
			if (row.Label.Length != 0)
			{
				item.StatedLabel = row.Label;
				item.TextTag = row.Label;
			}
			else
			{
				item.StatedLabel = null;
				item.TextTag = row.Name;
			}

			item.Ordinal = -1;
			item.HeadingOverride = null;
			item.EventType = row.EventType;
			item.IdLow = (int)(row.Id & 0xFFFFu);
			item.IdHigh = (int)((row.Id >> 16) & 0xFFFFu);
			item.Committable = true;
			item.RatingSlide = row.Rating;
			item.Ghosted = Engine_UiMenuModel.EffectivelyGhosted(menu, row);
			item.Emphasis = row.Emphasis;
			item.IsOpen = _open.Contains(Engine_UiScreenMenuSpawn.PathOf(item));
		}

		AttachSourcedFolders();
		PruneAuthoredDrops();

		Script.UpdateAfterSpawning();
		Script.UpdateAllGhosting();
		_labels.Clear();
		_ordinals.Clear();
		_indents.Clear();
		foreach (Engine_UiExpandableMenu.Item shown in Script.Visible)
		{
			_labels.Add(shown.StatedLabel ?? shown.Name);
			_ordinals.Add(shown.Ordinal);
			_indents.Add(null);
		}

		// The fold and the highlight are both ours here. `SetInitiallySelectedItem` is the one
		// cursor statement the C++ makes, and it applies on the first build only, as the guest's.
		if (menu.InitiallySelected >= 1 && !_initiallySelectedApplied)
		{
			_initiallySelectedApplied = true;
			Script.Highlight(System.Math.Clamp(
				menu.InitiallySelected, 1, System.Math.Max(1, Script.Visible.Count)));
		}
		else
		{
			Script.Highlight(System.Math.Clamp(Script.CurrentHighlightIdx, 1,
				System.Math.Max(1, Script.Visible.Count)));
		}

		_statedVisible = Script.Visible.Count;
		return true;
	}

	public bool Rebuild(uint presentation, Engine_UiScreen? screen)
	{
		bool changed = RebuildList(presentation, screen);
		return RestorePoppedHighlight() | changed;
	}

	private bool RebuildList(uint presentation, Engine_UiScreen? screen)
	{
		PopulationScript = Engine_ExpandableMenuItems.PopulationScriptOf(screen);

		// THE GUEST'S STATEMENT WINS OVER THE FILE'S ATTRIBUTE. `RepopulateMenuFromPopulationScript`
		// is the shipped Lua's `setAttribute(parent, "MenuPopulationScript", name)` followed by the
		// repopulation, so its argument IS the widget's live attribute; the file's is only where
		// it started. The logbook opened on an orb is the case: its widget says `LogbookMenu` and
		// the open states `'LogbookMenuOrb'` (`sub_826D8DD8`), one row. The gift family's widget
		// has no attribute at all, so the statement is its only name. The model forgets a
		// presentation that is freed, so a statement never outlives the screen it was made on.
		if (Model?.For(presentation)?.PopulationScript is { Length: > 0 } stated &&
			!string.Equals(stated, PopulationScript, System.StringComparison.Ordinal))
		{
			string file = PopulationScript;
			PopulationScript = stated;
			if (!string.Equals(_saidStatedScript, $"{presentation:X8}:{stated}", System.StringComparison.Ordinal))
			{
				_saidStatedScript = $"{presentation:X8}:{stated}";
				GD.Print($"[screen-menu] presentation 0x{presentation:X8} is '{stated}' by the guest's " +
					$"RepopulateMenuFromPopulationScript (its file says " +
					$"{(file.Length == 0 ? "none" : $"'{file}'")})");
			}
		}

		// AND THE GIFT FAMILY STATES IT NEITHER WAY. `sub_82430468` spawns screen 15's rows from
		// native C++ and issues no `RepopulateMenuFromPopulationScript` at all, so the screen's
		// own template names the list instead. A screen whose template names its lists BY MODE takes
		// the mode's list over a stated script whose shipped table authors no row (a runtime list):
		// the items book's attach and strip repopulate from 'EquipmentMenu' (`dword_8349B29C`,
		// `sub_826D1800` 0x826D2040 / 0x826D22F4) before spawning the items rows natively.
		if (PopulationScript.Length != 0 && ScreenId != 0 &&
			Engine_UiTemplates.Screen(ScreenId) is { MenuScriptByMode.Count: > 0 } &&
			Refii.Core.Engine_TitleGame.Active.PopulationTable(PopulationScript) is { Entries.Count: 0 })
		{
			PopulationScript = string.Empty;
		}

		if (PopulationScript.Length == 0 && ScreenId != 0 &&
			Engine_UiTemplates.Screen(ScreenId) is { } screenTemplate)
		{
			// A SCREEN CAN BE SEVERAL LISTS. Screen 15 is one id over Items, Weapons and
			// Clothing, chosen by the mode word `sub_829B0A58` reads at `screen+372`; binding it
			// to a single script would put the item categories on the weapons list. The mode is
			// asked for only when the template says the screen has modes.
			string bound = screenTemplate.MenuScript;
			if (screenTemplate.MenuScriptByMode.Count != 0)
			{
				int mode = (int)Refii.Host.HostBridge.GetInventoryScreenMode();
				if (screenTemplate.MenuScriptByMode.TryGetValue(mode, out string? byMode))
				{
					bound = byMode;
				}
				else if (mode != _saidMode)
				{
					_saidMode = mode;
					GD.Print($"[screen-menu] screen {ScreenId} is in mode {mode}, which its " +
						$"template does not name a list for — falling back to '{bound}'");
				}
			}

			if (bound.Length > 0 && !string.Equals(bound, _boundScript, System.StringComparison.Ordinal))
			{
				_boundScript = bound;
				GD.Print($"[screen-menu] screen {ScreenId} names no population script — its " +
					$"template binds it to '{bound}'");
			}

			PopulationScript = bound;
		}

		Presentation = presentation;

		// THE TEMPLATE FIRST, WHERE IT HAS ROWS. This list is Godot's: the rows come from the
		// population table the guest's own script declares, not from reading back what the guest
		// has laid out. Only the LIVE parts — the decorated label, the ghosting — are still the
		// guest's, joined on afterwards.
		//
		// THE SPLIT IS ALREADY IN THE DATA, which is why this needs no per-screen flag.
		// `Engine_UiMenuPopulation` gives eight tables real entries — HenchmanPauseMenu,
		// OptionsMenu, LogbookMenu, LogbookMenuOrb, CrimeMenu, ExpressionsMenu, AugmentMenu,
		// DyeMenu — and registers thirteen more as names alone: PauseMenu (its rows are the
		// C++'s, and the title builds them from `pause-root`), AbilitiesMenu,
		// EquipmentMenu, ItemsMenu, ClothingMenu, WorldMapMenu and the rest, which are populated
		// at runtime from the save. Those rows genuinely ARE the guest's data and still arrive
		// through the capture below, until they are read from the data areas instead.
		// A runtime template whose source is dry is not built. `BuildInto` clears the menu
		// before it spawns and the template is re-adopted every sample this falls through
		// (`_authoredFrom` is dropped below), so a template that yields nothing would empty the
		// list the MODEL built, and the model does not rebuild unchanged rows. The real-estate
		// screen is the case: its rows are spawned while the capture still states screen 0, so
		// its `capture:Root` is dry and only the model has its rows.
		Engine_UiMenuTemplate? authored = Engine_UiTemplates.Menu(PopulationScript);
		if (authored is { Entries.Count: 0, Source.Length: > 0 } runtime && !SourceHasRows(runtime))
		{
			if (!_saidTemplateDry)
			{
				_saidTemplateDry = true;
				GD.Print($"[screen-menu] '{PopulationScript}' has a template but its source " +
					$"'{runtime.Source}' yields NO rows — the list stays the model's. This is " +
					"the data hook, not the menu.");
			}

			authored = null;
		}

		if (authored is not null &&
			(authored.Entries.Count != 0 || authored.Source.Length != 0))
		{
			bool changed = RebuildFromTemplate(authored);
			if (Script.Visible.Count != 0)
			{
				if (!_saidAuthored)
				{
					_saidAuthored = true;
					GD.Print($"[screen-menu] '{PopulationScript}' is GODOT'S — " +
						$"{Script.Visible.Count} row(s) built from the population template" +
						(authored.Source.Length == 0
							? string.Empty
							: $" (source '{authored.Source}')") +
						", committing by identity; the guest supplies labels and ghosting only");
				}

				_stated = false;
				_authored = true;
				return changed;
			}

			// THE TEMPLATE PRODUCED NOTHING, so its source is dry — for the runtime menus that
			// means the `g_Menu` capture has no rows, which is a DEAD DATA HOOK and not a menu
			// with no entries. Said once per list; the model below is the only other source.
			if (!_saidTemplateDry)
			{
				_saidTemplateDry = true;
				GD.Print($"[screen-menu] '{PopulationScript}' has a template but it yielded NO " +
					$"rows (source '{authored.Source}') — falling back to the stated g_Menu " +
					"model. This is the data hook, not the menu.");
			}
		}

		// NO SCRIPT AND NO BINDING — the list is nobody's template, so say which screen it was.
		//
		// Every screen that reaches this line is one still to be bound, and the id is the only
		// handle on it: these are exactly the screens whose widgets state no
		// `MenuPopulationScript`.
		if (PopulationScript.Length == 0 && _saidUnbound != ScreenId)
		{
			_saidUnbound = ScreenId;
			GD.Print($"[screen-menu] screen {ScreenId} names NO population script and its " +
				"template binds none — the list is not owned by a template yet");
		}

		_authored = false;
		_authoredFrom = null;

		// The model next where it has rows: its rows are the C++'s own `g_Menu` statements, and
		// this path never touches the Gameface VM.
		if (Model?.For(presentation) is { Rows.Count: > 0 } modelled)
		{
			if (!_saidModel)
			{
				_saidModel = true;
				GD.Print($"[screen-menu] '{PopulationScript}' reads its model from the C++ " +
					$"STATEMENTS — {modelled.Rows.Count} row(s) on presentation " +
					$"0x{presentation:X8}, no Gameface read");
			}

			_stated = true;
			return RebuildFromModel(modelled, screen);
		}

		// Neither the template nor the C++'s statements have rows for this presentation: the
		// list is empty. Said once per presentation — a screen that reaches here is one whose
		// populate states nothing the runner records.
		if (_saidEmpty != presentation)
		{
			_saidEmpty = presentation;
			GD.Print($"[screen-menu] '{PopulationScript}' has no template rows and no stated " +
				$"g_Menu rows on presentation 0x{presentation:X8} — the list is empty");
		}

		bool wasEmpty = Script.Visible.Count == 0 && _labels.Count == 0;
		_stated = false;
		_signature.Clear();
		_statedVisible = -1;
		if (wasEmpty)
		{
			return false;
		}

		Script.Clear();
		_labels.Clear();
		_ordinals.Clear();
		_indents.Clear();
		return true;
	}

	/// <summary>The presentation the empty-list note was last said for.</summary>
	private uint _saidEmpty;

	/// <summary>How many host-only rows the last read attached, at every depth.</summary>
	private int _hostChildren;

	/// <summary>
	/// B — collapse the highlighted row's parent and put the cursor on it. False at the root,
	/// the caller's signal to forward B: the guest's `sub_826D5AF0` then puts the screen back on
	/// the PAUSE ROOT (id 0), it does not leave the GUI. The set and the model move together —
	/// `CloseParent` alone is overwritten from <see cref="_open"/> on the next rebuild.
	/// </summary>
	public bool Collapse()
	{
		if (Script.Highlighted is not { Parent: { } parent } ||
			ReferenceEquals(parent, Script.MenuRoot))
		{
			return false;
		}

		if (_stated)
		{
			_open.Remove(FoldPath(parent));
			++_foldGeneration;
		}

		return Script.CloseParent();
	}

	/// <summary>An item's fold key — its path from the root. See <see cref="_open"/>.</summary>
	private string FoldPath(Engine_UiExpandableMenu.Item item)
	{
		string path = item.Name;
		for (Engine_UiExpandableMenu.Item? at = item.Parent;
			at is not null && !ReferenceEquals(at, Script.MenuRoot); at = at.Parent)
		{
			path = at.Name + "/" + path;
		}

		return path;
	}

	/// <summary>A row's fold identity across a rebuild: name plus label, since an unjoined row is
	/// called `Row{n}`, which is its position and moves when the list changes length.</summary>
	private static string FoldKey(Engine_UiExpandableMenu.Item item) =>
		$"{item.Name}\u0000{item.StatedLabel ?? string.Empty}";

	/// <summary>
	/// A leaf the host unfolds — the logbook's Achievements row, whose action the console's
	/// guide answered. Its children are ours (Ordinal -1), in the source's order, re-attached on
	/// every rebuild so a list that arrived after the row did still lands. The row is named by
	/// its own identity where the model or the capture gave one; a pool solved from positions
	/// names its rows `RowN`, and then the population script's root order names them — the
	/// guest's list IS that table while no folder is open, which is when a root can be pressed.
	/// </summary>
	/// <summary>
	/// Drops the rows this list's TEMPLATE says the guest may spawn but we do not show — see
	/// <see cref="Engine_UiMenuEntry.Drop"/>.
	///
	/// Through `RemoveChild`, the guest's own call (<see cref="Engine_UiExpandableMenu.Remove"/>):
	/// the row leaves its parent's children and the spawn list, everything under it goes with it,
	/// and the recompute clamps the cursor. Applied AFTER the sourced folders so a dropped row
	/// takes its attached children with it either way.
	///
	/// Keyed on the population script, which is what names the template — so this reaches the
	/// STATED build and the position solve alike. The position solve can only drop a row whose
	/// identity it joined; one that fell back to `Row{n}` has no name to match, and that is the
	/// same limit every other name-keyed rule on that path has.
	/// </summary>
	private void PruneAuthoredDrops()
	{
		if (PopulationScript.Length == 0)
		{
			return;
		}

		IReadOnlyList<Engine_UiTemplates.DroppedRow> dropped =
			Engine_UiTemplates.DroppedRows(PopulationScript);
		if (dropped.Count == 0)
		{
			return;
		}

		int gone = 0;
		var how = new List<string>();
		foreach (Engine_UiTemplates.DroppedRow drop in dropped)
		{
			// BY NAME, which is what a STATED build carries.
			while (drop.Name.Length != 0 && Script.Find(drop.Name) is { } named)
			{
				Detach(named);
				++gone;
				how.Add($"{drop.Name} (by name)");
			}

			// BY DISPLAYED TEXT, which is all the POSITION SOLVE carries. It joined 0 of the
			// logbook's 5 rows against the spawn capture in a real run — every row was called
			// `Row{n}` — so a name-only drop reaches nothing on the screen this exists for.
			// The tag resolves through the guest's own string table, so this is the same text
			// the player is reading, in whatever language is installed.
			if (drop.Tag.Length == 0 || Strings is null ||
				!Strings.TryGetString(drop.Tag, out string label) || label.Length == 0)
			{
				continue;
			}

			while (FindByLabel(label) is { } shown)
			{
				Detach(shown);
				++gone;
				how.Add($"'{label}' (by text, tag {drop.Tag})");
			}
		}

		if (gone != 0 && !_saidDrops)
		{
			_saidDrops = true;
			GD.Print($"[screen-menu] '{PopulationScript}' drops {gone} authored row(s): " +
				string.Join(", ", how));
		}

		void Detach(Engine_UiExpandableMenu.Item item)
		{
			// `RemoveChild`: out of the parent's children and out of the spawn list, subtree
			// and all. Done by ITEM rather than by name because a row matched on its text has
			// no usable name to look up again.
			item.Parent?.Children.Remove(item);
			Script.Forget(item);
		}
	}

	/// <summary>
	/// A row by the text it is DISPLAYING. Prefix rather than equality: the guest appends live
	/// decoration to a label before it writes the row — `Abilities &lt;s4&gt;(2)&lt;/s4&gt;` when
	/// there are points to spend — and that exists nowhere but in the string.
	/// </summary>
	private Engine_UiExpandableMenu.Item? FindByLabel(string label)
	{
		foreach (Engine_UiExpandableMenu.Item item in Script.SpawnedItems)
		{
			if (item.StatedLabel is { } shown &&
				shown.StartsWith(label, System.StringComparison.Ordinal))
			{
				return item;
			}
		}

		return null;
	}

	private bool _saidDrops;

	private void AttachSourcedFolders()
	{
		// Bound the same way the template build is — a folded source reads the same capture.
		uint boundWas = Engine_UiMenuSources.BoundScreen;
		Engine_UiMenuSources.BoundScreen = CaptureKey;
		try
		{
			AttachSourcedFoldersBound();
		}
		finally
		{
			Engine_UiMenuSources.BoundScreen = boundWas;
		}
	}

	private void AttachSourcedFoldersBound()
	{
		var roots = new List<Engine_UiExpandableMenu.Item>();
		foreach (Engine_UiExpandableMenu.Item item in Script.SpawnedItems)
		{
			if (item.Parent is null || ReferenceEquals(item.Parent, Script.MenuRoot))
			{
				roots.Add(item);
			}
		}

		var tableRoots = new List<string>();
		if (Refii.Core.Engine_TitleGame.Active.PopulationTable(PopulationScript) is { } table)
		{
			foreach (Engine_UiPopulationTable.Entry entry in table.Entries)
			{
				if (entry.Parent.Length == 0)
				{
					tableRoots.Add(entry.Name);
				}
			}
		}

		for (int i = 0; i < roots.Count; ++i)
		{
			Engine_UiExpandableMenu.Item item = roots[i];
			string key = item.Name;
			if (key.StartsWith("Row", System.StringComparison.Ordinal) && tableRoots.Count == roots.Count)
			{
				key = tableRoots[i];
			}

			if (Engine_UiMenuSources.StatedFolderSource(PopulationScript, key) is not { } source)
			{
				continue;
			}

			foreach (Engine_UiMenuSources.Row sourced in Engine_UiMenuSources.Rows(source, key))
			{
				Engine_UiExpandableMenu.Item child = Script.Add(sourced.Name, item);
				child.StatedLabel = sourced.Label;
				child.TextTag = sourced.Label;
				child.Ordinal = -1;
				child.HeadingOverride = false;
				child.EventType = sourced.EventType;
				child.IdLow = (int)(sourced.Id & 0xFFFFu);
				child.IdHigh = (int)((sourced.Id >> 16) & 0xFFFFu);
				child.Committable = sourced.Committable;
				child.Ghosted = sourced.Ghosted;
				child.HostOnly = sourced.HostOnly;
				child.FromMod = sourced.FromMod;
				child.IsOpen = false;
				++_hostChildren;
			}
		}
	}

	/// <summary>Every open row in the tree, by <see cref="FoldKey"/>, before the model is cleared.</summary>
	private static void CollectOpen(
		Engine_UiExpandableMenu.Item item, System.Collections.Generic.HashSet<string> into)
	{
		foreach (Engine_UiExpandableMenu.Item child in item.Children)
		{
			if (child.IsOpen)
			{
				into.Add(FoldKey(child));
			}

			CollectOpen(child, into);
		}
	}

	private static bool Same(List<string> read, List<string> held)
	{
		if (read.Count != held.Count)
		{
			return false;
		}

		for (int i = 0; i < read.Count; ++i)
		{
			if (!string.Equals(read[i], held[i], System.StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Runs a row's own <see cref="Engine_UiMenuEntry.Command"/>, if it names one that is
	/// implemented. True when the row is fully answered and nothing should reach the guest.
	///
	/// A row with a host action needs no guest identity — that is the whole point of having one —
	/// so this is tried ahead of both the identity push and `Committable`.
	/// </summary>
	/// <summary>
	/// Whether this screen's handler reads the guest's own highlight rather than a pushed event
	/// — see <see cref="Engine_UiScreenTemplate.CommitsByHighlight"/>.
	/// </summary>
	private bool CommitsByHighlight => Engine_UiTemplates.Screen(ScreenId)?.CommitsByHighlight == true;

	/// <summary>
	/// The commit for a screen whose handler acts on the highlighted row rather than a pushed
	/// event: the row is stated as highlighted for this screen (`StateHighlightedRow`, which
	/// `sub_826C1B50` answers from), then the screen's own A (code 36) runs its 36 arm, which
	/// reads it. The statement is made before the press, so a click on a row the cursor was not
	/// on commits the row clicked.
	/// </summary>
	/// <summary>
	/// A MOD'S ROW WITH NO HOST ACTION COMMITS NOTHING. It has no row in the guest's list and no
	/// guest record behind its (EventType, id): `MenuHighlight` by its name finds nothing, so the
	/// guest's select or A would act on whichever of its own rows its cursor last held, and an
	/// identity push hands a screen handler an id whose record lookup fails. A furniture level the
	/// hero holds no card for: the Page2 filler `sub_8299C710` (disc `sub_828F22B8`) drops
	/// `sub_825CC2F0`'s answer (`bl` 0x8299CAF4 / 0x828F269C, r3 overwritten by `subf` at
	/// 0x8299CAFC / 0x828F26A4) and reads through the unfilled card (`sub_827647F8` 0x82764814);
	/// the upgrade `sub_826B6A18` tests it (`clrlwi r11,r3,24; beq` 0x826B6AF0 / 0x8259BC58).
	/// </summary>
	private static bool RefusesModRow(Engine_UiExpandableMenu.Item row, string label)
	{
		if (!row.FromMod)
		{
			return false;
		}

		GD.Print($"[screen-menu] '{label}' is a mod's row with no host action — nothing is committed");
		return true;
	}

	private void CommitThroughHighlight(Engine_UiExpandableMenu.Item row, string label)
	{
		GD.Print($"[screen-menu] commit THROUGH THE HIGHLIGHTED ROW: '{label}' ({row.Name}, type " +
			$"{row.EventType}) stated for screen {ScreenId}, then the screen's own A");
		StateAndPress(Engine_UiSource.Current, ScreenId, row);
	}

	/// <summary>
	/// The two calls of a highlight commit, in order: the row stated as highlighted for
	/// <paramref name="screen"/> — its event type, full id (`(IdHigh &lt;&lt; 16) + IdLow`, as
	/// `sub_826C1B50` hands it back at 0x826c1c90), name and label — then the screen's A.
	/// </summary>
	internal static void StateAndPress(Engine_UiSource source, uint screen, Engine_UiExpandableMenu.Item row)
	{
		int id = (row.IdHigh << 16) + row.IdLow;
		source.StateHighlightedRow(screen, row.EventType, id, row.Name,
			row.StatedLabel ?? row.TextTag ?? string.Empty);
		source.ScreenAction(Engine_UiScreenInput.Select);
	}

	private static bool RunHostAction(Engine_UiExpandableMenu.Item row, string label) =>
		row.Command is { Length: > 0 } action && RunAction(row, label, action);

	/// <summary>
	/// The screen's own verb for its rows (<see cref="Engine_UiScreenTemplate.RowAction"/>), the
	/// row's full id stated in it. False when the screen states none.
	/// </summary>
	private bool RunRowAction(Engine_UiExpandableMenu.Item row, string label)
	{
		if (Engine_UiTemplates.Screen(ScreenId)?.RowAction is not { Length: > 0 } verb)
		{
			return false;
		}

		int id = (row.IdHigh << 16) + row.IdLow;
		return RunAction(row, label, verb.Replace(Engine_UiScreenTemplate.RowIdToken,
			id.ToString(System.Globalization.CultureInfo.InvariantCulture), System.StringComparison.Ordinal));
	}

	private static bool RunAction(Engine_UiExpandableMenu.Item row, string label, string action)
	{
		Engine_UiCommandResult ran = Engine_UiCommands.Run(action, new Engine_UiCommandContext(
			Engine_UiCommandList.Screen, Row: row.Name, Player: Engine_UiModMenus.MenuPlayer)
		{
			RowEventType = row.EventType,
			RowId = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu),
		});
		if (ran.Outcome == Engine_UiCommandOutcome.Done)
		{
			Engine_UiScreenActions.RefreshSettingLabel(row);
			GD.Print($"[screen-menu] '{label}' ran the HOST action '{action}' — " +
				"nothing was pushed to the guest");
			return true;
		}

		// A verb that refused said why (Engine_UiCommands); the row is answered, not the guest's.
		if (ran.Outcome == Engine_UiCommandOutcome.Refused)
		{
			return true;
		}

		// Named but unknown: say so rather than silently falling through to the guest, which
		// would look like it worked while doing something else entirely.
		GD.Print($"[screen-menu] '{label}' names host action '{action}' but nothing " +
			"implements it — falling through");
		return false;
	}

	/// <summary>
	/// Commits the highlighted row BY IDENTITY, naming the row so our cursor is the only one — a
	/// plain A press commits whatever `g_Menu:GetHighlightedMenuItemData()` has as current.
	/// Ghosted rows are refused here: the script's early return lives in `SelectMenuItem` and the
	/// event push bypasses it. False when the row cannot be named (inventing an
	/// `(EventType, id)` pair would activate something else), so the caller falls back to the A
	/// press.
	/// </summary>
	/// <summary>Whether the highlighted row is a mod ability's, which has nothing to unlearn.</summary>
	public bool HighlightedIsModAbility =>
		Script.Highlighted?.Command is { } command && Refii.Core.Engine_TitleGame.Active.IsModAbilityRow(command);

	public bool Commit()
	{
		if (Script.Highlighted is not { } row)
		{
			GD.Print("[screen-menu] A pressed with NO highlighted row — falling back to the A press");
			return false;
		}

		string label = row.StatedLabel ?? row.Name;

		// A FOLDER IS ASKED AGAIN BEFORE IT IS JUDGED EMPTY. A category's children are its source's rows
		// (the item book's `items:use:<category>`), re-synced on the samples; a press that lands before the
		// re-sync has seen the guest's rows found the folder childless and committed it to the guest by
		// identity (`items (15) OnMenuEvent (301 0)`), which opens nothing. So a childless submenu row whose
		// template entry names a source is refreshed from it first, and folds when that gives it rows.
		if (row.Children.Count == 0 && row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu &&
			_authoredFrom?.Entry(row.Name) is { Source.Length: > 0 } sourcedEntry)
		{
			uint boundWas = Engine_UiMenuSources.BoundScreen;
			Engine_UiMenuSources.BoundScreen = CaptureKey;
			try
			{
				if (_authoredFrom.RefreshSources(Menu))
				{
					PruneAuthoredDrops();
					Script.UpdateAfterSpawning();
					Script.UpdateAllGhosting();
				}

				if (row.Children.Count == 0)
				{
					int under = 0;
					var parents = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
					foreach (Engine_UiModel.MenuRow captured in Engine_UiMenuSources.SpawnedRows())
					{
						parents.Add(captured.Parent);
						under += string.Equals(captured.Parent, row.Name, System.StringComparison.Ordinal) ? 1 : 0;
					}

					GD.Print($"[screen-menu] folder '{row.Name}' is empty: its source '{sourcedEntry.Source}' gives " +
						$"{Engine_UiMenuSources.Rows(sourcedEntry.Source, sourcedEntry.Name).Count} row(s); the capture " +
						$"(screen {Engine_UiSource.Current.MenuScreenId()}, bound {CaptureKey}) holds {under} under it, " +
						$"parents [{string.Join(", ", parents)}]");
				}
			}
			finally
			{
				Engine_UiMenuSources.BoundScreen = boundWas;
			}
		}

		// Declared before the host-row branch below, which folds through the same flag.
		bool folds = row.Children.Count > 0;

		// A host-only row never touches the guest's cursor: its fold is ours, and its leaf commit
		// is the identity event — the `GUI:PushButtonEvent` `SelectMenuItem` posts, which the
		// drain dispatches to the screen's handler without consulting Lua's highlight.
		if (row.Ordinal < 0)
		{
			if (folds)
			{
				if (Script.Select() is null)
				{
					GD.Print($"[screen-menu] '{label}' — the press landed mid-slide and " +
						"SelectMenuItem swallows the WHOLE action");
					return true;
				}

				GD.Print($"[screen-menu] host fold: '{label}' " +
					$"({row.Children.Count} captured child(ren)) — now " +
					$"{(row.IsOpen ? "open" : "closed")}");
				return true;
			}

			// THE ROW'S OWN ACTION FIRST, and this branch used to skip it entirely.
			//
			// A row with no guest ordinal left here by the identity commit below without ever
			// consulting `Command`, so every sourced row that performs something host-side —
			// an ability purchase, an unequip — pushed a `(name, event, id)` at the guest's
			// dispatcher instead of doing the thing. A run showed it plainly: pressing Physique
			// logged `host commit BY IDENTITY` and bought nothing.
			//
			// The identity push stays as the fallback for rows that genuinely have no action of
			// their own, which is most of them.
			// A ghosted leaf is silent here as below — `SelectMenuItem` returns before pushing.
			if (row.Ghosted)
			{
				GD.Print($"[screen-menu] '{label}' is GHOSTED — no commit");
				return true;
			}

			if (RunHostAction(row, label) || RefusesModRow(row, label) || RunRowAction(row, label))
			{
				return true;
			}

			if (CommitsByHighlight)
			{
				CommitThroughHighlight(row, label);
				return true;
			}

			// NO ACTION AND NO IDENTITY OF THE GUEST'S: nothing is pushed. A row the guest would
			// not act on (a trophy on the items list, a clothing slot with nothing to take off)
			// used to borrow a ticket by name and push it at a handler that reads its own cursor —
			// the abilities-discard shape, on items and clothing.
			if (!row.Committable)
			{
				GD.Print($"[screen-menu] '{label}' has no action and no identity of the guest's — " +
					"nothing to commit");
				return true;
			}

			uint hostId = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
			GD.Print($"[screen-menu] host commit BY IDENTITY: " +
				$"'{label}' ({row.Name}, type {row.EventType}, id 0x{hostId:X8})");
			Engine_UiSource.Current.MenuEvent(row.Name, row.EventType, hostId);
			return true;
		}

		// `Ghosted`, NOT `IsGhosted` — the auto-ghost dims a row, it does not silence it. See
		// Engine_UiExpandableMenu.Item.AutoGhosted.
		if (row.Ghosted)
		{
			GD.Print($"[screen-menu] '{label}' is GHOSTED — the shipped SelectMenuItem returns " +
				"before pushing anything, so neither do we");
			return true;
		}

		// A host-folded list (not the stated model) folds and moves its cursor in `Select()`; a
		// null answer is a press the script's own input gate swallows whole — no fold, no commit.
		if (!_stated && Script.Select() is null)
		{
			GD.Print($"[screen-menu] '{label}' — the press landed mid-slide and SelectMenuItem " +
				"swallows the WHOLE action, event included");
			return true;
		}

		// A folder folds here and nothing is posted. The guest's own `SelectMenuItem` is
		// unreachable: every host Lua chunk goes through `sub_82426470`, which defers rather than
		// runs once the GUI's `+0x64` byte is cleared (the constructor sets it, the frame update
		// clears it and nothing sets it back). The event is deliberately not pushed either — a
		// submenu row's event type is 301 (`sub_826CF098`, `li r9, 0x12D`) and no screen handler
		// has a case for it (`sub_826C6110` does not), so the expansion is the whole action.
		if (_stated && row.Children.Count > 0)
		{
			string path = FoldPath(row);
			bool opening = !_open.Contains(path);

			// The menu is an accordion: opening runs `AutoCollapse` first, or the visible count
			// runs past the twelve authored slots and rows land in the gutter either side.
			//
			// `expandablemenuformatting.dis main.6`, the shipped opcodes, verbatim:
			//
			//     for k, v in pairs(g_Menu.VisibleInfo.VisibleElements) do
			//       if v.IsOpen and k ~= keep then
			//         v.IsOpen = false
			//         self:CollapseMenuAndHighlight(k, true)
			//         self:onDirtyHighlight()
			//         return true                          -- pc11..19
			//       end
			//     end
			//     return false
			//
			// It walks the WHOLE visible list at every depth, so opening a nested category
			// closes the outer one — scoping it to siblings leaves two levels expanded.
			//
			// The toggle comes first: `SelectMenuItem` flips `IsOpen` at `main.5 pc26..28` and
			// only then fires the expand event (pc32..34), so the early return in
			// `onExpandMenuItem` (`main.7 pc8..13`) skips the CURSOR STEP, not the fold.
			if (opening)
			{
				_open.Add(path);
			}
			else
			{
				_open.Remove(path);
			}

			++_foldGeneration;
			row.IsOpen = opening;
			Script.DirtyLayout();

			string collapsed = string.Empty;
			if (opening)
			{
				// One finder, the formatter's — never a second copy of `main.6` here.
				if (Engine_UiMenuFormatter.AutoCollapse(Script, row) is { } other)
				{
					_open.Remove(FoldPath(other));
					++_foldGeneration;

					// `CollapseMenuAndHighlight(k, true)` — see the method for the arithmetic.
					Script.CollapseAndHighlight(other);
					collapsed = $" — AutoCollapse shut '{other.StatedLabel ?? other.Name}'";
				}
				else
				{
					// `CurrentHighlightIdx += 1` then `onScrollDown` (`main.7 pc14..22`) — the
					// cursor lands on the folder's first child. Only when nothing collapsed.
					Script.ScrollDown();
				}
			}

			GD.Print($"[screen-menu] fold: '{label}' {(opening ? "OPEN" : "SHUT")} " +
				$"({row.Children.Count} child(ren)) — {path}{collapsed}");
			return true;
		}

		// A HOST ACTION FIRST, AND IT NEVER REACHES THE GUEST'S DISPATCHER. A template row may
		// name what activating it DOES (`Engine_UiMenuEntry.Command`), and that is the seam by
		// which a menu stops needing the guest's UI at all: the row is ours, the action is ours,
		// and nothing is pushed into `PushButtonEvent` for a screen class to interpret.
		//
		// It is checked before `Committable` on purpose — a row with a host action needs no
		// guest identity, which is the whole point of having one.
		if (RunHostAction(row, label) || RefusesModRow(row, label) || RunRowAction(row, label))
		{
			return true;
		}

		if (CommitsByHighlight)
		{
			CommitThroughHighlight(row, label);
			return true;
		}

		// A stated row with no identity of the guest's: the guest's own A on that row, its cursor
		// put there first — never the A press on wherever the guest's cursor happened to be.
		if (!row.Committable)
		{
			CommitThroughHighlight(row, label);
			return true;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		GD.Print($"[screen-menu] commit BY IDENTITY: " +
			$"'{label}' ({row.Name}, type {row.EventType}, id 0x{id:X8}) — our cursor " +
			$"{Script.CurrentHighlightIdx}/{Script.Visible.Count}");
		Engine_UiSource.Current.MenuEvent(row.Name, row.EventType, id);
		return true;
	}

	/// <summary>
	/// A one-line description for the build report. Every label is quoted and an empty one says
	/// so: a blank tab, a missing row and an unresolved label look identical otherwise.
	/// </summary>
	public string Describe()
	{
		var quoted = new List<string>();
		for (int i = 0; i < _labels.Count; ++i)
		{
			// The indent decides the row's face and nesting, so it is reported with the label: a
			// root row reads about -41 and a submenu row about -70, and an unstated one reads `-`.
			string indent = i < _indents.Count && _indents[i] is { } offset
				? $"@{offset:0}"
				: "@-";
			quoted.Add((_labels[i].Length == 0 ? "<EMPTY>" : $"'{_labels[i]}'") + indent);
		}

		// `Count` is the finished list; the quoted names are the READ it was built from, which a
		// template drop can leave shorter. Named so the two cannot be mistaken for each other.
		return $"{Count} row(s)" +
			(_authored
				? " [GODOT'S, from the population template]"
				: _stated ? " [STATED by g_Menu]" : " [empty: no template rows, no statements]") +
			(_hostChildren == 0
				? string.Empty
				: $" (+{_hostChildren} host child(ren), folded)") +
			(_labels.Count == 0 ? string.Empty : $", read: {string.Join(", ", quoted)}") +
			$"  [script model: {Script.Describe()}]" +
			(PopulationScript.Length == 0
				? ", the widget names no population script"
				: $", population script '{PopulationScript}'" +
					(Refii.Core.Engine_TitleGame.Active.PopulationTable(PopulationScript) is { } known
						? $" ({known.Entries.Count} static entr(y/ies), " +
							$"HighlightIdx {known.HighlightIndex})"
						: " — NOT IN THE TRANSLATED SET"));
	}
}
