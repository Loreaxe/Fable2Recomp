using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The owned SPELL-ASSIGNMENT widget — the abilities blade and the quick wheel.
///
/// Two instances, one controller, as the original has it. The hosts author the widget in
/// different places — `hud.bgf` at canvas (−315, 9) scaled 0.8 for the wheel,
/// `abilitiesscreen.bgf` inside the book's fourth page — so each clone keeps its own file's
/// authored transform. Behind both is one CECSpellManager, sampled once per frame and shared.
///
/// The vocabulary is the guest's: moving the SLOT writes nothing, moving the CAROUSEL writes
/// immediately (`sub_82541F30` per press), and A is not a confirm — it propagates the current
/// slot's will down into every later slot that offers it.
///
/// B is still the guest's: the blade's open state is its mode byte at `screen14+620`, with
/// exactly two writers, and closing it is `sub_826C25C0` through the screen's own handler.
/// </summary>
public sealed partial class Engine_UiSpellAssignSet
{
	/// <summary>The host the widgets are parented to and take their art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>The presentation the QUICK WHEEL's copy is authored in.</summary>
	public const string WheelPresentation = "hud";

	/// <summary>
	/// The window the wheel's seat resolves against, fed by the owner. On split screen the wheel
	/// stands in the half of the player whose flag opened it (<see cref="WheelPlayer"/>), laid out
	/// as it is on a whole screen of that size; on a shared screen it is where it always was.
	/// </summary>
	public Vector2 Window { get; set; }

	/// <summary>
	/// Whose spells wheel is up, as the guest states it: 1 player one (`QM+36`), 2 player two
	/// (`QM+37`), 0 none. Both up reads as player one, the cursor's own rule.
	/// </summary>
	public int WheelPlayer =>
		_spells.WheelOpen ? 1 : _spells.WheelOpenPlayerTwo ? 2 : 0;

	/// <summary>Whether the wheel's node carries a seat transform a shared screen must undo.</summary>
	private bool _wheelSeated;

	/// <summary>The owned assignment widget, one per host — the blade and the quick wheel.</summary>
	private Engine_UiSpellAssign? _wheel;

	/// <summary>
	/// THE BOOK'S TRAY IS THE SAME WIDGET. The abilities book's own `SpellAssigning` element used to
	/// be the guest's presentation mirrored, so the two trays were two templates and nothing the
	/// host adds to one (a mod's secondary pick, for one) reached the other. This copy is cloned
	/// from the wheel's pattern book, stands where the book authored its element, and follows the
	/// guest's blade widget (screen 14 + 200) as its methods state it (`@Spell.Widget`); the book's own element
	/// is hidden while it stands. The levers still go to the guest (DriveBlade), whose widget commits.
	/// </summary>
	private Engine_UiSpellAssign? _blade;
	private Node3D? _bladeElement;

	/// <summary>This frame's assignment sample, taken once and shared by both hosts.</summary>
	private Engine_UiModel.SpellAssignState _spells;

	// The touch Spells button's state and picker: android/Engine_UiSpellAssignSet.Touch.cs.

	/// <summary>Auto-repeat clocks for the blade, one per axis — see <see cref="Engine_UiScreenLevel"/>.</summary>
	private Engine_UiNavigationClock? _vertical;
	private Engine_UiNavigationClock? _horizontal;

	/// <summary>
	/// A clock on the PAGE gate's numbers rather than the menu's — see
	/// <see cref="Engine_UiScreenInput.PageFirstRepeat"/>.
	/// </summary>
	private static Engine_UiNavigationClock PageClock() => new()
	{
		FirstInterval = Engine_UiScreenInput.PageFirstRepeat,
		SubsequentInterval = Engine_UiScreenInput.PageRepeat,
	};

