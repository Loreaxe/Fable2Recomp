using System;
using System.Collections.Generic;

using Godot;

namespace Refii.Ui;

/// <summary>
/// The UI's data model — the shapes every menu, screen, surface and HUD is written against.
/// </summary>
/// <remarks>
/// The bridge PRODUCES these; it does not own them. Each is a plain record over its own fields, so
/// a live guest or an editor template can fill the same shape and the UI cannot tell which.
/// Nested under one type because <c>Presentation</c>, <c>Language</c>, <c>MenuRow</c> and
/// <c>UiElement</c> are names a UI namespace will want again.
/// </remarks>
public static class Engine_UiModel
{
	/// <summary>The live value of one attribute on one element, as the running game holds it.</summary>
	/// <summary>What kind of value a `GUI` native was passed.</summary>
	/// <remarks>
	/// <c>Other</c> is a table or a function — `DisplayMenuBox`'s option list and the info-box
	/// params form both arrive that way; the shape is recorded even where the contents are not walked.
	/// </remarks>
	public enum GuiNativeArgumentKind : byte
	{
		None = 0,
		Number = 1,
		Boolean = 2,
		String = 3,
		Other = 4,

		/// <summary>A Lua table, walked into <see cref="GuiNativeArgument.Fields"/>.</summary>
		/// <remarks>
		/// Two natives use it, both by name only: `DisplayInfoBoxParams` (eleven keys) and a
		/// `DisplayMenuBox` option (<c>TextTag</c>, <c>Enabled</c>). Neither reads an integer key
		/// and neither nests.
		/// </remarks>
		Table = 5,
	}

	/// <summary>One argument to an intercepted `GUI` native, as the Lua stack held it.</summary>
	/// <remarks>
	/// Everything crosses as text; the payload formats numbers itself. <c>lua_Number</c> is
	/// <c>float</c> in this title, not double. A string is a Babel TAG
	/// (<c>TEXT_QUEST_COMPLETE</c>), not display text; the host resolves it.
	/// </remarks>
	public readonly record struct GuiNativeArgument(
		GuiNativeArgumentKind Kind, string Text,
		System.Collections.Generic.IReadOnlyDictionary<string, GuiNativeArgument>? Fields = null)
	{
		/// <summary>One named entry of a table argument, or an absent one.</summary>
		/// <remarks>
		/// Absent is not false, and the guest's defaults are not uniform: <c>ShowAButton</c> and
		/// <c>ShowYButton</c> default to TRUE, every other boolean in the same table to false. State
		/// the default rather than lean on <see cref="Boolean"/>, which reads an absent field as nil.
		/// </remarks>
		public GuiNativeArgument Field(string name) =>
			Fields is not null && Fields.TryGetValue(name, out GuiNativeArgument field)
				? field
				: new GuiNativeArgument(GuiNativeArgumentKind.None, string.Empty);

		/// <summary>A table field's boolean, or <paramref name="fallback"/> when it is absent.</summary>
		public bool FieldBoolean(string name, bool fallback) =>
			Fields is not null && Fields.TryGetValue(name, out GuiNativeArgument field) &&
			field.Kind == GuiNativeArgumentKind.Boolean
				? field.Text == "1"
				: fallback;

		/// <summary>A table field's string, or <paramref name="fallback"/> when it is absent.</summary>
		public string FieldText(string name, string fallback) =>
			Fields is not null && Fields.TryGetValue(name, out GuiNativeArgument field) &&
			field.Kind == GuiNativeArgumentKind.String && field.Text.Length > 0
				? field.Text
				: fallback;

		/// <summary>A table field's number, or <paramref name="fallback"/> when it is absent.</summary>
		public float FieldNumber(string name, float fallback) =>
			Fields is not null && Fields.TryGetValue(name, out GuiNativeArgument field) &&
			field.Kind == GuiNativeArgumentKind.Number
				? field.Number
				: fallback;

		/// <summary>The value as a number, or 0 when it was not one.</summary>
		public float Number =>
			Kind == GuiNativeArgumentKind.Number &&
			float.TryParse(Text, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float value)
				? value
				: 0f;

		/// <summary>Lua truth — false only for nil and <c>false</c>.</summary>
		public bool Boolean => Kind switch
		{
			GuiNativeArgumentKind.Boolean => Text == "1",
			GuiNativeArgumentKind.None => false,
			_ => true,
		};
	}

