using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The will assignment widget, owned — five d-pad slots down the left, a carousel across each.
///
/// One widget, two hosts: the quick wheel's `SpellAssigning` in `hud.bgf` and the abilities
/// blade's copy at `Foreground.Book.Pages.Page4.Contents.Blade.SpellAssigning`. Not a ring —
/// the ring in `SpellsMenu.bgf` is stale art that never shipped.
///
/// Ownership split:
/// <list type="bullet">
/// <item><b>Art</b> — cloned from the screen's pattern book: five `Spell{n}` slots authored at
/// y −160/−80/0/+80/+160, plus the `Adjuster` strip carrying the carousel cells.</item>
/// <item><b>Data</b> — the guest's: the will on each slot (<c>byte[mgr + 128 + slot]</c>) and
/// which wills a slot may be given (the two-plane offering test). Read, never inferred.</item>
/// <item><b>Cursor and input</b> — ours; the guest's own row and cell are never read.</item>
/// <item><b>The commit</b> — the guest's writer: moving the carousel assigns
/// (`sub_826B5330`), and A does something else.</item>
/// </list>
///
/// The icon table is the guest's, unrolled in `sub_826B5D30`; name tags from `sub_826B50B8`.
/// Eight wills, hard-coded in this order on the guest side.
/// </summary>
public sealed partial class Engine_UiSpellAssign : Node3D
{
	/// <summary>The element this widget is authored as, in both files that carry it.</summary>
	public const string ElementName = "SpellAssigning";

	/// <summary>How far apart the five slots sit — the authored y step, and the same values
	/// `sub_826B5CB0` moves the adjuster between.</summary>
	public const float RowStep = 80f;

	/// <summary>The eight wills — see <see cref="Engine_UiScreenConstants.Wills"/>, where the
	/// translated tables live.</summary>
	private static readonly (string Icon, string Tag)[] Wills = Engine_UiScreenConstants.Wills;

	private const string IconFolder = Engine_UiScreenConstants.WillIconFolder;

	/// <summary>The pattern book to clone the widget's art out of.</summary>
	public Engine_UiTheme? Book { get; set; }

	/// <summary>Loads one of the will icons by its game-relative path.</summary>
	public System.Func<string, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>One authored slot row and the icon quad inside it.</summary>
	private sealed class Row
	{
		public required Node3D Node { get; init; }
		public MeshInstance3D? Icon { get; set; }

		/// <summary>The smaller copies of <see cref="Icon"/>, one per mod's pick, in load order (Engine_UiSpellAssignSecondary).</summary>
		public List<MeshInstance3D> Minis { get; } = new();

		/// <summary>Each mini's last painted state; int.MinValue before the first paint.</summary>
		public List<int> MinisShown { get; } = new();

		/// <summary>Whether each mini's last paint left it drawable (its art resolved).</summary>
		public List<bool> MinisDrawn { get; } = new();

		/// <summary>The pick count and active pick the minis were last placed for; -1 to place again.</summary>
		public int MinisPlaced { get; set; } = -1;

		/// <summary>`Surround` — the segment ring; opacity 0 on slide 2, 100 on slide 3.</summary>
		public Node3D? Surround { get; set; }

		/// <summary>`Center.charging` — `scale.x` 0 on slide 2, 0.6 on slide 3.</summary>
		public Node3D? Charging { get; set; }

		public int Shown { get; set; } = -2;

		/// <summary>How many `Surround` segments are lit — the ring's slide, minus one.</summary>
		public int ShownCount { get; set; } = -1;
	}

	/// <summary>`charging.scale.x` on the "Active" slide, as authored.</summary>
	private const float ChargingScaleX = 0.6f;

	/// <summary>`Surround/Segment0..7` — the ring has eight, one per will the level offers.</summary>
	private const int SurroundSegments = 8;

	private readonly List<Row> _rows = new();
	private Node3D? _adjuster;
	/// <summary>The two painted carousel cells: [0] HL1 (previous will), [1] HR1 (next).
	/// The centre is the ROW's own icon — see Build.</summary>
	private MeshInstance3D?[] _cells = new MeshInstance3D?[2];

