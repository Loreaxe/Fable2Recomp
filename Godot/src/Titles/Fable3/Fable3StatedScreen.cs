namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// The element-written box-manager screens — hero status, stats, promises, quest summary, the
/// king's schedule and the ones <see cref="Fable3BoxScreenModel"/> composes — and the save menu,
/// drawn on the one menu template (<see cref="Fable3ListScreen"/>) from what the
/// guest states (<see cref="Fable3ScreenModel"/>). The screen showing is the box manager's
/// (display_box_hooks.cpp names its class; <see cref="Fable3ScreenBoxes.ScreenRoot"/> is its
/// element root); its texts are the stated texts under that root, its cursor row, ghosting and
/// rows what its own calls stated for its box (<see cref="Fable3ScreenStatements"/>). The screens
/// Godot owns (<see cref="Fable3BoxAnswers.OwnsScreen"/>: hero status, promise, king's schedule,
/// quest summary, wedding picker, chat channel, stats) take the pad, the keys bound to it and the
/// pointer here and answer through the box's own handler as the player who pressed
/// (<see cref="Fable3BoxAnswers.ForScreen"/>); on the others the pad still reaches the guest, which
/// moves its own cursor and closes its own screen.
/// </summary>
public partial class Fable3StatedScreen : Fable3ListScreen
{
	private readonly Dictionary<string, Fable3ScreenModel.Probe> _stated = new();
	private ulong _elements = ulong.MaxValue;
	private ulong _statements = ulong.MaxValue;
	private ulong _screens = ulong.MaxValue;
	private string? _root;
	private string? _boxRoot;
	private Fable3ScreenPage? _page;
	/// <summary>Each local player's presses, on that player's own pad.</summary>
	private readonly Fable3ActingPlayer.Edges _edges = new();
	private string? _panelRoot;
	private Fable3ListPanel? _statedPanel;

	/// <summary>The screen root's slide <see cref="_statedPanel"/> was read on: a page change re-reads the panel.</summary>
	private int _panelSlide;
	private readonly HashSet<string> _panelsSaid = new();

	protected override string LogTag => "fable3-screens";

	protected override string LogContext => $"{_root} ";

	protected override bool Framed => Fable3ScreenFrames.FrameOf(_root) is not null;

	/// <summary>
	/// The save menu's rows are choices, centred as the Options box centres its save section's
	/// (`Sections.SaveManagement.SelectionText.*`, halign 1 at x 0); its own `SaveMenu.bgf` ships
	/// in no build. The other box screens keep their rows left.
	/// </summary>
	protected override bool RowsOnCentreLine => _root == Fable3BoxScreenModel.SaveMenu;

	/// <summary>
	/// The panel the showing box's own frame stands its rows on (<see cref="Fable3ScreenFrames.PanelOf"/>),
	/// read again until its document stands; null for the save menu, which has no frame.
	/// </summary>
	protected override Fable3ListPanel? Panel
	{
		get
		{
			int slide = Fable3ScreenFrames.PanelSlide(_root);
			if (_panelRoot != _root || _panelSlide != slide || _statedPanel is null)
			{
				_panelRoot = _root;
				_panelSlide = slide;
				_statedPanel = Fable3ScreenFrames.PanelOf(_root, slide);
				if (_statedPanel is { } panel && _root is not null && _panelsSaid.Add(_root))
				{
					Godot.GD.Print($"[fable3-screens] {_root} stands on its panel: centre {panel.CentreX:0.#}, rows at " +
						$"{panel.LabelX:0.#} ({(panel.Centred ? "centred" : "left")}), first row {panel.FirstRowY:0.#}, " +
						$"pitch {panel.Pitch?.ToString("0.#") ?? "the carousel's"}, values {panel.ValueX?.ToString("0.#") ?? "-"}");
				}
			}

			return _statedPanel;
		}
	}

	protected override Fable3ScreenPage? Compose(double delta)
	{
		if (Fable3Elements.Generation == _elements && Fable3UiStatements.Generation == _statements &&
			Fable3ScreenStatements.Generation == _screens && Fable3ScreenBoxes.ScreenRoot == _boxRoot)
		{
			return _page;
		}

		_elements = Fable3Elements.Generation;
		_statements = Fable3UiStatements.Generation;
		_screens = Fable3ScreenStatements.Generation;
		_boxRoot = Fable3ScreenBoxes.ScreenRoot;

		// The box manager's screen first; else the save menu, which is no box — its open, its row
		// and its `allowed` are stated by its own calls (sub_8296C5D0 / sub_8296CB08 / sub_8296C858,
		// Fable3UiStatements).
		string? root = _boxRoot;
		Fable3ScreenState screen = Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox);
		if (root is null && Fable3UiStatements.SaveMenuOpen)
		{
			root = Fable3BoxScreenModel.SaveMenu;
			screen = new Fable3ScreenState
			{
				SaveRow = Fable3UiStatements.SaveMenuRow,
				SaveAllowed = Fable3UiStatements.SaveMenuAllowed,
			};
		}

		if (root is not null)
		{
			Collect(root);
			if (root == _boxRoot)
			{
				Fable3ScreenStatements.NoteRootState(Fable3ScreenBoxes.ScreenBox, _stated.TryGetValue(root, out Fable3ScreenModel.Probe probe) ? probe.State : null);
				screen = Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox);
			}
		}