	/// <summary>One `GUI.*` native call — the call itself, not its result.</summary>
	/// <remarks>
	/// The game VM calls ~120 semantic natives (<c>DisplayMessageBox</c>, <c>FadeScreenIn</c>,
	/// <c>SetCounter</c>). See docs/ui_native_cut.md.
	/// </remarks>
	public readonly record struct GuiNativeCall(
		string Name,
		System.Collections.Generic.IReadOnlyList<GuiNativeArgument> Arguments)
	{
		/// <summary>Argument <paramref name="index"/>, or an absent one past the end.</summary>
		/// <remarks>
		/// The guest does not check arity either: its thunks test
		/// <c>((L->top - L->base) &amp; ~7) >= 8*N</c> inline and a missing argument reads as
		/// 0 / false / empty, so asking past the end answers rather than throws.
		/// </remarks>
		public GuiNativeArgument Argument(int index) =>
			Arguments is { } list && index >= 0 && index < list.Count
				? list[index]
				: new GuiNativeArgument(GuiNativeArgumentKind.None, string.Empty);
	}

	/// <summary>
	/// One `g_Menu:` mutation the game's C++ made — the chunk it ran and what it staged for it.
	/// </summary>
	/// <remarks>
	/// The C++ stages attributes onto the `Parameters` element and runs a one-line Lua chunk that
	/// reads them back; every mutation goes through that pair and the 28 `g_Menu:` literals in the
	/// image are the whole vocabulary.
	///
	/// Key on `(Presentation, Chunk)`, never the chunk alone: a second
	/// `SetItemEmphasis`/`SetItemGhosting` exists for the non-expandable menu, treating `Float1` as
	/// a boolean rather than the 0..3 emphasis enum and naming its field `Ghosting`.
	///
	/// Some chunks carry their argument in the text — `SetInitiallySelectedItem(N)` is
	/// concatenated, `RepopulateMenuFromPopulationScript('name')` sprintf'd,
	/// `SetExpandableMenuDisabled` ships as two literals — so parse the chunk as well as the bag.
	/// </remarks>
	public readonly record struct MenuChunk(
		uint Presentation,
		string Chunk,
		System.Collections.Generic.IReadOnlyDictionary<string, string> Staged)
	{
		/// <summary>The chunk's method name — `SpawnButton`, `SetItemGhosting`, …</summary>
		/// <remarks>
		/// Everything between `g_Menu:` (or `g_MenuFormatter:`) and the opening bracket; the two
		/// receivers name disjoint methods, so the method alone is unambiguous.
		/// </remarks>
		public string Method
		{
			get
			{
				int colon = Chunk.IndexOf(':');
				if (colon < 0)
				{
					return string.Empty;
				}

				int open = Chunk.IndexOf('(', colon);
				return open < 0
					? Chunk[(colon + 1)..].Trim()
					: Chunk[(colon + 1)..open].Trim();
			}
		}

		/// <summary>The text inside the brackets, for the chunks that bake their argument in.</summary>
		public string Inline
		{
			get
			{
				int open = Chunk.IndexOf('(');
				int close = Chunk.LastIndexOf(')');
				return open >= 0 && close > open
					? Chunk[(open + 1)..close].Trim().Trim('\'', ' ')
					: string.Empty;
			}
		}

		/// <summary>A staged attribute, or empty when this chunk did not stage it.</summary>
		public string Attribute(string name) =>
			Staged is { } staged && staged.TryGetValue(name, out string? value)
				? value
				: string.Empty;

		/// <summary>A staged attribute as a number — the `Float*` slots.</summary>
		public float Number(string name) =>
			float.TryParse(Attribute(name), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float value)
				? value
				: 0f;
	}