	/// <summary>
	/// The host retired <paramref name="presentations"/>' pattern books on a new mount: a widget
	/// built from one goes, and is built again from the new book the next time it stands.
	/// </summary>
	public void Retire(System.Collections.Generic.ISet<string> presentations)
	{
		if (!presentations.Contains(WheelPresentation))
		{
			return;
		}

		foreach (Engine_UiSpellAssign? widget in new[] { _wheel, _blade })
		{
			if (widget is not null && GodotObject.IsInstanceValid(widget))
			{
				widget.QueueFree();
			}
		}

		_wheel = null;
		_blade = null;
	}

	/// <summary>Builds the owned widget from one host's pattern book, keeping that file's own
	/// authored transform.</summary>
	private Engine_UiSpellAssign? Ensure(string presentation)
	{
		if (Host?.PatternBookFor(presentation) is not { } art)
		{
			return null;
		}

		var widget = new Engine_UiSpellAssign
		{
			Name = $"SpellAssign_{presentation}",
			Book = art,
			Visible = false,
			ResolveTexture = path => Host.LoadTexture(path),
		};

		widget.Build();
		if (!widget.HasArt)
		{
			widget.QueueFree();
			return null;
		}

		Host.AddChild(widget);
		return widget;
	}

	/// <summary>
	/// Stands the owned widget wherever the guest says one is up, and feeds it. The quick-wheel
	/// half is driven here because its open flag is the guest's (`[GUI+28]+36` for player one,
	/// `+37` for two) and the widget is the same one.
	/// </summary>
	private string _gateReport = string.Empty;