	/// <summary>
	/// HL1 and HR1 THEMSELVES, not their icon quads. With no neighbour `sub_826B5710` puts the
	/// whole cell on slide 1 (0x826b57cc / 0x826b5828) — frame and ring go with the icon — so
	/// blanking the quad alone leaves an empty ringed disc the guest does not draw.
	/// </summary>
	private Node3D?[] _cellNodes = new Node3D?[2];

	/// <summary>The will's name under the blade — see the note in <see cref="Build"/>.</summary>
	private MeshInstance3D? _label;

	/// <summary>
	/// The dark rounded plate behind that name — the SAME `Engine_UiRoundedPanel` the notices,
	/// the TopBox and the top-centre will banner sit on, not a second kind of panel. It is the
	/// remaster's replacement for the shipped black slab, and it sizes itself from the text.
	/// </summary>
	private Engine_UiRoundedPanel? _labelPanel;

	private string _labelShown = string.Empty;

	private int[] _cellShown = { -2, -2 };

	/// <summary>Which slot the player is on. OURS — see the class note.</summary>
	public int Row_ { get; private set; }

	/// <summary>Where in that slot's offered list the carousel sits.</summary>
	private int _cell;

	/// <summary>
	/// The assignment the carousel was last seeded from; -2 before the first sample, so the
	/// first one always seeds. Only a CHANGE re-seeds — see <see cref="SetLive"/>.
	/// </summary>
	private int _seededFrom = -2;

	/// <summary>The row <see cref="_seededFrom"/> was read from; -1 before the first sample.</summary>
	private int _seededRow = -1;

	/// <summary>
	/// The GUEST'S cursor, when this copy follows one — the quick wheel's widget at
	/// `*(*(GUI+32)+92)`, whose row (`+176`) and per-row cell (`+200+4r`) the guest moves itself
	/// (`sub_826B5430` cases 28..31, fed the held d-pad ids by the pump's `SpellBlade:up|down|left|right`). While
	/// set, <see cref="Row_"/> and the carousel cell are read from it and never seeded here.
	/// The abilities blade's copy follows the live presentation instead and leaves this unset.
	/// </summary>
	private Engine_UiModel.SpellWheelState _guest;

	/// <summary>States the guest's cursor for this copy to follow; default to stop following.</summary>
	public void FollowGuest(Engine_UiModel.SpellWheelState guest) => _guest = guest;

	private Engine_UiModel.SpellAssignState _state;

	/// <summary>True once the art resolved and the widget can stand.</summary>
	public bool HasArt => _rows.Count != 0;