	/// <summary>The GUI modal gate: underlying screen id, pending modal id and MODE, tab.</summary>
	/// <remarks>
	/// Screen id 0 is the pause ROOT — the window in which the owned menu may stand and tab
	/// selection is accepted; the modal openers save and restore it, so it names the UNDERLYING
	/// screen whatever box is up. ModalMode is the guest's latch at guiOwner+40: 1 idle,
	/// 2 MessageBox, 3 QueryBox, 5 the numeric adjuster. Null when the GUI object is not up.
	/// </remarks>
	public readonly record struct PauseState(
		uint ScreenId, uint ModalId, uint ModalMode, uint CurrentTab)
	{
		/// <summary>
		/// Every predicate here keys on the MODE, never the id: query id 0 is a real box (the shop's
		/// tattoo notice, real estate's buy-denied), so `ModalId != 0` reads a live box as idle.
		/// </summary>
		public bool RootActive => ScreenId == 0 && ModalMode == 1;

		/// <summary>A QueryBox-mode (two-choice) dialog is pending.</summary>
		public bool QueryBoxUp => ModalMode == 3;

		/// <summary>A MessageBox-mode (dismiss) dialog is pending.</summary>
		public bool MessageBoxUp => ModalMode == 2;

		/// <summary>The numeric adjuster is pending.</summary>
		public bool AdjusterUp => ModalMode == 5;

		/// <summary>Any modal dialog is pending.</summary>
		public bool ModalUp => ModalMode != 1;
	}

	/// <summary>
	/// The Lua-opened MenuBox's model — the guest's option list (the bed's sleep menu, the
	/// world map's destination stop, DLC prompts). Null when no box is open.
	/// </summary>
	/// <remarks>
	/// The labels are not here: they are live element text on `Multiplayer_Button{i+1}`.
	/// </remarks>
	public readonly record struct MenuBoxState(
		int Count, int Selected, bool Cancelable, bool Cancelled, uint EnabledMask, bool GamerCard = false)
	{
		public bool IsEnabled(int index) =>
			index >= 0 && index < Count && (EnabledMask & (1u << index)) != 0;
	}

	/// <summary>
	/// One pause-map quest marker as the guest names it to its placer (`sub_826A3FD8`): the
	/// `Foreground.ReusableTokens` entry it draws (`Quest`, `QuestExit`, `QuestGiver`) and the
	/// world point it projects, in the guest's coordinates.
	/// </summary>
	public readonly record struct MapMarker(string Token, float X, float Y, float Z);

	/// <summary>Which of the guest's front-end screens is up, and its state.</summary>
	public enum FrontEndScreen { None = 0, Title = 1, MainMenu = 2 }

	/// <summary>
	/// The in-game screen IDs the guest's own table assigns. <c>sub_8281F620</c> builds it as a
	/// <c>std::map&lt;int, std::string&gt;</c> of ID to <c>.bgf</c> file name, thirty entries,
	/// IDs 0..29. Only the two verified in the disassembly are named here:
	///
	/// <code>
	///   addi r4, r11, aPausemainmenuB   # "PauseMainMenu.bgf"
	///   li   r10, 0                     # key 0
	///   ...
	///   addi r4, r8, aPhotomodescree    # "PhotomodeScreen.bgf"
	///   li   r7, 1                      # key 1
	/// </code>
	///
	/// The rest are absent rather than guessed: a presentation's own screen comes from its file
	/// name through the registry walk; this enum only says which one the guest considers CURRENT.
	/// </summary>
	public enum InGameScreen
	{
		/// <summary><c>PauseMainMenu.bgf</c>.</summary>
		PauseMenu = 0,

