using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// `g_Menu` in C#: the shipped expandable-menu model of `scripts/gameface/expandablemenu/*.lua`,
/// translated from the bytecode. Every in-game list is one of these. It does not populate itself —
/// <see cref="Engine_UiScreenMenu"/> reads the guest's rows off the element tree and calls
/// <see cref="SpawnButton"/>.
/// </summary>
public sealed class Engine_UiExpandableMenu
{
	/// <summary>`Const.ROOTNAME`.</summary>
	public const string RootName = Engine_UiMenuConstants.RootName;

	/// <summary>
	/// `Const.NUM_VISIBLE` = 10 rows in the window. Not `NUM_VIS_SLOTS`, which is 12: slots 1 and 12
	/// are the fade gutters (`opacity_scale_index` 1 → `{scale 0.65, opacity 0}`).
	/// </summary>
	public const int NumVisible = Engine_UiMenuConstants.NumVisible;

	/// <summary>`HIGHLIGHT_INDEX` = 4 — the slot the cursor is pinned to.</summary>
	public const int HighlightSlot = Engine_UiMenuConstants.HighlightSlot;

	/// <summary>`Const.EMPTY_RATING_SLIDE` = 11, `ONE_STAR_EMPTY_RATING_SLIDE` = 1.</summary>
	public const int EmptyRatingSlide = Engine_UiMenuConstants.EmptyRatingSlide;

	/// <summary>`g_Menu.EmphasisConst`.</summary>
	public const Engine_UiMenuConstants.Emphasis NoEmphasis =
		Engine_UiMenuConstants.Emphasis.None;

	/// <summary>
	/// One entry, with the fields `CreateMenuItem` gives it. The `_no name_` / `_no tag_` /
	/// `_no texture_` defaults are the script's own.
	/// </summary>
	public sealed class Item
	{
		public Item()
		{
		}

		/// <summary>
		/// The host-authored form: name, label tag, and optionally the parent's NAME — the link
		/// itself is made by `Add`, so a template can state nesting before the tree exists.
		/// </summary>
		public Item(string name, string textTag, string? parentName = null)
		{
			Name = name;
			TextTag = textTag;
			ParentName = parentName;
		}

		public string Name = "_no name_";
		public string TextTag = "_no tag_";
		public string IconTexture = "_no texture_";

		/// <summary>The display string the guest wrote, which can differ from what the tag resolves to.</summary>
		public string? StatedLabel;

		/// <summary>`CreateMenuItem`'s `EventType`, 0 when the caller omits it —
		/// `expandablemenu.dis main.9 pc21..24`.</summary>
		public int EventType;

		/// <summary>
		/// `CreateMenuItem`'s `IDLow`; the absent value is -1, not 0 (`expandablemenu.dis main.9
		/// pc25..28`), while `IDHigh` at pc29..32 defaults to 0 — 0 is a usable id. `Commit` packs
		/// `(IdHigh &lt;&lt; 16) | (IdLow &amp; 0xFFFF)`, widening -1 to 0xFFFF.
		/// </summary>
		public int IdLow = -1;

		/// <summary>`CreateMenuItem`'s `IDHigh`, 0 when the caller omits it — `main.9`
		/// pc29..32.</summary>
		public int IdHigh;

		/// <summary>
		/// The guest object a sourced row stands for, beside the id that names its kind —
		/// see `Engine_UiMenuSources.Row.Instance`. 0 for none.
		/// </summary>
		public uint Instance;

		/// <summary>`item.Ghosted` — set by `SetItemGhosting`, which is the guest's own call.</summary>
		public bool Ghosted;

		/// <summary>
		/// Whether a parent's header counts this row — see `Engine_UiMenuSources.Row.Counted`.
		/// </summary>
		public bool Counted = true;

		/// <summary>
		/// The row is new to the player, so its parent's header takes `&lt;exclamation.img&gt;` — see
		/// `Engine_UiMenuSources.Row.Unviewed`.
		/// </summary>
		public bool Unviewed;

		/// <summary>
		/// True when a template source put this row here rather than the template authoring it: it
		/// is replaced wholesale on restate. Bookkeeping for the resync, never a behaviour test.
		/// </summary>
		public bool Sourced;