	/// <summary>
	/// Clones the widget out of the pattern book and takes it apart into the pieces this drives.
	/// Clone the whole `SpellAssigning` subtree, not the parts: slots and adjuster are authored
	/// in one component at a stated scale (0.8 on the HUD copy).
	/// </summary>
	public void Build()
	{
		if (Book is null)
		{
			GD.PushWarning("[spell-assign] no pattern book; nothing to build");
			return;
		}

		Node3D? source = Book.ArtSourceNode(ElementName);
		Node3D? clone = Book.Art(ElementName);
		if (source is null || clone is null)
		{
			GD.PushWarning($"[spell-assign] '{ElementName}' is not in this screen's catalogue");
			return;
		}

		clone.Transform = Engine_UiTheme.CanvasTransform(source);
		clone.Name = ElementName;
		AddChild(clone);

		Node3D? group = Find(clone, "Group");
		if (group is null)
		{
			GD.PushWarning("[spell-assign] the clone carries no 'Group'");
			return;
		}

		for (int slot = 0; slot < Engine_UiModel.SpellAssignState.Slots; ++slot)
		{
			// `Spell1` is the BOTTOM row at y −160 and `Spell5` the top at +160, so the file's
			// numbering already runs in d-pad slot order and no remap is needed.
			if (Find(group, $"Spell{slot + 1}") is not { } row)
			{
				continue;
			}

			var built = new Row { Node = row };
			built.Icon = Find(row, "Icon") as MeshInstance3D;
			built.Surround = Find(row, "Surround");
			built.Charging = Find(row, "charging");
			_rows.Add(built);
		}

		_adjuster = Find(group, "Adjuster");
		if (_adjuster is not null)
		{
			// The carousel cells live one level further in, inside `Adjuster.Adjuster`, beside
			// the `select` frame. Only HL1 and HR1 are ever painted: `sub_826B5710` writes
			// `HL1.Center.Icon` (previous will), `HR1.Center.Icon` (next), and for the middle
			// the row's own `Spell{row}.Center.Icon` (`a1[row + 13]`), never `HC`. `HL2`, `HC`
			// and `HR2` are bound at `0x826B59CC`/`5A1C`/`5A6C` and read nowhere; painting `HC`
			// would double-draw over the row's own icon.
			Node3D inner = Find(_adjuster, "Adjuster") ?? _adjuster;
			_cellNodes[0] = Find(inner, "HL1");
			_cellNodes[1] = Find(inner, "HR1");
			_cells[0] = _cellNodes[0] is { } left ? Find(left, "Icon") as MeshInstance3D : null;
			_cells[1] = _cellNodes[1] is { } right ? Find(right, "Icon") as MeshInstance3D : null;

			// THE RESTING POSE, RESTATED — this is what stops the icons stacking.
			//
			// The guest NEVER MOVES these cells: `sub_826B5710` only swaps textures
			// (`sub_826B5D30`) and sets slide 1 or 3, and slide 3 commands opacity alone. The
			// one call to `sub_824251E0` in the whole class (0x826b5d28) targets the OUTER
			// Adjuster, per row. So the spread is AUTHORED, and the pattern book has to carry it.
			//
			// IT DOES NOT. The book is built with no live inputs, so `ComputeSlideState` falls
			// back to `PickForPreview`, and for the inner `Adjuster` — which owns the eight
			// slides Slide1/L1..L4/R1/R2 — that heuristic picks **L3**, a MID-SCROLL pose. In L3
			// `HR1` sits at x 0, dead on top of the row's own current-will icon, and `HC` sits
			// at −80 on top of `HL1`. That is the stacking, and an earlier fix here hid HC/HL2/
			// HR2 but left the worst duplicate — HR1 parked at centre — because its comment was
			// reading the L3 pose and calling it authored.
			//
			// So the carousel needs a layout of its own, and these are it — a three-cell row
			// with the current will in the middle and its neighbours either side:
			//     HL2 hidden · HL1 x −80 · HC hidden · HR1 x +80 · HR2 hidden, scale.x 0.85
			//
			// Seeded from the art's own resting pose because it is a sensible default and the
			// spacing already suits the icons, NOT because the file has any authority over
			// where we put things. These are OUR numbers now: they belong in the layout
			// template alongside the rest of the HUD's pieces, so a modder can move them
			// without touching code, and they live here only until that covers this widget.
			foreach ((string cellName, float x, bool shown) in new[]
				{
					("HL2", -120f, false), ("HL1", -80f, true), ("HC", 0f, false),
					("HR1", 80f, true), ("HR2", 120f, false),
				})
			{
				if (Find(inner, cellName) is not { } cell)
				{
					continue;
				}

				cell.Position = new Vector3(x, cell.Position.Y, cell.Position.Z);
				cell.Visible = shown;
				if (!shown)
				{
					continue;
				}

				// NO SCALE WRITE HERE. An earlier pass set scale.X alone to the art's 0.85 and
				// left Y at whatever the clone carried, which is not "smaller" — it is
				// NARROWER, and the icons came out visibly squashed. The cells' own basis is
				// already whatever the book built; the defect being fixed here is the POSITION,
				// so position is all that is restated.

				// AND THE OPACITY, not only the place. The builder folded the baked pose's
				// alpha into every descendant, so `HL1` came out transparent under L3 and
				// `Paint` only ever undoes that for the `Icon` quad — the frame and the ring
				// stayed invisible. Slide 1 states the whole cell at 100.
				RaiseCell(cell);
			}
		}

		// THE WILL'S NAME, UNDER THE BLADE. The guest writes it to no element: `sub_826B50B8`
		// clears the channel (`sub_8230D0C0`, 0x826b51c4) and pushes the resolved tag onto HUD
		// text channel 10 (`sub_8230C908`, 0x826b5220) of the 12-channel stack at `*(GUI+32)+88`,
		// which the rebuilt HUD draws as the top-centre banner. This label is OURS, and that is
		// allowed — Godot owns the UI's presentation and hooks the guest's DATA, not its
		// placement.
		//
		// WHAT IS NOT ALLOWED is sourcing it differently from the icon. It used to read a cell
		// of its own, so the name and the glyph could name different wills, and did. It now
		// reads the guest's own naming expression — `spells[row][sel]`, the identical term
		// `sub_826B50B8` uses at 0x826b513c and `sub_826B4770` paints at 0x826b4814 — so the
		// two cannot disagree by construction.
		// ONE HUD TEXT STYLE. The size and colour are not this widget's to choose: the notices,
		// the TopBox and the top-centre will banner all wear the style captured from `hud.bgf`,
		// and a label that picks its own 18pt white is the odd one out on screen. Falls back to
		// the captured defaults before `Capture` has run.
		if (Book.Label(string.Empty, Engine_UiHudTextStyle.TitleSize,
				Engine_UiHudTextStyle.TitleColour) is { } label)
		{
			label.Name = "WillName";
			label.Position = new Vector3(0f, -220f, 1f);

			// The plate goes in first and sits half a unit BEHIND the text — the same depth
			// relation `Engine_HudView.FitPanel` uses, where lower z is further back.
			var plate = new Engine_UiRoundedPanel { Name = "WillNamePlate" };
			clone.AddChild(plate);
			_labelPanel = plate;

			clone.AddChild(label);
			_label = label;
		}

		GD.Print($"[spell-assign] built: {_rows.Count}/{Engine_UiModel.SpellAssignState.Slots} slot(s), " +
			$"adjuster {(_adjuster is null ? "MISSING" : "ok")}, " +
			$"carousel {(_cells[0] is null && _cells[1] is null ? "MISSING" : "ok")}, " +
			$"label {(_label is null ? "none" : "ok")}");
	}

