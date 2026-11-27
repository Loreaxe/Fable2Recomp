namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Game;
using Refii.Ui;

/// <summary>
/// A host options page in Fable 3 — the shared Controls page (<see cref="Engine_UiControlsPage"/>:
/// glyph style and pack, the pad remap, every key binding, the mods' actions) — on the one menu
/// template (<see cref="Fable3ListScreen"/>), driven by the host: up/down move, left/right step a
/// row, B leaves. A is the page's: on a value page it saves; on a page whose rows take it
/// (<see cref="Engine_UiHostOptionsPage.RowsTakeAccept"/>: the Controls page, the settings hub) A or
/// a click acts on the row — a rebind row listens for its input, an opener opens — and X saves.
/// While it is up the guest's pad is neutral (<see cref="Fable3Input"/>).
/// </summary>
/// <remarks>
/// WHERE IT OPENS. Fable 3's Options has no controller page — its Game Settings "Controller" row
/// is Invert Aim (+31, set by sub_825CAA98 / sub_825CB028) — and its input handler sub_825CA410
/// takes only A (74), B (75) and Y (77) on every page: X (76) falls to `li r3, 1` (`cmpwi r3, 0x4D`
/// / `bne` at 0x825ca760 → 0x825ca84c). So X over the Game Settings pages (3, 4) opens this page
/// with no row invented in the guest's list and the guest's cursor untouched; Fable3Options shows
/// the prompt.
/// </remarks>
public partial class Fable3HostOptions : Fable3ListScreen
{
	private const double RepeatDelay = 0.35;
	private const double RepeatEvery = 0.09;
	private const int StickDeadZone = 16000;

	private static Fable3HostOptions? _instance;

	/// <summary>
	/// Whether a host page is up — an options page, or one of the host's own surfaces
	/// (<see cref="Fable3HostSurface"/>): the guest's pad is neutral meanwhile.
	/// </summary>
	public static new bool IsOpen => Engine_UiHostOptionsPage.Current is not null || Fable3HostSurface.AnyUp;

	/// <summary>The cursor row on the open page.</summary>
	public static int Cursor => _instance?._cursor ?? 0;

	/// <summary>The pending index of a row on the open page.</summary>
	public static int IndexOf(int row) => _instance is { } screen && row >= 0 && row < screen._indices.Length ? screen._indices[row] : 0;

	private int[] _indices = System.Array.Empty<int>();
	private int _cursor;
	private Engine_UiPressEdges _edges;
	private int _heldStep;
	private double _repeat;

	protected override string LogTag => "fable3-host-options";

	public override void _Ready()
	{
		base._Ready();
		_instance = this;
	}

	/// <summary>The pages under the open one (the hub under a settings page), B returns to the top of it.</summary>
	private static readonly Stack<(Engine_UiHostOptionsPage Page, int Cursor)> _under = new();

	/// <summary>Opens <paramref name="page"/> over the open one; B on it comes back.</summary>
	public static void Push(Engine_UiHostOptionsPage page)
	{
		if (Engine_UiHostOptionsPage.Current is { } open)
		{
			_under.Push((open, Cursor));
		}

		Enter(page);
	}

	/// <summary>Opens <paramref name="page"/> straight over the guest's Options (its Controller tab's row); B comes back to it.</summary>
	public static void Open(Engine_UiHostOptionsPage page)
	{
		_under.Clear();
		Enter(page);
	}

	/// <summary>Opens <paramref name="page"/> at its committed values.</summary>
	public static void Enter(Engine_UiHostOptionsPage page)
	{
		Engine_UiHostOptionsPage.Current = page;
		if (_instance is { } screen)
		{
			screen._indices = page.Read();
			screen._cursor = 0;
			// The press that opened it is not a press on the page.
			screen._edges.Hold();
		}
	}

	/// <summary>Moves one row to <paramref name="index"/> (a rebind row's captured input, a reset row's defaults).</summary>
	public static void SetIndex(int row, int index)
	{
		if (_instance is { } screen && row >= 0 && row < screen._indices.Length)
		{
			screen._indices[row] = index;
		}
	}