		/// <summary>
		/// `UpdateAllGhosting`'s own answer for this row (an empty submenu draws dim), kept apart
		/// from <see cref="Ghosted"/> so a recompute cannot clobber the guest's statement.
		/// Cosmetic only: main.32 passes it to `SetGhosting`, which writes colours on `Icon.Title`,
		/// while `SelectMenuItem` (`expandablemenuinput` main.5 op 9-12) reads `item['Ghosted']` alone.
		/// </summary>
		public bool AutoGhosted;

		/// <summary>Whether the row draws dim, either reason. The press is refused on <see cref="Ghosted"/> only.</summary>
		public bool IsGhosted => Ghosted || AutoGhosted;
		public Engine_UiMenuConstants.Emphasis Emphasis = Engine_UiMenuConstants.Emphasis.None;
		/// <summary>
		/// `item.RatingSlide`, SpawnButton's ninth argument, bimodal by magnitude (`sub_826CE230`):
		/// below 100 it is twice the GDB `Rating` (0..10, a star slide) or 11 for item types that
		/// state none; 100 and above it is a five-digit base-10 pip word, one digit per augment
		/// slot, 1 empty and 2 filled. Decoded by <see cref="Engine_UiMenuFormatter.RatingFor"/>.
		/// </summary>
		public int RatingSlide = EmptyRatingSlide;
		public bool IsOpen;

		public Item? Parent;
		public readonly List<Item> Children = new();

		/// <summary>The parent's name as a template stated it; <see cref="Parent"/> is the link.</summary>
		public string? ParentName;

		/// <summary>
		/// <see cref="Emphasis"/> as the single flag the guest raises: `sub_826E2220` and
		/// `sub_826E1C98` both call `SetItemEmphasis(1)` (`PrimaryQuest`); nothing raises the others.
		/// </summary>
		public bool Emphasised
		{
			get => Emphasis != Engine_UiMenuConstants.Emphasis.None;
			set => Emphasis = value
				? Engine_UiMenuConstants.Emphasis.PrimaryQuest
				: Engine_UiMenuConstants.Emphasis.None;
		}

		/// <summary>The row's element ordinal in the guest's pool, or -1.</summary>
		public int Ordinal = -1;

		/// <summary>
		/// True once the spawn capture (`sub_826CE8D0`) has supplied this row's identity. A row
		/// without one must not be committed by name — it falls back to the A-press path, where the
		/// guest's Lua decides.
		/// </summary>
		public bool Committable;

		/// <summary>
		/// A screen this entry opens instead of dispatching — host routing, not menu content.
		/// "New Game" is one: activating it by name would consume the boy/girl answer unasked
		/// (slide 1 on `choosecard`, substate 2).
		/// </summary>
		public string? Opens;

		/// <summary>
		/// A host action to post instead of committing by identity, for an entry the guest has
		/// no `(EventType, id)` for — the pause menu's saving rows reach `sub_823DC888` this way.
		/// </summary>
		public string? Command;

		/// <summary>
		/// The guest's own spawn condition over a host-authored list ("Load Game" needs a save,
		/// "Language" more than one installed). False drops the entry from the visible list
		/// entirely rather than ghosting it.
		/// </summary>
		public bool Available = true;

		/// <summary>An entry the guest knows nothing about; activating it is complete host-side.</summary>
		public bool HostOnly;

		/// <summary>
		/// MOD CONTENT: a row a mod (or the player's templates file) put on this list, which the
		/// guest has no record for. It is never stated to the guest as its highlighted row: every
		/// list screen's C++ reads the highlight through `sub_826C1B50`, and a handler can feed
		/// the id to a lookup whose failure it ignores — the abilities edge `sub_826C3C78` drops
		/// `sub_82896900`'s result (0x826c3d58) and indexes with the unset Type (0x824e4f5c). The
		/// row is otherwise as stated: it activates, commits and runs row actions exactly as before.
		/// </summary>
		public bool FromMod;

		/// <summary>Whether this row may be stated to the guest as its highlighted row: neither host-only nor a mod's.</summary>
		public bool GuestHighlightable => !HostOnly && !FromMod;