		/// <summary><c>PhotomodeScreen.bgf</c>.</summary>
		PhotoMode = 1,

		/// <summary>
		/// The guest's own "nothing is up", and its initial value: the screen manager is constructed
		/// with 31 at manager+152 (<c>*(_DWORD *)(v44 + 152) = 31</c>), one past the thirty entries.
		/// </summary>
		None = 31,
	}

	/// <summary>
	/// Which in-game screen the guest has up, STATED rather than inferred — the registry walk
	/// cannot answer it, because the pause menu's presentation is built at game entry and never
	/// freed. <see cref="InGameScreen.None"/> (31) means no screen is up; null means there is no
	/// screen manager yet.
	/// </summary>
	/// <summary>
	/// The guest's screen manager: which screen is current, and which presentations it owns.
	/// </summary>
	/// <param name="Current">
	/// The current screen, <see cref="InGameScreen.None"/> when nothing is up.
	/// </param>
	/// <param name="Owned">
	/// Presentation addresses the screen manager holds, mapped to the screen ID holding them. A
	/// presentation not in here — the HUD above all — is not governed by <paramref name="Current"/>.
	/// </param>
	public readonly record struct InGameScreens(
		InGameScreen Current,
		System.Collections.Generic.IReadOnlyDictionary<uint, int> Owned)
	{
	}

	/// <summary>One achievement write the guest has made this session.</summary>
	/// <param name="UserIndex">The pad slot that earned it, as the XGI write states.</param>
	/// <param name="AchievementId">The title's own id, as the XDBF numbers it.</param>
	/// <param name="Sequence">
	/// Monotonic. The seam keeps every write of the session stamped, so the host holds N and
	/// asks "what is new since N"; the read is safe to repeat, unlike a pop-style queue.
	/// </param>
	/// <param name="Binding">The serial of the writing user's binding (<see cref="AchievementBinding"/>), 0 when never bound.</param>
	public readonly record struct AchievementUnlock(uint UserIndex, uint AchievementId, ulong Sequence, ulong Binding = 0);

	/// <summary>
	/// A guest adoption point: user <paramref name="User"/> plays the hero of <paramref name="Save"/>; an
	/// empty save is a hero no save holds yet (a new game, a new hero). Serials count up from 1.
	/// </summary>
	public readonly record struct AchievementBinding(ulong Serial, uint User, string Save);

	/// <summary>One spawned menu row's identity, as the guest handed it to `SpawnButton`.</summary>
	public readonly record struct MenuRow(
		string Name, string Parent, uint Id, int EventType, int Rating, string Label)
	{
		/// <summary>`String3`, the icon path the spawn states (SpawnButton's r6).</summary>
		public string Icon { get; init; } = string.Empty;

		/// <summary>The guest's mark on the row (0 none .. 3): `SetItemEmphasisByIndex` inside the row
		/// builder, `SetItemEmphasis` by name otherwise.</summary>
		public int Emphasis { get; init; }

		/// <summary>The inventory item the row stands for, or null: what the row builder `sub_826CE230`
		/// was handed, or the item its ticket was filed against (RefiiHostGetMenuRowItem).</summary>
		public MenuRowItem? Item { get; init; }

		/// <summary>
		/// `String4 == "Root"` is the spawn convention for a top-level entry (`sub_82430468` passes
		/// the literal into the `String4` position). `String3`, one slot earlier, is the icon.
		/// </summary>
		public bool IsRoot =>
			Parent.Length == 0 ||
			string.Equals(Parent, "Root", System.StringComparison.Ordinal);
	}