	private static void Leave()
	{
		Engine_UiInputCapture.Cancel();
		if (_under.Count > 0)
		{
			(Engine_UiHostOptionsPage page, int cursor) = _under.Pop();
			Enter(page);
			if (_instance is { } screen)
			{
				screen._cursor = cursor;
			}

			return;
		}

		Engine_UiHostOptionsPage.Current = null;
		Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
	}

	protected override Fable3ScreenPage? Compose(double delta)
	{
		// While a capture listens (and until its input is let go) the page does not have the pad.
		uint down = GameInput.Pad0.Buttons;
		uint pressed = 0u;
		if (Engine_UiInputCapture.Blocking)
		{
			_edges.Hold();
		}
		else
		{
			pressed = _edges.Take(down);
		}

		if (Engine_UiHostOptionsPage.Current is not { } page)
		{
			// X over the guest's Game Settings pages opens the Controls page (the guest ignores X there).
			if ((pressed & GameInput.ButtonX) != 0 && Fable3Options.OffersControls)
			{
				_under.Clear();
				Enter(Fable3SettingsHub.Page);
				GD($"opened '{Fable3SettingsHub.Page.Title}' from the guest's Game Settings (X)");
			}

			return null;
		}

		if (_indices.Length != page.Count)
		{
			_indices = page.Read();
			_cursor = System.Math.Clamp(_cursor, 0, System.Math.Max(0, page.Count - 1));
		}

		// A capture is listening for the next press: the page waits.
		if (!Engine_UiInputCapture.Blocking)
		{
			Drive(page, pressed, down, delta);
		}

		if (Engine_UiHostOptionsPage.Current is null)
		{
			return null;
		}

		var rows = new List<Fable3ScreenPage.Row>(page.Count);
		for (int row = 0; row < page.Count; ++row)
		{
			string caption = row == _cursor && Engine_UiInputCapture.Armed is not null
				? Refii.Text.Engine_OwnWords.Say("REFII_PRESS_AN_INPUT")
				: page.RowsTakeAccept ? page.Caption(row, _indices[row], CommitButton) : page.Caption(row, _indices[row]);
			rows.Add(new Fable3ScreenPage.Row("row" + row, page.RowTitle(row) + (caption.Length > 0 ? "    " + caption : string.Empty)));
		}

		var prompts = new List<Fable3ScreenPage.Prompt>(3);
		foreach ((string button, string word) in Prompts(page.RowsTakeAccept, page.Commits))
		{
			prompts.Add(new Fable3ScreenPage.Prompt(button, Refii.Text.Engine_OwnWords.Say(word)));
		}

		return new Fable3ScreenPage(page.Title, rows, _cursor, string.Empty, prompts, true);
	}

	/// <summary>The button that saves a page whose rows take A.</summary>
	public const string CommitButton = "X";

	/// <summary>
	/// The prompts of a host page, button and own word: a page of openers offers A Open; a page
	/// whose rows take A offers A Change and <see cref="CommitButton"/> Save; a value page A Save.
	/// B Back on all. Pure.
	/// </summary>
	public static IReadOnlyList<(string Button, string Word)> Prompts(bool rowsTakeAccept, bool commits) =>
		!commits ? new[] { ("A", "REFII_OPEN"), ("B", "REFII_BACK") } :
		rowsTakeAccept ? new[] { ("A", "REFII_CHANGE"), (CommitButton, "REFII_SAVE"), ("B", "REFII_BACK") } :
		new[] { ("A", "REFII_SAVE"), ("B", "REFII_BACK") };

	/// <summary>
	/// Whether a click on a row is A on it: on a page whose rows take A, a click on the cursor row,
	/// or on any row that has nothing to step (an opener opens on the first click, as a list
	/// page's row does). Otherwise the click places the cursor or steps a value row on. Pure.
	/// </summary>
	public static bool ClickAccepts(bool rowsTakeAccept, bool onCursor, int choices) =>
		rowsTakeAccept && (onCursor || choices <= 1);