		/// <summary>`GetMenuItemDepth` counts parent links; a root entry is depth 1.</summary>
		public int Depth
		{
			get
			{
				int depth = 0;
				for (Item? at = this; at?.Parent is not null; at = at.Parent)
				{
					++depth;
				}

				return depth;
			}
		}

		/// <summary>
		/// Headedness as the guest stated it, from the tab offset it wrote; null takes the shipped
		/// rule below. The guest can disagree with the tree shape: the world map's five category
		/// rows state offset −65, which `GetMenuItemTabOffset` produces only for a depth-1 row that
		/// is NOT a heading.
		/// </summary>
		public bool? HeadingOverride;

		/// <summary>`IsHeading(item)` — `item.ParentItem == g_Menu.MenuRoot`.</summary>
		public bool IsHeading =>
			HeadingOverride ?? (Parent is not null && Parent.Parent is null);
	}

	private Item _root = new() { Name = RootName, IsOpen = true };

	/// <summary>
	/// The root; never visible itself, its children are the top level. Swappable: `PushMenuState`
	/// replaces the whole menu with an empty one and `PopMenuState` puts the old one back.
	/// </summary>
	public Item MenuRoot => _root;

	/// <summary>`g_Menu.CurrentHighlightIdx` — 1-based into <see cref="Visible"/>. 0 when empty.</summary>
	public int CurrentHighlightIdx { get; private set; } = 1;

	private readonly List<Item> _visible = new();
	private bool _dirty = true;

	/// <summary>`VisibleInfo.VisibleElements`, in display order.</summary>
	public IReadOnlyList<Item> Visible
	{
		get
		{
			if (_dirty)
			{
				Recompute();
			}

			return _visible;
		}
	}

	/// <summary>The highlighted entry, or null when the list is empty.</summary>
	public Item? Highlighted =>
		CurrentHighlightIdx >= 1 && CurrentHighlightIdx <= Visible.Count
			? Visible[CurrentHighlightIdx - 1]
			: null;

	/// <summary>What `GetHighlightedMenuItemData` writes back to the guest.</summary>
	public readonly record struct Fired(string Name, int EventType, int IdLow, int IdHigh);

	/// <summary>Throws the visible list away; it is rebuilt on next read.</summary>
	public void DirtyLayout() => _dirty = true;

	/// <summary>
	/// `g_MenuFormatting:UpdateAllGhosting()` — recomputes the automatic half, over the whole tree
	/// rather than the visible window so a row scrolling in already knows its answer.
	///
	/// <code>
	/// for i in GetVisibleIndices() do
	///   item = g_Menu.VisibleInfo.VisibleElements[i]
	///   emptySubmenu = item.EventType == EVENT_TYPE_SUBMENU
	///                  and #item.ChildItems == 0
	///                  and g_Menu.AutomaticGhostingOnSubmenus == true
	///   self:SetGhosting(comp, item.Ghosted or emptySubmenu, item)
	/// end
	/// </code>
	/// </summary>
	public void UpdateAllGhosting()
	{
		// The rule needs the menu's full tree, so it applies only once `SpawnButton` has populated it.
		if (!KnowsItsOwnChildren)
		{
			return;
		}

		Recompute(MenuRoot);

		void Recompute(Item parent)
		{
			foreach (Item child in parent.Children)
			{
				child.AutoGhosted =
					child.EventType == (int)Engine_UiMenuConstants.EventType.Submenu &&
					child.Children.Count == 0 &&
					AutomaticGhostingOnSubmenus;

				Recompute(child);
			}
		}
	}

	/// <summary>`RemoveAllChildren` — clears the tree and puts the cursor back at 1.</summary>
	public void Clear()
	{
		KnowsItsOwnChildren = false;
		MenuRoot.Children.Clear();
		CurrentHighlightIdx = 1;
		VisibleStartIdx = 1;
		NumberOfItems = 0;
		_spawned.Clear();
		_dirty = true;
	}

	/// <summary>`g_Menu.VisibleStartIdx`, 1-based. Where the ten-row window begins.</summary>
	public int VisibleStartIdx { get; private set; } = 1;

	/// <summary>`g_Menu.NumberOfItems` — every item created, not every visible one.</summary>
	public int NumberOfItems { get; private set; }