	/// <summary>
	/// Applies one sample of the guest's state and repaints. Cheap when nothing changed.
	/// The cursor is re-solved against the offer list, which grows and shrinks, so a carried
	/// cell index can point past the end; the guest's populate seeks the same way
	/// (`sub_826B4848`, 0x826b4b24).
	/// </summary>
	public void SetLive(Engine_UiModel.SpellAssignState state)
	{
		_state = state;
		if (_rows.Count == 0)
		{
			return;
		}

		if (_guest.Known)
		{
			// THE GUEST'S ROW AND CELL, read, not kept. The gold box sat on row 0 for as long as
			// the wheel was drawn from a host cursor nothing moved; the guest's own climbs and
			// descends with the stick, and its cell is the one the commit just wrote.
			Row_ = System.Math.Clamp(_guest.Row, 0, Engine_UiModel.SpellAssignState.Slots - 1);
		}

		Row_ = System.Math.Clamp(Row_, 0, MaxAssignableRow());
		List<int> choices = state.Choices(Row_);

		// THE CAROUSEL FOLLOWS THE ASSIGNMENT UNLESS SOMEONE IS DRIVING IT. `_cell` was only
		// ever seeded by `Move`/`Choose`, and the QUICK WHEEL calls neither — `Follow` calls
		// this and nothing else — so the wheel sat on row 0 cell 0 for its whole life while the
		// row icons painted the real assignment. That is a banner reading "Force Push" over a
		// selected Shock.
		//
		// Re-seeding on a CHANGE OF ASSIGNMENT rather than on every sample is what keeps this
		// from fighting the abilities blade: navigating moves the cell but assigns nothing
		// (`sub_826B5430` cases 28/29 only repaint), so the assignment is unchanged and the
		// cell is left alone. When a commit does land, seeking to the newly assigned will is
		// the same seek the guest's own populate does.
		// PER ROW, NOT ONE SCALAR. Keyed on the value alone, moving between two rows that hold
		// the SAME will read as no change, so the previous row's cell index was carried into a
		// different offer list — and the two rarely have the same membership, so the label and
		// the flanking icons named the wrong wills. The row is part of the key.
		int assignedNow = state.On(Row_) ?? -1;
		if (_guest.Known)
		{
			// The guest's list for a row is its offered wills compacted in index order
			// (`sub_826B4848`), which is exactly `Choices(row)`, so its cell indexes ours. Resolve
			// by the will it names rather than trusting the index outright.
			int guestCell = _guest.CellOf(Row_);
			int found = _guest.WillAt(Row_, guestCell) is { } will ? choices.IndexOf(will) : -1;
			_cell = found >= 0 ? found : System.Math.Max(guestCell, 0);
			_seededFrom = assignedNow;
			_seededRow = Row_;
		}
		else if (assignedNow != _seededFrom || Row_ != _seededRow)
		{
			_seededFrom = assignedNow;
			_seededRow = Row_;
			int found = assignedNow >= 0 ? choices.IndexOf(assignedNow) : -1;
			_cell = found >= 0 ? found : 0;
		}

		_cell = choices.Count == 0 ? 0 : System.Math.Clamp(_cell, 0, choices.Count - 1);

		for (int slot = 0; slot < _rows.Count; ++slot)
		{
			Row row = _rows[slot];
			if (!GodotObject.IsInstanceValid(row.Node))
			{
				continue;
			}

			// A dead row is slide 2, not hidden: `sub_826B4770` puts `Group.Spell{n}` on slide 3
			// within maxRow and slide 2 past it, never 1. Slides in `abilitiesscreen.bgf`:
			// 1 "Invisible" `Center.opacity 0`; 2 "Inactive" `Center.opacity 100`,
			// `Icon.opacity 0`, `Surround.opacity 0`, `charging.scale.x 0`; 3 "Active"
			// `Icon.opacity 100`, `Surround.opacity 100`, `charging.scale.x 0.6`.
			// The test is `r <= maxRow` (0x826b47c4), gaps included; a live row with nothing
			// assigned paints `spells[row][0]` (0x826b4814, cell seeded 0), never blank.
			List<int> offered = state.Choices(slot);
			bool live = state.Known && slot <= MaxAssignableRow();
			row.Node.Visible = true;

			if (row.Charging is not null)
			{
				Engine_UiBasisScale.SetX(row.Charging, live ? ChargingScaleX : 0f);
			}

			int will = -1;
			if (live)
			{
				int assigned = state.On(slot) ?? -1;
				will = assigned >= 0 ? assigned : offered.Count > 0 ? offered[0] : -1;
			}

			if (will != row.Shown)
			{
				row.Shown = will;
				Paint(row.Icon, will);
			}

			// The ring is a slide, `count + 1` (0x826b47ec): slide 1 "off" hides every segment;
			// slides 2..9 light the first N segments' `on` and the rest's `off`. The master
			// state the clone was built from has BOTH children active on every segment.
			int count = live ? System.Math.Min(offered.Count, SurroundSegments) : 0;
			if (row.Surround is not null && count != row.ShownCount)
			{
				row.ShownCount = count;
				row.Surround.Visible = count > 0;
				for (int i = 0; i < SurroundSegments; ++i)
				{
					if (Find(row.Surround, $"Segment{i}") is not { } segment)
					{
						continue;
					}

					if (Find(segment, "on") is { } on)
					{
						on.Visible = i < count;
					}

					if (Find(segment, "off") is { } off)
					{
						off.Visible = i >= count;
					}
				}
			}
		}

		// The carousel, over the selected slot: what would be assigned if the player moved left,
		// what is on it now, and what a move right would put there.
		if (_adjuster is not null && GodotObject.IsInstanceValid(_adjuster))
		{
			_adjuster.Position = new Vector3(
				_adjuster.Position.X,
				-160f + (RowStep * Row_),
				_adjuster.Position.Z);
			_adjuster.Visible = choices.Count > 0;
		}

		// Previous and next only: `sub_826B5710` reads `col - 1` into HL1 and `col + 1` into
		// HR1, each -1 with no neighbour; the middle goes on the row's own icon.
		for (int at = 0; at < _cells.Length; ++at)
		{
			int index = _cell + (at == 0 ? -1 : 1);
			int will = index >= 0 && index < choices.Count ? choices[index] : -1;

			// The CELL, not just its quad — see _cellNodes.
			if (_cellNodes[at] is { } cell && GodotObject.IsInstanceValid(cell))
			{
				cell.Visible = will >= 0;
			}

			if (will != _cellShown[at])
			{
				_cellShown[at] = will;
				Paint(_cells[at], will);
			}
		}

		// `spells[row][sel]` — the guest's own naming term, the same one the centre icon takes.
		if (_label is not null && GodotObject.IsInstanceValid(_label) && Book is not null)
		{
			int named = _cell >= 0 && _cell < choices.Count ? choices[_cell] : -1;
			string display = named >= 0 && named < Wills.Length
				? Book.Resolve(Wills[named].Tag)
				: string.Empty;
			if (display != _labelShown)
			{
				_labelShown = display;
				Book.SetMeshText(_label, display);
				FitLabelPlate(display);
			}
		}

		PaintSecondary();
	}