	/// <summary>
	/// An item row's item, as its row builder was handed it: the GDB record, the item instance (its guest
	/// address), and — when <see cref="Counted"/> — the count, the category and the viewed byte the builder
	/// read (`+0xC`, `+0x10`, `+0x14` of the item).
	/// </summary>
	public readonly record struct MenuRowItem(uint Record, uint Instance, int Count, int Category, bool Viewed, bool Counted)
	{
		/// <summary>The export's five words {flags, record, item, count, category}; null for a row with no item.</summary>
		public static MenuRowItem? FromWords(uint flags, uint record, uint item, uint count, uint category) =>
			(flags & 1u) == 0u
				? null
				: new MenuRowItem(record, item, (int)count, (int)category, (flags & 2u) != 0u, (flags & 4u) != 0u);
	}

	/// <summary>
	/// The screen fade the guest is running, as a colour to draw. The guest owns the state end to
	/// end — it starts the fade and advances it at 15 Hz, and `CmdLevelChange_Update` and
	/// `CmdLoadSaveGame_Update` block on it, so advancing or snapping the progress here would hang
	/// a level change. The host owns only the pixels.
	/// </summary>
	/// <param name="Object">The fade object, or 0 when the GUI object does not exist yet.</param>
	/// <param name="Locked">`LockScreenFade` — the only escape hatch `CmdLevelChange_Update` has.</param>
	public readonly record struct ScreenFade(
		uint Object, Color Colour, float Progress, bool Locked)
	{
		/// <summary>True once the fade object resolved; an ALPHA-ZERO fade is still known.</summary>
		public bool Known => Object != 0u;

		/// <summary>
		/// Whether there is anything to draw. "Faded in" is alpha ZERO — `FadeScreenIn` targets
		/// `byte_83497BF4`, four zero bytes — and the guest's presentation early-outs on that.
		/// </summary>
		public bool Covers => Colour.A > 0f;
	}

	/// <summary>
	/// The five d-pad will slots, what may go on each, and whether an assigner is up.
	/// <paramref name="Manager"/> is 0 until the hero has a CECSpellManager; before that the slots
	/// and offers are unknown rather than empty.
	/// </summary>
	public readonly record struct SpellAssignState(
		uint Manager,
		int[] Assigned,
		uint[] Offered,
		int AbilitiesMode,
		bool WheelOpen,
		bool WheelOpenPlayerTwo)
	{
		/// <summary>How many d-pad slots there are — the guest's own loop bound.</summary>
		public const int Slots = 5;

		/// <summary>How many wills exist — likewise.</summary>
		public const int Spells = 8;

		/// <summary>True once the hero's manager resolved and the tables mean something.</summary>
		public bool Known => Manager != 0;

		/// <summary>The will on a slot, or null when the slot is empty.</summary>
		///
		/// <remarks>
		/// Null-safe on the arrays: a `default` of this struct has none, and a widget is asked for
		/// its cursor before the first sample arrives.
		/// </remarks>
		public int? On(int slot) =>
			Assigned is not null && slot >= 0 && slot < Assigned.Length && Assigned[slot] >= 0
				? Assigned[slot]
				: null;

		/// <summary>Whether a slot may be given a will — the guest's two-plane offering test.</summary>
		public bool Offers(int slot, int spell) =>
			Offered is not null && slot >= 0 && slot < Offered.Length &&
			spell >= 0 && spell < Spells &&
			(Offered[slot] & (1u << spell)) != 0;

		/// <summary>The wills a slot may hold, in index order.</summary>
		public System.Collections.Generic.List<int> Choices(int slot)
		{
			var choices = new System.Collections.Generic.List<int>();
			for (int spell = 0; spell < Spells; ++spell)
			{
				if (Offers(slot, spell))
				{
					choices.Add(spell);
				}
			}

			return choices;
		}

		/// <summary>True when any slot has something to offer — the guest's own "show me" gate.</summary>
		public bool Any
		{
			get
			{
				foreach (uint mask in Offered ?? System.Array.Empty<uint>())
				{
					if (mask != 0)
					{
						return true;
					}
				}

				return false;
			}
		}
	}

