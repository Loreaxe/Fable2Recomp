namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Game;
using Refii.Ui;

/// <summary>
/// A host options page in Fable 3 — the shared Controls page (<see cref="Engine_UiControlsPage"/>:
/// glyph style and pack, the pad remap, every key binding, the mods' actions) — on the one menu
/// template (<see cref="Fable3ListScreen"/>), driven by the host: up/down move, left/right step a
/// row, A saves, B leaves. While it is up the guest's pad is neutral (<see cref="Fable3Input"/>).
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

	/// <summary>Whether a host page is up: the guest's pad is neutral meanwhile.</summary>
	public static new bool IsOpen => Engine_UiHostOptionsPage.Current is not null;

	/// <summary>The cursor row on the open page.</summary>
	public static int Cursor => _instance?._cursor ?? 0;

	/// <summary>The pending index of a row on the open page.</summary>
	public static int IndexOf(int row) => _instance is { } screen && row >= 0 && row < screen._indices.Length ? screen._indices[row] : 0;

	private int[] _indices = System.Array.Empty<int>();
	private int _cursor;
	private uint _wasDown;
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

	/// <summary>Opens <paramref name="page"/> at its committed values.</summary>
	public static void Enter(Engine_UiHostOptionsPage page)
	{
		Engine_UiHostOptionsPage.Current = page;
		if (_instance is { } screen)
		{
			screen._indices = page.Read();
			screen._cursor = 0;
			// The press that opened it is not a press on the page.
			screen._wasDown = GameInput.Pad0.Buttons;
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
		uint down = GameInput.Pad0.Buttons;
		uint pressed = down & ~_wasDown;
		_wasDown = down;

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
			string caption = row == _cursor && Engine_UiInputCapture.Armed is not null ? Refii.Text.Engine_OwnWords.Say("REFII_PRESS_AN_INPUT") : page.Caption(row, _indices[row]);
			rows.Add(new Fable3ScreenPage.Row("row" + row, page.RowTitle(row) + (caption.Length > 0 ? "    " + caption : string.Empty)));
		}

		var prompts = new[]
		{
			new Fable3ScreenPage.Prompt("A", Refii.Text.Engine_OwnWords.Say(page is Fable3SettingsHub ? "REFII_OPEN" : "REFII_SAVE")),
			new Fable3ScreenPage.Prompt("B", Refii.Text.Engine_OwnWords.Say("REFII_BACK")),
		};
		return new Fable3ScreenPage(page.Title, rows, _cursor, string.Empty, prompts, true);
	}

	private void Drive(Engine_UiHostOptionsPage page, uint pressed, uint down, double delta)
	{
		if ((pressed & GameInput.ButtonA) != 0)
		{
			int[] indices = _indices;
			if (page.Commit(row => row >= 0 && row < indices.Length ? indices[row] : 0))
			{
				GD($"'{page.Title}' saved");
				Leave();
			}

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

	/// <summary>The page's cursor is ours: a click moves it to the row, a click on the cursor row steps the row on.</summary>
	protected override void PointAt(int row, bool clicked)
	{
		if (Engine_UiHostOptionsPage.Current is not { } page || row < 0 || row >= _indices.Length)
		{
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