	/// <summary>
	/// Moves the slot cursor. No commit — changing slot assigns nothing: `sub_826B5430` cases
	/// 28/29 write `*(this+176)` (0x826B54B4 / 0x826B5544) and only repaint, and the writer
	/// `sub_82541F30` (`byte[will + 0x80 + slot]`) has three callers — `sub_826B5240`,
	/// `sub_826B5330` and `sub_826B4EF8` — none of them the row handler. The Lua's
	/// `assignCurrentSpellInRow()` writes only the presentation's `assignedSpells` table.
	///
	/// Deliberate difference: the guest resumes a per-row column cursor at `this+4*(row+50)`;
	/// we re-seek to whatever will is actually on the row.
	/// </summary>
	/// <summary>
	/// The highest row that offers anything — the guest's `maxAssignableRows`, the cursor's real
	/// ceiling; not `Slots - 1`. `sub_826B5430` case 28 is `if (row &lt; *(this+412)) ++row`, and
	/// `+412` is seeded -1 in the populate and set to the index of each row with an available
	/// spell, ending at the last such row.
	/// </summary>
	private int MaxAssignableRow()
	{
		int max = 0;
		for (int slot = 0; slot < Engine_UiModel.SpellAssignState.Slots; ++slot)
		{
			if (_state.Choices(slot).Count > 0)
			{
				max = slot;
			}
		}

		return max;
	}