	/// <summary>
	/// The quick spell wheel's CURSOR, as the guest's own `SpellAssigning` widget holds it: the
	/// row the gold box is on, the cell each row's carousel sits at, and each row's compacted
	/// list of offered wills. The guest moves these itself (`sub_826B5430`, fed the live stick by
	/// the pump); the host draws them. Default (<see cref="Known"/> false) while the wheel is
	/// closed.
	/// </summary>
	public readonly record struct SpellWheelState(
		uint Widget, int Row, int MaxRow, int[] Counts, int[] Cells, int[][] Lists)
	{
		public bool Known => Widget != 0;

		/// <summary>The row's current cell, clamped into its list; -1 for an empty row.</summary>
		public int CellOf(int row) =>
			Known && row >= 0 && row < Counts.Length && Counts[row] > 0
				? System.Math.Clamp(Cells[row], 0, Counts[row] - 1)
				: -1;

		/// <summary>The will at a row's cell, or null.</summary>
		public int? WillAt(int row, int cell) =>
			Known && row >= 0 && row < Lists.Length && cell >= 0 && cell < Lists[row].Length
				? Lists[row][cell]
				: null;
	}

	/// <summary>
	/// The front end's own storage criteria. <paramref name="Count"/> is -1 until the tick pump has
	/// sampled, which is not zero: a menu built while it is -1 must not conclude "no saves".
	/// </summary>
	public readonly record struct SaveState(bool SignedIn, int Count, int Limit)
	{
		/// <summary>True once the pump has answered at all.</summary>
		public bool Known => Count >= 0;

		/// <summary>
		/// The guest's Load Game spawn guard at 0x826E2264..0x826E2278: signed in, and
		/// `sub_82443308()` non-zero.
		/// </summary>
		public bool CanLoad => SignedIn && Count > 0;

		/// <summary>
		/// The guest's New Game slot cap at 0x826E0148 (unsigned compare); the limit is 6 once the
		/// front end has entered — `sub_82378618` stores it over the image's 150.
		/// </summary>
		public bool AtSaveLimit => Known && Limit > 0 && Count >= Limit;
	}

	/// <summary>One installed language, as the guest's own table holds it.</summary>
	public readonly record struct Language(int Index, string Name, bool IsCurrent);

	/// <summary>One live Gameface presentation: where it is, its creation serial (a later one is
	/// larger; 0 for an entry the host stands itself), its path, and whether it is actually on
	/// screen.</summary>
	public readonly record struct Presentation(
		uint Address, uint Serial, string Name, uint State, int Screen)
	{
		/// <summary>
		/// The guest is drawing this screen. Live is not visible: closed menus stay in the registry
		/// with valid roots. A frozen screen is excluded too — a non-zero freeze byte returns before
		/// the per-frame lists are rebuilt, so it holds stale content.
		/// </summary>
		/// <remarks>
		/// Three gates at three levels, all of which must pass, because the guest suppresses a
		/// screen at whichever level suits it: the presentation's draw byte, its update byte, and
		/// the root element's <c>active</c> bit, which <c>CPresentation::Update</c> tests before
		/// walking the tree.
		/// </remarks>
		public bool IsShowing =>
			(State & 1u) != 0u && (State & 2u) == 0u && (State & 4u) == 0u && (State & 8u) != 0u;

		/// <summary>Held at last frame's content rather than being updated.</summary>
		public bool IsFrozen => (State & 2u) != 0u;

		/// <summary>
		/// Parked by the guest: loaded, kept in the registry, skipped by the scene draw. The pause
		/// menu's and menu cycler's loaders write the suppression pair right after loading; the
		/// HUD's loader never does.
		/// </summary>
		public bool IsSuppressed => (State & 4u) != 0u;

		/// <summary>
		/// True when the path ends with the given file name. A suffix test: the string is whatever
		/// the guest's path conformer handed the factory, prefix and slash direction unestablished.
		/// </summary>
		public bool Is(string fileName) =>
			Name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase);
	}
}
