namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// The element-written box-manager screens — hero status, stats, promises, quest summary, the
/// king's schedule and the ones <see cref="Fable3BoxScreenModel"/> composes — and the save menu,
/// drawn on the one menu template (<see cref="Fable3ListScreen"/>) from what the
/// guest states (<see cref="Fable3ScreenModel"/>). The screen showing is the box manager's
/// (display_box_hooks.cpp names its class; <see cref="Fable3ScreenBoxes.ScreenRoot"/> is its
/// element root); its texts are the stated texts under that root, its cursor row, ghosting and
/// rows what its own calls stated for its box (<see cref="Fable3ScreenStatements"/>). The pad
/// reaches the guest unchanged, which moves its own cursor and closes its own screen.
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

	protected override string LogTag => "fable3-screens";

	protected override string LogContext => $"{_root} ";

	protected override bool Framed => Fable3ScreenFrames.FrameOf(_root) is not null;

	/// <summary>
	/// The save menu's rows are choices, centred as the Options box centres its save section's
	/// (`Sections.SaveManagement.SelectionText.*`, halign 1 at x 0); its own `SaveMenu.bgf` ships
	/// in no build. The other box screens keep their rows left.
	/// </summary>
	protected override bool RowsOnCentreLine => _root == Fable3BoxScreenModel.SaveMenu;

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
		}

		_root = root;
		_page = root is null ? null : Fable3ScreenModel.Compose(root, _stated, screen, Resolve);
		return _page;
	}

	/// <summary>
	/// The save menu is stated the clicked row: its own steps take its cursor there (0.3 s a
	/// row, its pace) and the click selects the row once reached (<see cref="Fable3BoxAnswers.SaveMenuRow"/>). The box
	/// manager's screens keep their cursors inside their pad handlers and take no row from
	/// outside, so theirs is walked (<see cref="Fable3PointerSteps"/>).
	/// </summary>
	protected override void PointAt(int row, bool clicked)
	{
		if (_root != Fable3BoxScreenModel.SaveMenu)
		{
			base.PointAt(row, clicked);
			return;
		}

		if ((clicked || row != _page?.Selected) && Fable3BoxAnswers.SaveMenuRow(row, clicked) is { } action)
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