	/// <summary>
	/// `g_Menu.UpDownInputDisabled`, set by `SetExpandableMenuDisabled` (the abilities blade raises
	/// it while its carousel is up). `onUpdate` checks it before reading the stick.
	/// </summary>
	public bool UpDownInputDisabled { get; private set; }

	/// <summary>`g_Menu:SetExpandableMenuDisabled(state)`.</summary>
	public void SetDisabled(bool disabled) => UpDownInputDisabled = disabled;

	/// <summary>
	/// `WaitForInput()` — true while a row is mid-slide (the script asks `g_AnimationList`; here the
	/// drawing surface sets it). <see cref="Select"/> is swallowed while true. `ANIMATE_TIME` = 0.15.
	/// </summary>
	public bool Animating { get; set; }

	/// <summary>
	/// Whether `SpawnButton` has populated the tree since the last <see cref="Clear"/>.
	/// </summary>
	public bool KnowsItsOwnChildren { get; private set; }

	/// <summary>`g_Menu.AutomaticGhostingOnSubmenus`, true until a screen turns it off.</summary>
	public bool AutomaticGhostingOnSubmenus { get; private set; } =
		Engine_UiMenuConstants.AutomaticGhostingOnSubmenusDefault;

	/// <summary>`TurnOffAutomaticGhostingOnSubmenus`.</summary>
	public void TurnOffAutomaticGhostingOnSubmenus() => AutomaticGhostingOnSubmenus = false;

	private readonly List<Item> _spawned = new();

	/// <summary>`g_Menu.SpawnedItems` — what the population put in, in spawn order.</summary>
	public IReadOnlyList<Item> SpawnedItems => _spawned;

	/// <summary>
	/// `g_Menu:GetLastItemIndex()` — `NumberOfItems - 1` (`expandablemenu.dis main.27` pc4..6, into
	/// `Float1`). `CreateMenuItem` stamps the current count then increments (`main.9` pc49..56), so
	/// the counter is one past the last index.
	/// </summary>
	public int GetLastItemIndex() => NumberOfItems - 1;

	/// <summary>One saved menu, as `PushMenuState` keeps it.</summary>
	private sealed record Backup(Item Root, int Highlight, List<Item> Spawned, int Items);

	private Backup? _backup;

	/// <summary>
	/// `g_Menu:PushMenuState()` — save the whole menu and start an empty one, for a screen opening a
	/// sub-list over its own. One level, not a stack, as the script has it.
	/// </summary>
	public void PushMenuState()
	{
		_backup = new Backup(MenuRoot, CurrentHighlightIdx, new List<Item>(_spawned),
			NumberOfItems);

		_root = new Item { Name = Engine_UiMenuConstants.RootName, IsOpen = true };
		NumberOfItems = 0;
		_spawned.Clear();
		CurrentHighlightIdx = 1;
		_dirty = true;
	}

	/// <summary>`g_Menu:PopMenuState()`. A pop with nothing pushed does nothing.</summary>
	public bool PopMenuState()
	{
		if (_backup is not { } saved)
		{
			return false;
		}

		_root = saved.Root;
		CurrentHighlightIdx = saved.Highlight;
		NumberOfItems = saved.Items;
		_spawned.Clear();
		_spawned.AddRange(saved.Spawned);
		_backup = null;
		_dirty = true;
		return true;
	}

	/// <summary>
	/// `g_Menu:UpdateAfterSpawning()`. The shipped body is empty (`expandablemenu.dis main.25` is
	/// one `RETURN`); it marks the end of a population, and the recompute below is ours.
	/// </summary>
	public void UpdateAfterSpawning()
	{
		_dirty = true;
		Recompute();
	}

	/// <summary>
	/// `g_Menu:SetRootTitle(title)`. The shipped body is empty (`expandablemenu.dis main.24`);
	/// keeping the title on the root is ours, for the folder header when a submenu is open.
	/// </summary>
	public void SetRootTitle(string title) => MenuRoot.StatedLabel = title;

	/// <summary>
	/// `CreateMenuItem(name)` then `AddChild(parent, item)`. A null parent attaches to the root,
	/// which is what the spawn handler's `String4 == "Root"` means.
	/// </summary>
	public Item Add(string name, Item? parent = null)
	{
		var item = new Item { Name = name, Parent = parent ?? MenuRoot };
		item.Parent!.Children.Add(item);
		_spawned.Add(item);
		++NumberOfItems;
		_dirty = true;
		return item;
	}