	public void Follow(bool revealed)
	{
		if (Host is null || !revealed)
		{
			return;
		}

		_spells = Fable2UiSource.Active.GetSpellAssign();
		PublishForTouch();
		TrackAssigned();

		// THE BLADE IS THE GUEST'S. Its widget (`screen14+200`, bound by `sub_826C2480` at
		// 0x826c2510) writes every slide, icon texture and adjuster position onto the live
		// presentation, and the abilities book's clone follows those statements; the host only
		// forwards the levers (DriveBlade). The wheel below has no presentation of its own to
		// follow, so that one stays drawn here.
		// The wheel opens over the running game with no screen behind it, so it is gated on its
		// own flag, never a screen id. Both are suppressed inside their books, so one copy draws.
		// WHY THE WHEEL DID NOT OPEN. The right trigger IS the binding — the loaded control
		// scheme `GameControlsCombatOnRelease` is an alias onto a record that overrides the
		// spells-menu action's input to XboxButton 16, the right trigger, over a base scheme
		// that uses the right bumper — and the pad reaches the guest (the input log shows the
		// trigger at full travel). So a refusal is the GUEST'S, and its gates are invisible from
		// here: `sub_826B4448` needs TWO spell TYPES both AWARDED and PURCHASED at POWER
		// LEVEL 0, `sub_8253E220` needs one assigned and magic enabled, and the flow state and
		// player mode must both be right.
		//
		// THE FIVE-WIDE AXIS IS THE POWER (CHARGE) LEVEL, not a ring and not a d-pad slot:
		// the guest's own planes are `mgr+156+5*type+level` (awarded) and `mgr+196+5*type+level`
		// (purchased), and the gate's cursor takes level 0 alone. So `0:1` means one will usable
		// at the first charge level — a correct refusal, because there is nothing to choose
		// between. Five levels each holding the SAME single will would read `1,1,1,1,1` and be
		// refused too; it is spell TYPES at one level that the gate counts.
		//
		// The offers per level are already in the state we read, so the count that gate turns on
		// can be said out loud. Printed only when it changes.
		// EITHER PLAYER'S FLAG. The guest keeps one open flag per player (QM+36 player one,
		// QM+37 player two: `sub_8236F788` sets `a1[37]` when the player's `+36` is 2) and a
		// joiner's own hero IS player two, so a gate on player one's flag alone never drew the
		// joiner's wheel. The cursor below follows whichever is open.
		bool wheelUp = _spells.WheelOpen || _spells.WheelOpenPlayerTwo;
		if (_spells.Known)
		{
			var offers = new System.Collections.Generic.List<string>();
			for (int level = 0; level < Engine_UiModel.SpellAssignState.Slots; ++level)
			{
				offers.Add($"L{level}:{_spells.Choices(level).Count}");
			}

			string report = $"open={wheelUp} offers {string.Join(",", offers)}";
			if (report != _gateReport)
			{
				_gateReport = report;
				GD.Print($"[spell-assign] {report} — offers are per POWER LEVEL; the guest " +
					"refuses below two spell types awarded AND purchased at level 0");
			}
		}

		// THE CAROUSEL COMMITS ON MOVE (the guest's widget writes as it turns), so a partner pick
		// is made by turning to the partner and pressing X — which has just re-assigned the slot
		// to that will. The wills each slot held when the selector came up are kept, and a pick
		// puts the slot's own will back. Picking without turning is a clear.
		if (wheelUp && !_wheelWasUp)
		{
			_wheelOpenedWills = OpenedWills();
		}

		_wheelWasUp = wheelUp;
		if (wheelUp && (_wheel is null || !GodotObject.IsInstanceValid(_wheel)))
		{
			_wheel = Ensure(WheelPresentation);
			var offered = new System.Collections.Generic.List<string>();
			var assigned = new System.Collections.Generic.List<string>();
			for (int i = 0; i < Engine_UiModel.SpellAssignState.Slots; ++i)
			{
				offered.Add(_spells.Choices(i).Count.ToString());
				assigned.Add(_spells.On(i)?.ToString() ?? "-");
			}
			GD.Print($"[spell-assign] wheel opened: known={_spells.Known} offered per level=" +
				$"{string.Join(",", offered)} assigned={string.Join(",", assigned)}");
		}

		if (_wheel is not null && GodotObject.IsInstanceValid(_wheel))
		{
			_wheel.Visible = wheelUp && !(Refii.Core.Engine_Platform.IsAndroid && TouchHidesWheel);
			if (wheelUp)
			{
				// THE CURSOR IS THE GUEST'S. Its widget at `*(*(GUI+32)+92)` moves row and cell
				// itself (the pump feeds it the stick every held frame); this copy follows them.
				int player = _spells.WheelOpenPlayerTwo && !_spells.WheelOpen ? 2 : 1;
				_wheel.Player = player;
				_wheel.FollowGuest(Fable2UiSource.Active.GetSpellWheel(player));
				_wheel.SetLive(_spells);

				// In its owner's half while split. The widget's own node is untransformed (its
				// clone carries the HUD's authored canvas transform), so the seat goes on it.
				Engine_UiSeats.Frame seat = Engine_UiSeats.Of(
					Window, Engine_UiSeats.For(player, Refii.Core.Engine_Views.Split));
				if (!seat.IsWhole || _wheelSeated)
				{
					_wheel.Transform = seat.IsWhole ? Transform3D.Identity : seat.Transform(0f);
					_wheelSeated = !seat.IsWhole;
				}
			}
			else
			{
				_wheel.FollowGuest(default);
			}
		}
	}