	public void Move(int by)
	{
		if (by == 0)
		{
			return;
		}

		int wanted = System.Math.Clamp(Row_ + by, 0, MaxAssignableRow());
		if (wanted == Row_)
		{
			return;
		}

		Row_ = wanted;

		// Seek the carousel to whatever is already on the new slot, so arriving on a row shows
		// its will rather than the position the last row's carousel happened to be at.
		List<int> choices = _state.Choices(Row_);
		_cell = 0;
		if (_state.On(Row_) is { } will)
		{
			int found = choices.IndexOf(will);
			if (found >= 0)
			{
				_cell = found;
			}
		}

		SetLive(_state);
	}

	/// <summary>
	/// Moves the carousel and COMMITS, which is what the original does.
	///
	/// `sub_826B5330` writes `byte[mgr + 128 + row]` in the same call that advances the cell —
	/// there is no separate confirm. Returns true when something was actually assigned.
	/// </summary>
	public bool Choose(int by)
	{
		List<int> choices = _state.Choices(Row_);
		if (by == 0 || choices.Count == 0)
		{
			return false;
		}

		int wanted = System.Math.Clamp(_cell + by, 0, choices.Count - 1);
		if (wanted == _cell)
		{
			return false;
		}

		_cell = wanted;
		Engine_UiSource.Current.AssignSpell(Row_, choices[_cell], Engine_UiSource.Current.ActingPlayer);
		SetLive(_state);
		return true;
	}