	/// <summary>`g_Menu:RemoveChild(name)` — detaches an item and everything under it; the recompute clamps the cursor.</summary>
	/// <summary>
	/// Drops <paramref name="item"/> and everything under it from the spawn list, for a caller
	/// holding the item rather than the name (names repeat across parents). `NumberOfItems` is left
	/// alone: it is the guest's spawn counter, not the list's length.
	/// </summary>
	internal void Forget(Item item)
	{
		_spawned.Remove(item);
		foreach (Item child in item.Children)
		{
			Forget(child);
		}

		item.Children.Clear();
		_dirty = true;
	}

	public bool Remove(string name)
	{
		if (Find(name) is not { Parent: { } parent } item)
		{
			return false;
		}

		parent.Children.Remove(item);
		_spawned.Remove(item);
		_dirty = true;
		return true;
	}

	/// <summary>
	/// `g_Menu:SpawnButton()` — the population's one entry point. The C++ writes eight attributes on
	/// the presentation's Parameters element and runs this as a Lua string; the host reaches it with
	/// the same eight captured at `sub_826CE8D0`.
	///
	/// <code>
	/// String1 name   String2 textTag   String3 icon   String4 parent ("Root" = top level)
	/// Float1  idLow  Float2  idHigh    Float3  eventType   Float4 ratingSlide
	///
	/// local item = g_Menu:CreateMenuItem(name, textTag, icon, Float3, Float1, Float2, Float4)
	/// if parent == 'Root' then parent = nil end
	/// g_Menu:AddChild(item, parent)
	/// table.insert(g_Menu.SpawnedItems, name)
	/// </code>
	///
	/// `CreateMenuItem` takes the event type FOURTH, before the two id halves — not the order the
	/// attributes are read in.
	/// </summary>
	public Item SpawnButton(string name, string textTag, string iconTexture, int eventType,
		int idLow, int idHigh, int ratingSlide, string parentName)
	{
		// `if parent == 'Root' then parent = nil end`; an unknown parent name falls to the root too,
		// so a bad name costs one row's nesting rather than the whole list.
		Item? parent = string.IsNullOrEmpty(parentName) ||
			string.Equals(parentName, RootName, System.StringComparison.Ordinal)
				? null
				: Find(parentName);

		KnowsItsOwnChildren = true;

		Item item = Add(name, parent);

		// The sentinels survive an empty argument: `CreateMenuItem` applies them as
		// `textTag or '_no tag_'` / `iconTexture or '_no texture_'` (`expandablemenu.dis main.9`
		// pc13..20), so no caller can produce a row with nothing to draw.
		if (textTag.Length != 0)
		{
			item.TextTag = textTag;
		}

		if (iconTexture.Length != 0)
		{
			item.IconTexture = iconTexture;
		}

		item.EventType = eventType;
		item.IdLow = idLow;
		item.IdHigh = idHigh;
		item.RatingSlide = ratingSlide;
		item.Committable = true;
		return item;
	}

	/// <summary>`GetMenuItem(name)` — the first entry with that name, anywhere in the tree.</summary>
	public Item? Find(string name)
	{
		foreach (Item item in Walk(MenuRoot))
		{
			if (string.Equals(item.Name, name, System.StringComparison.Ordinal))
			{
				return item;
			}
		}

		return null;
	}