	/// <summary>One frame of the blade: up/down move the slot, left/right move the carousel and
	/// commit, A propagates, B closes the level through the guest's own mode byte.</summary>
	/// <summary>
	/// Forwards the blade's levers to the guest's own handler: `sub_826C2958` routes 28/29 to
	/// the row, 30/31 to the carousel (which commits as it moves, `sub_826B5330`), 36 to the
	/// copy-down (`sub_826B4EF8`) and 37 to the close — and the widget repaints the presentation
	/// the book's clone follows. Repeat cadence is the page clock's, as for every held direction.
	/// </summary>
	/// <summary>
	/// The mouse on the abilities blade, drawn by <paramref name="book"/>'s clone of
	/// `SpellAssigning`: a click on a `Spell{n}` row walks the guest's row to it, a click on an
	/// `HL1`/`HR1` arrow steps the will carousel (which commits as it moves). Each press goes
	/// with its release (32..35) so a burst lands. The copy-down stays on A — a click is not a
	/// confirm here. Every click on the screen is taken.
	/// </summary>
	/// <summary>Stands the owned widget in the book's blade place and feeds it the guest's blade state.</summary>
	public void FollowBlade(Engine_UiOwnedBook book)
	{
		if (Host is null || book.Find("SpellAssigning") is not { } element)
		{
			return;
		}

		if (_blade is null || !GodotObject.IsInstanceValid(_blade) || !ReferenceEquals(_bladeElement, element))
		{
			if (_blade is not null && GodotObject.IsInstanceValid(_blade))
			{
				_blade.QueueFree();
			}

			if (_bladeElement is not null && GodotObject.IsInstanceValid(_bladeElement))
			{
				_bladeElement.Visible = true;
			}

			_blade = Ensure(WheelPresentation);
			_bladeElement = element;
			if (_blade is null)
			{
				return;
			}

			// Out of the HUD host and into the book, at the book's own authored place and scale:
			// the clone inside the widget carries the HUD's canvas transform (Build), which is
			// dropped so only the book's element transform places it. The book's own element is
			// SUPPRESSED, not hidden: the binding re-applies the presentation's slides to it every
			// frame, and a plain Visible = false was undone on the next one.
			_blade.Name = "SpellAssign_book";
			_blade.GetParent()?.RemoveChild(_blade);
			element.GetParent()?.AddChild(_blade);
			_blade.Transform = element.Transform;
			if (_blade.GetNodeOrNull<Node3D>(Engine_UiSpellAssign.ElementName) is { } inner)
			{
				inner.Transform = Transform3D.Identity;
			}

			book.Suppressed.Add(Engine_UiSpellAssign.ElementName);
			element.Visible = false;
		}

		Engine_UiModel.SpellWheelState state = Fable2UiSource.Active.GetSpellBlade();
		if (state.Known && !_bladeWasKnown)
		{
			_bladeOpenedWills = OpenedWills();
		}

		_bladeWasKnown = state.Known;
		_blade.Visible = state.Known;
		_blade.EnsurePrompts(book);
		// The abilities book's player: its blade reads and picks for them.
		int bookPlayer = Engine_UiModMenus.MenuPlayer;
		_blade.ShowPrompts(state.Known && Engine_UiSpellAssign.SecondaryUnlocked(bookPlayer), bookPlayer);
		if (state.Known)
		{
			_blade.Player = bookPlayer;
			_blade.FollowGuest(state);
			_blade.SetLive(_spells);
		}
		else
		{
			_blade.FollowGuest(default);
		}
	}

	public void BladePointer(Engine_UiOwnedBook book, double delta)
	{
		_predictedFor = System.Math.Max(0.0, _predictedFor - delta);
		Node3D? blade = _blade is { } own && GodotObject.IsInstanceValid(own) && own.Visible
			? own
			: book.Find("SpellAssigning");
		if (!Engine_UiPointer.TakeLeft() || Engine_UiPointer.Canvas is not { } at || blade is null ||
			Engine_UiTheme.FindByName(blade, "Adjuster") is not Node3D adjuster)
		{
			return;
		}

		// The guest's row is where it put the adjuster (`y = -160 + 80·row`); a click within a
		// moment of the last one uses the row it asked for, before the clone catches up.
		int row = _predictedFor > 0.0
			? _predictedRow
			: Mathf.RoundToInt((adjuster.Position.Y + 160f) / 80f);

		foreach ((string arrow, uint code, uint release) in new[]
		{
			("HL1", Engine_UiScreenInput.Left, Engine_UiScreenInput.LeftReleased),
			("HR1", Engine_UiScreenInput.Right, Engine_UiScreenInput.RightReleased),
		})
		{
			if (Engine_UiTheme.FindByName(adjuster, arrow) is Node3D button && button.IsVisibleInTree() &&
				Engine_UiPointer.CanvasBounds(button) is { } box && box.Grow(6f).HasPoint(at))
			{
				Engine_UiSource.Current.ScreenAction(code);
				Engine_UiSource.Current.ScreenAction(release);
				return;
			}
		}

		for (int n = 1; n <= 5; ++n)
		{
			if (Engine_UiTheme.FindByName(blade, $"Spell{n}") is not Node3D slot ||
				!slot.IsVisibleInTree() || Engine_UiPointer.CanvasBounds(slot) is not { } bounds ||
				!bounds.HasPoint(at))
			{
				continue;
			}

			int steps = (n - 1) - row;
			for (int i = 0; i < System.Math.Abs(steps); ++i)
			{
				// 28 raises the row (`++row`), 29 lowers it.
				Engine_UiSource.Current.ScreenAction(
					steps > 0 ? Engine_UiScreenInput.Up : Engine_UiScreenInput.Down);
				Engine_UiSource.Current.ScreenAction(
					steps > 0 ? Engine_UiScreenInput.UpReleased : Engine_UiScreenInput.DownReleased);
			}

			_predictedRow = n - 1;
			_predictedFor = 0.3;
			return;
		}
	}