	/// <summary>
	/// A press: copies this slot's will DOWN into every later slot that also offers it.
	/// `sub_826B4EF8` loops `row+1 .. maxRow` calling the writer for each match; it does not
	/// commit the current row, which is already committed.
	/// </summary>
	public void Propagate()
	{
		List<int> choices = _state.Choices(Row_);
		if (_cell >= choices.Count)
		{
			return;
		}

		int will = choices[_cell];
		int copied = 0;
		for (int slot = Row_ + 1; slot < Engine_UiModel.SpellAssignState.Slots; ++slot)
		{
			if (_state.Offers(slot, will))
			{
				Engine_UiSource.Current.AssignSpell(slot, will, Engine_UiSource.Current.ActingPlayer);
				++copied;
			}
		}

		if (copied != 0)
		{
			GD.Print($"[spell-assign] will {will} copied down into {copied} slot(s)");
		}
	}

	private void Paint(MeshInstance3D? node, int will)
	{
		if (node is null || !GodotObject.IsInstanceValid(node))
		{
			return;
		}

		// An empty slot hides its icon: these quads are authored on `will_shock`, so leaving one
		// alone draws lightning on a slot with nothing assigned.
		if (will < 0 || will >= Wills.Length)
		{
			node.Visible = false;
			return;
		}

		// Every `Center.Icon` quad in hud.bgf is authored opacity 0 in the master state and
		// reaches 100 only on the row's "Active" slide (`sub_826B4770` 0x826b47c4 → slide 3),
		// which the clone is never put on; painting a will stands in for that slide.
		node.Visible = true;
		Engine_UiSceneBuilder.SetQuadOpacity(node, 1f);
		if (ResolveTexture?.Invoke(IconFolder + Wills[will].Icon + ".tex") is { } picture &&
			node.MaterialOverride is ShaderMaterial material)
		{
			material.SetShaderParameter("diffuse_tex", picture);
		}
	}

	/// <summary>First descendant with a given name — the authored names are unique inside this
	/// subtree, which is why this is a search rather than a path.</summary>
	private static Node3D? Find(Node root, string name)
	{
		foreach (Node child in root.GetChildren())
		{
			if (child is Node3D spatial && child.Name == name)
			{
				return spatial;
			}

			if (Find(child, name) is { } deeper)
			{
				return deeper;
			}
		}

		return null;
	}

	/// <summary>
	/// Sizes the plate to the name it backs, in the clone's own space — the label and the plate
	/// are siblings, so the label's local position plus its mesh bounds is the rectangle.
	/// `Fit` adds its own padding, so nothing is grown here.
	/// </summary>
	private void FitLabelPlate(string display)
	{
		if (_labelPanel is null || !GodotObject.IsInstanceValid(_labelPanel) ||
			_label is null || !GodotObject.IsInstanceValid(_label))
		{
			return;
		}

		if (display.Length == 0)
		{
			_labelPanel.Visible = false;
			return;
		}

		// CENTRED ON THE BLADE, whatever the label's own alignment is. The pattern book's text
		// mesh is not necessarily centred on its origin, so parking it at x 0 put its LEFT EDGE
		// on the centre line and the word sat off to the right. Solving it from the mesh's own
		// bounds works for either alignment and costs nothing: shift by the bounds' centre so
		// the drawn glyphs straddle x 0. The bounds do not depend on Position, so this is stable
		// rather than a drift that creeps a little further every frame.
		Aabb box = _label.GetAabb();
		_label.Position = new Vector3(
			-(box.Position.X + (box.Size.X * 0.5f)), _label.Position.Y, _label.Position.Z);

		var rect = new Rect2(
			new Vector2(_label.Position.X + box.Position.X, _label.Position.Y + box.Position.Y),
			new Vector2(box.Size.X, box.Size.Y));
		_labelPanel.Fit(rect, _label.Position.Z - 0.5f);
	}

	/// <summary>
	/// Raises every quad under a carousel cell to full opacity — slide 1's statement for `HL1`
	/// and `HR1`. The icon quad is left to <see cref="Paint"/>, which hides it outright when the
	/// cell has no neighbouring will rather than drawing the master's `will_shock`.
	/// </summary>
	private static void RaiseCell(Node node)
	{
		if (node is MeshInstance3D quad && GodotObject.IsInstanceValid(quad))
		{
			Engine_UiSceneBuilder.SetQuadOpacity(quad, 1f);
		}

		foreach (Node child in node.GetChildren())
		{
			RaiseCell(child);
		}
	}
}