	/// <summary>
	/// The visible entry <paramref name="name"/> names — by its name, else by its stated label. Of
	/// several, the one whose event type and full id (`(IdHigh &lt;&lt; 16) | IdLow`) are
	/// <paramref name="type"/> / <paramref name="id"/>, else the first. Null when none is visible.
	/// </summary>
	public Item? VisibleNamed(string name, int type, uint id)
	{
		foreach (bool byLabel in new[] { false, true })
		{
			Item? first = null;
			foreach (Item row in Visible)
			{
				if (!string.Equals(byLabel ? row.StatedLabel : row.Name, name, System.StringComparison.Ordinal))
				{
					continue;
				}

				if (row.EventType == type && (((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu)) == id)
				{
					return row;
				}

				first ??= row;
			}

			if (first is not null)
			{
				return first;
			}
		}

		return null;
	}

	/// <summary>`GetMenuItemVisibleIdx(name)` — 1-based, or 0 when the entry is not visible.</summary>
	public int VisibleIndexOf(Item item)
	{
		for (int i = 0; i < Visible.Count; ++i)
		{
			if (ReferenceEquals(Visible[i], item))
			{
				return i + 1;
			}
		}

		return 0;
	}

	/// <summary>
	/// `g_Menu:ScrollUp()`, true when the cursor moved. Clamped at 1, not wrapped
	/// (`if 1 &lt; self.CurrentHighlightIdx`); the event push and slide belong to the drawing side.
	/// </summary>
	public bool ScrollUp()
	{
		if (UpDownInputDisabled)
		{
			return false;
		}

		if (CurrentHighlightIdx <= 1)
		{
			return false;
		}

		--CurrentHighlightIdx;
		return true;
	}

	/// <summary>`g_Menu:ScrollDown()`. Clamped at the count, not wrapped.</summary>
	public bool ScrollDown()
	{
		if (UpDownInputDisabled)
		{
			return false;
		}

		if (CurrentHighlightIdx >= Visible.Count)
		{
			return false;
		}

		++CurrentHighlightIdx;
		return true;
	}

	/// <summary>Puts the cursor on a 1-based index, clamped into the list.</summary>
	public void Highlight(int index)
	{
		CurrentHighlightIdx = Visible.Count == 0
			? 1
			: System.Math.Clamp(index, 1, Visible.Count);
	}

	/// <summary>
	/// `g_Menu:SelectMenuItem()` — the A press. Returns what the guest would have pushed, or null
	/// when the row is ghosted: the test returns before the push and the toggle, so a ghosted
	/// category cannot even be opened.
	/// </summary>
	public Fired? Select()
	{
		// `if self:WaitForInput() then return end` — the first line of `SelectMenuItem`: A is
		// swallowed for the length of a slide.
		if (Animating)
		{
			return null;
		}

		// `if item.Ghosted then return end`, before anything is pushed. The cursor may still sit on
		// the row. `Ghosted`, not `IsGhosted`: the auto-ghost is a colour, not a refusal, and a
		// category the guest fills on demand must stay enterable.
		if (Highlighted is not { } item || item.Ghosted)
		{
			return null;
		}

		var fired = new Fired(item.Name, item.EventType, item.IdLow, item.IdHigh);

		if (item.Children.Count > 0)
		{
			item.IsOpen = !item.IsOpen;
			_dirty = true;

			// `FireExpandMenuItemEvent` on the way OPEN, `FireCollapseMenuItemEvent` on the way
			// shut (`expandablemenuinput.dis main.5 pc29..38`), and the expand handler's FIRST
			// act is `AutoCollapse` — if it closed something, the handler RETURNS and the
			// cursor does not move (`expandablemenuformatting.dis main.7 pc8..13`).
			if (item.IsOpen)
			{
				if (Engine_UiMenuFormatter.AutoCollapse(this, item) is { } other)
				{
					// The row stays open: `SelectMenuItem` toggles `IsOpen` at `main.5 pc26..28`,
					// before the expand event at pc32..34, so the early return in
					// `onExpandMenuItem` skips only the cursor step and the animate-in.
					CollapseAndHighlight(other);
					return fired;
				}

				// `g_Menu.CurrentHighlightIdx = CurrentHighlightIdx + 1` then
				// `onScrollDown(nil, true)` (`main.7 pc14..22`) — opening a folder puts the
				// cursor on its FIRST CHILD, it does not leave it on the header.
				CurrentHighlightIdx += 1;
			}
		}

		return fired;
	}

	/// <summary>
	/// `CollapseMenuAndHighlight(idx, true)` — shut one open row and move the cursor. The `true` arm
	/// is the only one `AutoCollapse` takes; the `false` arm (cursor to the collapsed row's own
	/// index) is what `CloseMenuItem` wants instead. `expandablemenuformatting.dis main.39`:
	///
	/// <code>
	/// pc5-7   childCount = #VisibleElements[idx].ChildItems
	/// pc51    TEST R2            -- the flag; false jumps to pc68
	/// pc55    LT   idx &lt; hl      -- true falls through to the subtract
	/// pc60    hl = hl - childCount
	/// pc65    hl = hl + 1
	/// pc68-69 (flag false) hl = idx
	/// </code>
	///
	/// The `true` arm (`main.6 pc14 LOADBOOL R10 = True`) compensates for the rows that vanished
	/// above the cursor and then steps once; a wrong cursor shifts the whole visible window.
	/// </summary>
	public void CollapseAndHighlight(Item row)
	{
		int collapsed = VisibleIndexOf(row);
		int children = row.Children.Count;

		row.IsOpen = false;
		_dirty = true;

		if (collapsed >= 1 && collapsed < CurrentHighlightIdx)
		{
			CurrentHighlightIdx -= children;
		}

		CurrentHighlightIdx += 1;
	}

	/// <summary>
	/// `g_Menu:CloseMenuItem()` — the B press. Closes the highlighted row's parent and moves the
	/// cursor onto it; a row at the root falls through to whatever else answers B.
	///
	/// Must return false when nothing was closed: `expandablemenuinput.dis main.6` walks backwards
	/// from `CurrentHighlightIdx - 1` under `pc11 LT 0 &lt; R2` and falls out at pc27 having touched
	/// neither `IsOpen` nor the cursor, and a true answer means "B was consumed", which would keep
	/// the press from ever reaching `ScreenAction(37)`.
	/// </summary>
	public bool CloseParent()
	{
		if (Highlighted is not { } item || item.Parent is null || item.Parent == MenuRoot)
		{
			return false;
		}

		int index = VisibleIndexOf(item.Parent);
		if (index < 1)
		{
			return false;
		}

		item.Parent.IsOpen = false;
		_dirty = true;
		Highlight(index);
		return true;
	}

	/// <summary>
	/// The window the formatter shows, as 1-based inclusive display indices: the cursor pinned to
	/// slot <see cref="HighlightSlot"/> with at most two entries above it, at most
	/// <see cref="NumVisible"/> rows. Shared with the view.
	/// </summary>
	public (int First, int Last) Window() => Window(CurrentHighlightIdx, Visible.Count);

	/// <summary>The slot a still list's top row stands in: the first a player can see (slot 1 is the top gutter).</summary>
	public const int StillTopSlot = 2;

	/// <summary>
	/// A STILL LIST'S WINDOW (the view's <c>StillRows</c>): the first row of the window, 1-based, once the
	/// cursor is at <paramref name="highlight"/> of <paramref name="total"/> rows. The window holds
	/// <see cref="NumVisible"/> rows and does not move while the cursor stays inside it; it moves only
	/// as far as it must to take the cursor in, and never past the last row. So a list that fits never
	/// moves, and no cursor move inside the window puts another row under a still pointer.
	/// </summary>
	public static int StillTop(int top, int highlight, int total) => StillTop(top, highlight, total, NumVisible);

	/// <summary><see cref="StillTop(int, int, int)"/> for a window of <paramref name="rows"/> rows (at least one).</summary>
	public static int StillTop(int top, int highlight, int total, int rows)
	{
		rows = System.Math.Max(1, rows);
		if (highlight < top)
		{
			top = highlight;
		}
		else if (highlight > top + rows - 1)
		{
			top = highlight - rows + 1;
		}

		return System.Math.Clamp(top, 1, System.Math.Max(1, total - rows + 1));
	}

	/// <summary>The display index a still list's window centres the slot rule on: its top row in <see cref="StillTopSlot"/>.</summary>
	public static int StillCentre(int top) => StillCentre(top, StillTopSlot);

	/// <summary>The display index the slot rule centres on for a still window whose top row stands in <paramref name="topSlot"/>.</summary>
	public static int StillCentre(int top, int topSlot) => top + HighlightSlot - topSlot;

	/// <summary>
	/// THE SLOTS A STILL LIST STANDS IN: its top slot and how many rows its window holds. With no band
	/// (<paramref name="bandBottom"/> / <paramref name="bandTop"/> null, canvas y up) the window is
	/// <see cref="NumVisible"/> rows from <see cref="StillTopSlot"/>. With a band — what the screen draws
	/// over the list: a tab strip above, a description or the prompts below — the window starts at the
	/// first slot from <see cref="StillTopSlot"/> whose row stands inside the band and holds every slot
	/// after it that does, up to the last of <paramref name="slots"/>. A row is inside when its height
	/// (<paramref name="slotY"/>, 1-based slot) lies in [bottom, top], the view's clip test, so no row of
	/// the window is ever cut, and the cursor, which walks the window, is never under what is drawn over
	/// the list. A band no slot stands in keeps one row, in the first slot below its top.
	/// </summary>
	public static (int TopSlot, int Rows) StillSlots(System.Func<int, float> slotY, int slots, float? bandBottom, float? bandTop)
	{
		if (bandBottom is not { } bottom || bandTop is not { } top)
		{
			return (StillTopSlot, System.Math.Min(NumVisible, slots - StillTopSlot + 1));
		}

		int first = StillTopSlot;
		while (first < slots && slotY(first) > top)
		{
			++first;
		}

		int rows = 0;
		for (int slot = first; slot <= slots; ++slot)
		{
			float y = slotY(slot);
			if (y > top || y < bottom)
			{
				break;
			}

			++rows;
		}

		return (first, System.Math.Max(1, rows));
	}

	/// <summary>The window rule itself, for a caller that holds the two numbers already.</summary>
	public static (int First, int Last) Window(int highlight, int total)
	{
		if (total <= 0)
		{
			return (1, 0);
		}

		// At most `HighlightSlot - 2` entries sit above the cursor: slot 1 is the top gutter, so
		// the first row a player can see is slot 2 and the cursor is pinned to slot 4.
		const int Above = HighlightSlot - 2;

		int first = System.Math.Max(highlight - Above, 1);
		int span = total - first + 1;
		int cap = NumVisible - (Above - (highlight - first));
		int count = System.Math.Min(span, cap);
		return (first, first + count - 1);
	}

	/// <summary>
	/// `CountVisibleChildren` — flattens the tree, descending only into open entries, so a collapsed
	/// category contributes one row. The guest's highlight index indexes the VISIBLE list, not the entries.
	/// </summary>
	private void Recompute()
	{
		_visible.Clear();
		Collect(MenuRoot);
		_dirty = false;

		if (_visible.Count == 0)
		{
			CurrentHighlightIdx = 1;
		}
		else if (CurrentHighlightIdx > _visible.Count)
		{
			CurrentHighlightIdx = _visible.Count;
		}
		else if (CurrentHighlightIdx < 1)
		{
			CurrentHighlightIdx = 1;
		}
	}

	private void Collect(Item parent)
	{
		foreach (Item child in parent.Children)
		{
			// A deliberate deviation: `CountVisibleChildren` (`expandablemenu.dis main.0.0`) has no
			// availability test — the guest decides existence at spawn time (`sub_826E2220` simply
			// does not call `CreateMenuItem`). A host-authored list applies the condition here
			// instead, so an unavailable entry is absent rather than ghosted.
			if (!child.Available)
			{
				continue;
			}

			_visible.Add(child);
			if (child.IsOpen)
			{
				Collect(child);
			}
		}
	}

	private static IEnumerable<Item> Walk(Item from)
	{
		foreach (Item child in from.Children)
		{
			yield return child;
			foreach (Item deeper in Walk(child))
			{
				yield return deeper;
			}
		}
	}

	/// <summary>One line for the run — the rows as the model holds them, and where the cursor is.</summary>
	public string Describe()
	{
		var rows = new List<string>();
		for (int i = 0; i < Visible.Count; ++i)
		{
			Item item = Visible[i];
			string label = item.StatedLabel ?? item.TextTag;
			rows.Add($"{i + 1}{(i + 1 == CurrentHighlightIdx ? "*" : string.Empty)}=" +
				$"{(label.Length == 0 ? "<EMPTY>" : label)}" +
				$"{(item.Ghosted ? " ghosted" : string.Empty)}" +
				$"{(item.Children.Count == 0 ? string.Empty : item.IsOpen ? " open" : " closed")}");
		}

		(int first, int last) = Window();
		return $"{Visible.Count} visible, cursor {CurrentHighlightIdx}, window {first}..{last}" +
			(rows.Count == 0 ? string.Empty : $": {string.Join(", ", rows)}");
	}
}