	private int _predictedRow;
	private double _predictedFor;

	/// <summary>
	/// The mouse on the quick spell blade (held right trigger): a click on a `Spell{n}` row walks
	/// the guest's widget to it and a click on `HL1`/`HR1` steps the carousel, through the same
	/// `SpellBlade:*` levers the stick uses, each step followed by its release. The cursor is
	/// the guest's (`GetSpellWheel`). Every click while the blade stands is taken.
	/// </summary>
	public void WheelPointer()
	{
		int player = _spells.WheelOpenPlayerTwo && !_spells.WheelOpen ? 2 : 1;
		if (_wheel is not { Visible: true } wheel || !GodotObject.IsInstanceValid(wheel) ||
			!Engine_UiPointer.TakeLeft() || Engine_UiPointer.Canvas is not { } at)
		{
			return;
		}

		if (Engine_UiTheme.FindByName(wheel, "Adjuster") is Node3D adjuster)
		{
			foreach ((string arrow, string lever) in new[] { ("HL1", "left"), ("HR1", "right") })
			{
				if (Engine_UiTheme.FindByName(adjuster, arrow) is Node3D button &&
					button.IsVisibleInTree() && Engine_UiPointer.CanvasBounds(button) is { } box &&
					box.Grow(6f).HasPoint(at))
				{
					Engine_UiSource.Current.PostFrontEndAction("SpellBlade:" + lever);
					Engine_UiSource.Current.PostFrontEndAction("SpellBlade:release");
					return;
				}
			}
		}

		int row = Fable2UiSource.Active.GetSpellWheel(player).Row;
		for (int n = 1; n <= 5; ++n)
		{
			if (Engine_UiTheme.FindByName(wheel, $"Spell{n}") is not Node3D slot ||
				!slot.IsVisibleInTree() || Engine_UiPointer.CanvasBounds(slot) is not { } bounds ||
				!bounds.HasPoint(at))
			{
				continue;
			}

			int steps = (n - 1) - row;
			for (int i = 0; i < System.Math.Abs(steps); ++i)
			{
				Engine_UiSource.Current.PostFrontEndAction(steps > 0 ? "SpellBlade:up" : "SpellBlade:down");
				Engine_UiSource.Current.PostFrontEndAction("SpellBlade:release");
			}

			return;
		}
	}

	/// <summary>A mod's secondary pick on the quick wheel: the will under the guest's own cursor.</summary>
	public void WheelSecondary(Engine_UiSpellAssign.SecondaryAction action)
	{
		int player = _spells.WheelOpenPlayerTwo && !_spells.WheelOpen ? 2 : 1;
		Engine_UiModel.SpellWheelState state = Fable2UiSource.Active.GetSpellWheel(player);
		if (action == Engine_UiSpellAssign.SecondaryAction.Remove)
		{
			if (state.Known)
			{
				Engine_UiSpellAssign.RemoveSecondary(player, state.Row);
			}

			return;
		}

		PickSecondary(player, state, _wheelOpenedWills,
			_wheel is { } wheel && GodotObject.IsInstanceValid(wheel) ? wheel.CursorWill() : null);
	}

	private bool _wheelWasUp;
	private bool _bladeWasKnown;
	private int[]? _wheelOpenedWills;
	private int[]? _bladeOpenedWills;