	/// <summary>The page's commit: every row's pending index to the page; it leaves when the page took them.</summary>
	private void Save(Engine_UiHostOptionsPage page)
	{
		int[] indices = _indices;
		if (page.Commit(row => row >= 0 && row < indices.Length ? indices[row] : 0))
		{
			GD($"'{page.Title}' saved");
			Leave();
		}
	}

	/// <summary>A on <paramref name="row"/> of a page whose rows take it: the row goes where the page says, and is told.</summary>
	private void Accept(Engine_UiHostOptionsPage page, int row)
	{
		if (row < 0 || row >= _indices.Length)
		{
			return;
		}

		int at = page.Accepted(row, _indices[row]);
		if (Engine_UiHostOptionsPage.Current != page || row >= _indices.Length)
		{
			// The row opened another page: the indices are that page's now.
			return;
		}

		_indices[row] = at;
		page.Changed(row, at);
	}

	private void Drive(Engine_UiHostOptionsPage page, uint pressed, uint down, double delta)
	{
		if ((pressed & GameInput.ButtonA) != 0)
		{
			if (page.RowsTakeAccept)
			{
				Accept(page, _cursor);
			}
			else
			{
				Save(page);
			}

			return;
		}

		if (page.RowsTakeAccept && page.Commits && (pressed & GameInput.ButtonX) != 0)
		{
			Save(page);
			return;
		}

		if ((pressed & GameInput.ButtonB) != 0)
		{
			GD($"'{page.Title}' left unsaved");
			Leave();
			return;
		}

		GameInput.PadState pad = GameInput.Pad0;
		int vertical = (down & GameInput.DpadUp) != 0 || pad.LeftY > StickDeadZone ? -1 :
			(down & GameInput.DpadDown) != 0 || pad.LeftY < -StickDeadZone ? 1 : 0;
		int horizontal = (down & GameInput.DpadLeft) != 0 || pad.LeftX < -StickDeadZone ? -1 :
			(down & GameInput.DpadRight) != 0 || pad.LeftX > StickDeadZone ? 1 : 0;
		int step = vertical != 0 ? vertical * 2 : horizontal;
		if (step != _heldStep)
		{
			_heldStep = step;
			_repeat = RepeatDelay;
		}
		else if (step == 0 || (_repeat -= delta) > 0.0)
		{
			return;
		}
		else
		{
			_repeat = RepeatEvery;
		}

		if (vertical != 0 && page.Count > 0)
		{
			_cursor = (_cursor + vertical + page.Count) % page.Count;
		}
		else if (horizontal != 0 && _cursor < _indices.Length)
		{
			int choices = System.Math.Max(1, page.Choices(_cursor));
			_indices[_cursor] = (_indices[_cursor] + horizontal + choices) % choices;
			page.Changed(_cursor, _indices[_cursor]);
		}
	}

	/// <summary>
	/// The page's cursor is ours: a click moves it to the row; a click that is A on the row
	/// (<see cref="ClickAccepts"/>) accepts it; on a value page a click on the cursor row steps the row on.
	/// </summary>
	protected override void PointAt(int row, bool clicked)
	{
		if (Engine_UiHostOptionsPage.Current is not { } page || row < 0 || row >= _indices.Length)
		{
			return;
		}

		if (clicked && ClickAccepts(page.RowsTakeAccept, row == _cursor, page.Choices(row)))
		{
			_cursor = row;
			Accept(page, row);
			return;
		}

		if (clicked && row == _cursor)
		{
			int choices = System.Math.Max(1, page.Choices(row));
			_indices[row] = (_indices[row] + 1) % choices;
			page.Changed(row, _indices[row]);
		}

		_cursor = row;
	}

	private static void GD(string line) => Godot.GD.Print($"[fable3-host-options] {line}");
}