		_root = root;
		_page = root is null ? null : Fable3ScreenModel.Compose(root, _stated, screen, Resolve);
		return _page;
	}

	protected override void Frame(double delta)
	{
		base.Frame(delta);
		// Each local player's pad (pad 0 carries the bound keys and the pointer's leftovers too: a right
		// click is its B, Engine_UiPointer.HandLeftoversToPad), edge-detected on its own.
		uint pressedOne = _edges.Pressed(Fable3ActingPlayer.One, PadDown(Fable3ActingPlayer.One));
		uint pressedTwo = _edges.Pressed(Fable3ActingPlayer.Two, PadDown(Fable3ActingPlayer.Two));
		if (_page is null || _root is null || !Fable3BoxAnswers.OwnsScreen(_root) || !Refii.Host.HostBridge.IsGuestUp ||
			Fable3HostOptions.IsOpen)
		{
			return;
		}

		// A press counts for the player the screen takes it from (the box's own owner rule,
		// Fable3BoxAnswers.TakesPlayer): Godot's cursor moves only for that player, and the lever carries them.
		Fable3ScreenState screen = Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox);
		foreach ((int player, uint pressed) in new[] { (Fable3ActingPlayer.One, pressedOne), (Fable3ActingPlayer.Two, pressedTwo) })
		{
			var press = new Fable3BoxAnswers.Press(
				(pressed & Refii.Game.GameInput.ButtonA) != 0, (pressed & Refii.Game.GameInput.ButtonB) != 0,
				(pressed & Refii.Game.GameInput.ButtonX) != 0, (pressed & Refii.Game.GameInput.ButtonY) != 0,
				(pressed & Refii.Game.GameInput.DpadUp) != 0, (pressed & Refii.Game.GameInput.DpadDown) != 0,
				(pressed & Refii.Game.GameInput.DpadLeft) != 0, (pressed & Refii.Game.GameInput.DpadRight) != 0);
			if (press.Any && Fable3BoxAnswers.TakesPlayer(_root, screen, player))
			{
				Take(Fable3BoxAnswers.ForScreen(_root, Fable3ScreenBoxes.ScreenBox, press, Working(), Muted(), player), player);
				return;
			}
		}
	}

	/// <summary>The buttons down on local <paramref name="player"/>'s pad (GameInput.Seen: every source folded).</summary>
	private static uint PadDown(int player) => Refii.Game.GameInput.Seen(Fable3ActingPlayer.PadOf(player)).Buttons;

	private Fable3ScreenState Working() => Fable3ScreenStatements.Working(Fable3ScreenBoxes.ScreenBox);

	private bool Muted() => Fable3BoxScreenModel.ChatMuted(Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox));

	/// <summary>Posts a step's lever and takes its working values (the page is composed again from them).</summary>
	private void Take(Fable3BoxAnswers.ScreenStep step, int player)
	{
		if (!step.Any)
		{
			return;
		}

		Fable3ScreenState working = Working();
		if (step.Tier is { } tier)
		{
			working.Tier = tier;
		}

		if (step.Muted is { } muted)
		{
			working.Muted = muted;
		}

		if (step.Cursor is { } cursor)
		{
			working.Cursor = cursor;
		}

		if (step.Sleep is { } sleep)
		{
			working.BedSleep = sleep;
		}

		if (step.Sex is { } sex)
		{
			working.BedSex = sex;
		}

		if (step.StatsAchievements is { } onAchievements && working.StatsAchievements != onAchievements)
		{
			working.StatsAchievements = onAchievements;
			Fable3ScreenStatements.Touch();
		}

		if (step.OpenAchievements)
		{
			// The host's category: the achievements page, for the player whose pad drives this screen.
			Fable3Elements.Log($"[fable3-screens] {_root}: the Achievements category opens the achievements page");
			Fable3AchievementsPage.Open(player);
		}

		if (step.Lever is { } lever)
		{
			Fable3Elements.Log($"[fable3-screens] {_root}: '{lever}'");
			Refii.Host.HostBridge.PostFrontEndAction(lever);
		}
	}

	/// <summary>
	/// The save menu is stated the clicked row: its own steps take its cursor there (0.3 s a
	/// row, its pace) and the click selects the row once reached (<see cref="Fable3BoxAnswers.SaveMenuRow"/>). An
	/// owned screen's cursor is Godot's: the row is stated or picked by its lever
	/// (<see cref="Fable3BoxAnswers.PointScreen"/>). The other box-manager screens keep their cursors
	/// inside their pad handlers and take no row from outside, so theirs is walked
	/// (<see cref="Fable3PointerSteps"/>).
	/// </summary>
	protected override void PointAt(int row, bool clicked)
	{
		if (Fable3BoxAnswers.OwnsScreen(_root))
		{
			if (_page is { } page && row >= 0 && row < page.Rows.Count &&
				Fable3BoxAnswers.TakesPlayer(_root!, Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox), Fable3ActingPlayer.Pointer))
			{
				Take(Fable3BoxAnswers.PointScreen(_root!, Fable3ScreenBoxes.ScreenBox, page.Rows[row].Name, clicked, Working(),
					Muted(), Fable3ActingPlayer.Pointer), Fable3ActingPlayer.Pointer);
			}

			return;
		}

		if (_root != Fable3BoxScreenModel.SaveMenu)
		{
			base.PointAt(row, clicked);
			return;
		}

		if ((clicked || row != _page?.Selected) && Fable3BoxAnswers.SaveMenuRow(row, clicked, Fable3ActingPlayer.Pointer) is { } action)
		{
			Refii.Host.HostBridge.PostFrontEndAction(action);
		}
	}

	/// <summary>
	/// The values stated under <paramref name="root"/> in the document that last wrote under its
	/// first segment — two .bgf share a root (foodquerybox's and proposalquerybox's `InfoBox`), so
	/// only the latest one is the screen showing; a reloaded document starts clean
	/// (<see cref="Fable3Elements.Load"/>). No presentation is waited on: a root nothing wrote yet
	/// collects nothing and the model composes the screen from what its calls stated.
	/// </summary>
	private void Collect(string root)
	{
		_stated.Clear();
		int dot = root.IndexOf('.');
		if (Fable3Elements.LatestDocumentOf(dot < 0 ? root : root[..dot]) is not { } document)
		{
			return;
		}

		string prefix = root + ".";
		foreach (KeyValuePair<(string Document, string Path), Fable3Elements.Element> entry in Fable3Elements.Of(document))
		{
			string path = entry.Key.Path;
			if (path == root || path.StartsWith(prefix, System.StringComparison.Ordinal))
			{
				Fable3Elements.Element element = entry.Value;
				// The writers' values as their calls passed them (`@Ui.State`, `@Ui.Visible`,
				// `@Ui.Opacity`, `@Ui.Text*`, `@Ui.Time`): no setter report has a route (Fable3Elements).
				_stated[path] = new Fable3ScreenModel.Probe(element.State, element.Hidden, element.StatedTextValue, element.Time);
			}
		}
	}
}