	/// <summary>
	/// Each slot's will as last sampled, and the will it held BEFORE the carousel turned it. The
	/// carousel commits as it turns, and a turn that ends without a pick leaves the slot on
	/// the will turned to; the next open then reads that will as the slot's own, and a pick on
	/// it was refused ("is the slot's own spell") — the shock pick that never took. The will
	/// before the turn is what the pick puts back in that case.
	///
	/// ONLY A TURN COUNTS. The record is taken only while a selector is up (the carousel's
	/// commit is the only writer then), never from the pick's own restore (<see cref="_restoring"/>),
	/// and it is dropped when the assignment changes with no selector up (the abilities book,
	/// a load): taken from those, a deliberate assignment read as a stray turn and a later pick
	/// on the slot's own will SWAPPED the pair — the new spell became the partner and the old
	/// one came back as the slot's spell.
	/// </summary>
	private readonly int[] _lastAssigned = Filled(-1);
	private readonly int[] _prevAssigned = Filled(-1);
	private readonly int[] _restoring = Filled(-1);

	private static int[] Filled(int value)
	{
		var wills = new int[Engine_UiModel.SpellAssignState.Slots];
		System.Array.Fill(wills, value);
		return wills;
	}

	private void TrackAssigned()
	{
		if (!_spells.Known)
		{
			return;
		}

		bool selectorUp = _wheelWasUp || _bladeWasKnown;
		for (int slot = 0; slot < _lastAssigned.Length; ++slot)
		{
			int now = _spells.On(slot) ?? -1;
			if (now == _lastAssigned[slot])
			{
				continue;
			}

			if (now >= 0 && now == _restoring[slot])
			{
				// The pick put the slot's own will back: the slot is on its own will again.
				_restoring[slot] = -1;
				_prevAssigned[slot] = -1;
			}
			else if (selectorUp && _lastAssigned[slot] >= 0)
			{
				_prevAssigned[slot] = _lastAssigned[slot];
			}
			else
			{
				_prevAssigned[slot] = -1;
			}

			_lastAssigned[slot] = now;
		}
	}

	/// <summary>The will on each slot right now: what a pick restores after the carousel's commit.</summary>
	private int[] OpenedWills()
	{
		var wills = new int[Engine_UiModel.SpellAssignState.Slots];
		for (int slot = 0; slot < wills.Length; ++slot)
		{
			wills[slot] = _spells.On(slot) ?? -1;
		}

		return wills;
	}

	/// <summary>
	/// A pick on a selector: the will under the cursor becomes the row's partner, and the row's
	/// own will — the one it held when the selector came up — is put back, since turning the
	/// carousel to the partner committed the partner as the row's spell.
	///
	/// THE CURSOR IS THE HOST WIDGET'S. The quick wheel's carousel is Godot's: `Move` steps the
	/// widget's own cell and commits with `AssignSpell`, and the guest's widget words never move
	/// their cell for it — so reading `state.CellOf(row)` named the will the slot held when the
	/// wheel came up, every time, and the pick was refused as "the slot's own spell" (seven in a
	/// row in one log). The widget's <see cref="Engine_UiSpellAssign.CursorWill"/> is the will
	/// under its centre; the guest's cell stands in only when there is no widget.
	///
	/// THE RESTORE IS THE GUEST'S OWN TURN ON THE BLADE. The blade's carousel is the guest's
	/// widget: its cell is what the centre, the flanking icons and the label are read from, so
	/// putting the own will back through the slot writer alone left the cell parked on the
	/// partner — the centre showed the own will over the partner's neighbours and label, and
	/// the next turn started from the partner. Turning the guest's carousel back to the own
	/// will (the same LEFT/RIGHT levers the stick uses) commits it and moves the cell in one
	/// go. The quick wheel's carousel is Godot's and follows the assignment (SetLive), so its
	/// restore goes through the slot writer, for either player.
	/// </summary>
	private void PickSecondary(int player, Engine_UiModel.SpellWheelState state, int[]? opened, int? cursor, bool guestCarousel = false)
	{
		if (!state.Known)
		{
			return;
		}

		int row = state.Row;
		int? will = cursor ?? state.WillAt(row, state.CellOf(row));
		int primary = opened is not null && row >= 0 && row < opened.Length ? opened[row] : -1;
		if (will is { } picked && picked == primary && row >= 0 && row < _prevAssigned.Length &&
			_prevAssigned[row] >= 0 && _prevAssigned[row] != picked)
		{
			// The slot shows the will under the cursor because an earlier turn committed it
			// there: the will the slot held before that turn is its own, and is put back.
			primary = _prevAssigned[row];
		}

		Engine_UiSpellAssign.ChooseSecondary(player, row, will, primary);
		if (primary < 0 || will is not { } chosen || chosen == primary)
		{
			return;
		}

		_restoring[row] = primary;
		if (guestCarousel)
		{
			System.Collections.Generic.List<int> choices = _spells.Choices(row);
			int steps = choices.IndexOf(primary) - choices.IndexOf(chosen);
			if (choices.IndexOf(primary) < 0 || choices.IndexOf(chosen) < 0)
			{
				Engine_UiSpellPicks.Choose(Engine_UiSpellPicks.Game, player, row, primary);
				return;
			}

			for (int i = 0; i < System.Math.Abs(steps); ++i)
			{
				Engine_UiSource.Current.PostFrontEndAction(steps < 0 ? "SpellBlade:left" : "SpellBlade:right");
				Engine_UiSource.Current.PostFrontEndAction("SpellBlade:release");
			}
		}
		else
		{
			Engine_UiSpellPicks.Choose(Engine_UiSpellPicks.Game, player, row, primary);
		}
	}

	public void DriveBlade(Engine_UiSurfaceInput input)
	{
		_vertical ??= PageClock();
		_horizontal ??= PageClock();

		for (int step = _vertical.Steps(input.Direction, input.Delta); step > 0; --step)
		{
			Engine_UiSource.Current.ScreenAction(
				input.Direction < 0 ? Engine_UiScreenInput.Up : Engine_UiScreenInput.Down);
		}

		for (int step = _horizontal.Steps(input.Horizontal, input.Delta); step > 0; --step)
		{
			Engine_UiSource.Current.ScreenAction(
				input.Horizontal < 0 ? Engine_UiScreenInput.Left : Engine_UiScreenInput.Right);
		}

		// LB / RB step the book's player's active pick (Engine_UiSpellAssignSecondary): the book
		// leaves them unused while the blade is up.
		if (input.TabPrev || input.TabNext)
		{
			Engine_UiSpellAssign.StepSecondary(Engine_UiModMenus.MenuPlayer, input.TabNext ? 1 : -1);
		}

		Engine_UiSpellAssign.SecondaryAction action = Engine_UiSpellAssign.SecondaryPressed(input.Flip, input.Equip);
		if (action != Engine_UiSpellAssign.SecondaryAction.None)
		{
			// The mods' picks on the blade, for the book's player: X picks the will under the
			// carousel's centre into the active pick, Y clears the row's active pick.
			Engine_UiModel.SpellWheelState state = Fable2UiSource.Active.GetSpellBlade();
			int bookPlayer = Engine_UiModMenus.MenuPlayer;
			if (state.Known && action == Engine_UiSpellAssign.SecondaryAction.Remove)
			{
				Engine_UiSpellAssign.RemoveSecondary(bookPlayer, state.Row);
			}
			else if (state.Known)
			{
				PickSecondary(bookPlayer, state, _bladeOpenedWills,
					_blade is { } blade && GodotObject.IsInstanceValid(blade) ? blade.CursorWill() : null, guestCarousel: true);
			}
		}
		else if (input.Accept)
		{
			Engine_UiSource.Current.ScreenAction(Engine_UiScreenInput.Select);
		}
		else if (input.Back)
		{
			Engine_UiSource.Current.ScreenAction(Engine_UiScreenInput.Back);
		}
	}
}
