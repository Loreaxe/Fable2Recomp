namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Input cadence for a screen's second input level (the options category page). Nothing is
/// drawn: the level stays guest-mirrored; this only supplies the auto-repeat the guest's stick
/// translator would (the masked D-pad never reaches it) and posts the codes. 36 commits the
/// whole page (`sub_829BF698`); nothing is written while scrolling. Each axis has its own clock.
///
/// Two gates on the guest's side shape what is sent. The page's repeat gate (`sub_82881B28`)
/// drops a second 28..31 within 0.3 s of the first unless a release (32..35) came between —
/// `sub_828818E8` resets the gate's clocks on any release — so every press goes out WITH its
/// release and every tap lands. And the page group dispatches nothing to the page for its first
/// half second (`sub_829907F0`: `timer <= 0.5` returns) while the screen's own handler still
/// takes a 36 and closes the page without committing (`sub_826DA228` case 36 → `sub_826D9F28`),
/// so the codes of that window are HELD here and posted when it ends — not dropped, not early.
/// A host page (<see cref="Engine_UiHostOptionsPage"/>) has no guest and no gates.
/// </summary>
public sealed class Engine_UiScreenLevel
{
	private Engine_UiNavigationClock? _vertical;

	private Engine_UiNavigationClock? _horizontal;

	/// <summary>`sub_829907F0` ignores the page for its first half second; so do the pad and the pointer.</summary>
	public const double OpenQuiet = 0.55;

	/// <summary>Codes pressed inside the quiet window, posted in order when it ends.</summary>
	private readonly System.Collections.Generic.Queue<uint> _held = new();

	private const int HeldCap = 16;

	private string _page = string.Empty;

	private double _openFor;

	private int _dragRow = -1;

	/// <summary>The row the pointer is walking the cursor to, or -1.</summary>
	private int _walkTarget = -1;

	/// <summary>A clock on the page gate's repeat numbers, not the menu's.</summary>
	private static Engine_UiNavigationClock PageClock() => new()
	{
		FirstInterval = Engine_UiScreenInput.PageFirstRepeat,
		SubsequentInterval = Engine_UiScreenInput.PageRepeat,
	};

	public void Drive(Engine_UiSurfaceInput input)
	{
		Follow(input.Delta);
		if (!Quiet)
		{
			Flush();
		}

		_vertical ??= PageClock();
		_horizontal ??= PageClock();

		for (int step = _vertical.Steps(input.Direction, input.Delta);
			step > 0; --step)
		{
			Post(input.Direction < 0 ? Engine_UiScreenInput.Up : Engine_UiScreenInput.Down);
		}

		for (int step = _horizontal.Steps(input.Horizontal, input.Delta);
			step > 0; --step)
		{
			Post(input.Horizontal < 0 ? Engine_UiScreenInput.Left : Engine_UiScreenInput.Right);
		}

		if (input.Accept)
		{
			Post(Engine_UiScreenInput.Select);
		}
		else if (input.Back)
		{
			Post(Engine_UiScreenInput.Back);
		}
	}

	/// <summary>Restarts the page clock when the open page changes; advances it by <paramref name="delta"/>.</summary>
	private void Follow(double delta)
	{
		string page = Engine_UiOptionsPending.Page;
		if (page != _page)
		{
			_page = page;
			_openFor = 0.0;
			_dragRow = -1;
			_walkTarget = -1;
			_held.Clear();
		}

		_openFor += delta;
	}

	/// <summary>Whether a guest page is still inside its quiet window.</summary>
	private bool Quiet => !Engine_UiHostOptionsPage.Open && _openFor < OpenQuiet;

	/// <summary>A code from the pad: held through the quiet window, otherwise sent after anything held.</summary>
	private void Post(uint code)
	{
		if (Quiet)
		{
			if (_held.Count < HeldCap)
			{
				_held.Enqueue(code);
			}

			return;
		}

		Flush();
		Send(code);
	}

	private void Flush()
	{
		while (_held.Count > 0)
		{
			Send(_held.Dequeue());
		}
	}

	/// <summary>
	/// One code to the page. A guest page gets the press and, for 28..31, its release, so its
	/// repeat gate takes the next tap; a host page has no guest, only the host's record moves.
	/// </summary>
	private static void Send(uint code)
	{
		if (Engine_UiHostOptionsPage.Open)
		{
			Engine_UiOptionsPending.Track(code);
			return;
		}

		Engine_UiSource.Current.ScreenAction(code);
		if (code >= Engine_UiScreenInput.Up && code <= Engine_UiScreenInput.Right)
		{
			Engine_UiSource.Current.ScreenAction(
				code + (Engine_UiScreenInput.UpReleased - Engine_UiScreenInput.Up));
		}
	}

	// ---------------------------------------------------------------------------------------
	// THE MOUSE ON AN OPTIONS PAGE
	// ---------------------------------------------------------------------------------------
	//
	// The guest owns the row and every value; the pointer only posts the codes the keys would.
	// Hovering a row walks the cursor to it; a click or drag on a slider steps it to the point
	// under the pointer; a pickbox steps back on its left half and forward on its right; the
	// wheel over a value row steps it; the page's A and B prompts commit and cancel. Every other
	// click is taken, so a stray one cannot commit the page. A guest page's cursor moves on the
	// guest's frame, so a walk is one pair per frame and a value step waits until it has arrived.

	/// <summary>Each page's rows, top to bottom, as the guest orders them.</summary>
	private static readonly System.Collections.Generic.Dictionary<string, string[]> Rows = new()
	{
		["Game"] = new[]
		{
			"Options.Subtitles_pickbox", "Options.BreadcrumbSize_pickbox",
			"Options.TutorialBoxes_pickbox", "Options.MultiplayerOrbs_pickbox",
			"Options.Auto_joinable_pickbox",
		},
		["Controls"] = new[] { "Page2.Contents.pickbox1" },
		["Video"] = new[] { "Page3.Contents.slider1" },
		["Audio"] = new[]
		{
			"Page4.Contents.slider1", "Page4.Contents.slider2", "Page4.Contents.slider3",
			"Page4.Contents.pickbox1",
		},
	};

	/// <summary>One frame of the pointer on the open page of <paramref name="book"/>.</summary>
	public void Pointer(Engine_UiOwnedBook book, double delta)
	{
		// Drive, which runs after the pointer every frame, advances the clock.
		Follow(0.0);

		// A host page stands on the shared five rows whatever it is called.
		string[]? rows = Engine_UiHostOptionsPage.Open ? Engine_UiHostOptionsPage.Rows : null;
		if (rows is null && !Rows.TryGetValue(_page, out rows))
		{
			return;
		}

		if (Engine_UiPointer.Canvas is not { } at)
		{
			return;
		}

		bool left = Engine_UiPointer.TakeLeft();
		int wheel = Engine_UiPointer.TakeWheel();
		if (_openFor < OpenQuiet)
		{
			return;
		}

		Walk();

		// A slider drag holds its row while the button is down, wherever the pointer goes.
		if (_dragRow >= 0)
		{
			if (Godot.Input.IsMouseButtonPressed(Godot.MouseButton.Left) && _dragRow < rows.Length)
			{
				SetSlider(book, rows[_dragRow], _dragRow, at);
				return;
			}

			_dragRow = -1;
		}

		if (left && PromptAt(book, at) is { } prompt)
		{
			Send(prompt);
			return;
		}

		if (Engine_UiHostOptionsPage.Current is { } host && wheel != 0)
		{
			WalkTo(System.Math.Clamp(Engine_UiOptionsPending.Row - wheel, 0, host.Count - 1));
			return;
		}
		int row = RowAt(book, rows, at);
		if (row < 0)
		{
			return;
		}

		int target = row + Engine_UiHostOptionsPage.VisibleStart;
		if (Engine_UiPointer.Moved || left || wheel != 0)
		{
			WalkTo(target);
		}

		// A value step lands on the cursor's row: none goes out until the cursor is on this one.
		if (Engine_UiOptionsPending.Row != target)
		{
			return;
		}

		bool slider = Engine_UiOptionsPending.SliderValue(row) is not null;
		if (left)
		{
			if (slider)
			{
				_dragRow = row;
				SetSlider(book, rows[row], row, at);
			}
			else if (book.NodeFor(rows[row]) is { } box &&
				Engine_UiPointer.CanvasBounds(box) is { } bounds)
			{
				Step(at.X < bounds.GetCenter().X ? -1 : 1);
			}
		}

		if (wheel != 0)
		{
			// Wheel up raises the value, as on a PC slider or spinner.
			for (int i = 0; i < System.Math.Abs(wheel); ++i)
			{
				Step(wheel < 0 ? 1 : -1);
			}
		}
	}

	/// <summary>Starts walking the cursor to <paramref name="row"/>.</summary>
	private void WalkTo(int row)
	{
		_walkTarget = row;
		Walk();
	}

	/// <summary>
	/// One 28/29 pair per row the cursor has still to move. A host page's record moves at once,
	/// so the walk completes here; a guest page's cursor moves on its own frame and arrives
	/// with the guest's statement, so the target is kept until then.
	/// </summary>
	private void Walk()
	{
		for (int i = 0; i < 32 && _walkTarget >= 0 && Engine_UiOptionsPending.Row != _walkTarget; ++i)
		{
			int before = Engine_UiOptionsPending.Row;
			Send(_walkTarget < before ? Engine_UiScreenInput.Up : Engine_UiScreenInput.Down);
			if (Engine_UiOptionsPending.Row == before)
			{
				return;
			}
		}

		_walkTarget = -1;
	}

	/// <summary>One value step on the cursor's row.</summary>
	private static void Step(int direction) =>
		Send(direction < 0 ? Engine_UiScreenInput.Left : Engine_UiScreenInput.Right);

	/// <summary>
	/// Steps a slider to the value under the pointer: the pip travels `x = v·440 − 210` in the
	/// slider group's own units (`sub_82881E38`), in twentieths. Nothing until the cursor is on
	/// the row — a step lands on the cursor's widget.
	/// </summary>
	private void SetSlider(Engine_UiOwnedBook book, string path, int row, Godot.Vector2 at)
	{
		if (book.NodeFor(path + ".grp") is not { } group ||
			Engine_UiOptionsPending.SliderValue(row) is not { } value)
		{
			return;
		}

		WalkTo(row);
		if (Engine_UiOptionsPending.Row != row)
		{
			return;
		}

		Godot.Vector3 local = group.GlobalTransform.AffineInverse() *
			new Godot.Vector3(at.X, at.Y, group.GlobalPosition.Z);
		float wanted = Godot.Mathf.Clamp((local.X + 210f) / 440f, 0f, 1f);
		int steps = Godot.Mathf.RoundToInt((wanted - value) * 20f);
		for (int i = 0; i < System.Math.Abs(steps); ++i)
		{
			Step(steps < 0 ? -1 : 1);
		}
	}

	/// <summary>
	/// Which row a canvas point is on: within the row's own height, anywhere across the page —
	/// the Game page authors its labels apart from its boxes.
	/// </summary>
	private static int RowAt(Engine_UiOwnedBook book, string[] rows, Godot.Vector2 at)
	{
		for (int i = 0; i < rows.Length; ++i)
		{
			if (book.NodeFor(rows[i]) is { } node && node.IsVisibleInTree() &&
				Engine_UiPointer.CanvasBounds(node) is { } bounds &&
				at.Y >= bounds.Position.Y - 6f && at.Y <= bounds.End.Y + 6f &&
				at.X >= bounds.Position.X - 460f && at.X <= bounds.End.X + 40f)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>The code a drawn A or B prompt on the page stands for, under the pointer.</summary>
	private static uint? PromptAt(Engine_UiOwnedBook book, Godot.Vector2 at)
	{
		if (Engine_UiHostOptionsPage.Open)
		{
			foreach ((string path, uint code) in new[]
				{ ("Learn", Engine_UiScreenInput.Select), ("Unlearn", Engine_UiScreenInput.Back) })
			{
				if (book.NodeFor(Engine_UiHostOptionsPage.Buttons + "." + path) is { } node &&
					node.IsVisibleInTree() && Engine_UiPointer.CanvasBounds(node) is { } bounds &&
					bounds.Grow(4f).HasPoint(at)) return code;
			}
			return null;
		}

		foreach ((string name, uint code) in new[]
			{ ("icon_button_a", Engine_UiScreenInput.Select), ("icon_button_b", Engine_UiScreenInput.Back) })
		{
			if (Find(book, name, at))
			{
				return code;
			}
		}

		return null;
	}

	private static bool Find(Godot.Node node, string name, Godot.Vector2 at)
	{
		if (node is Godot.Node3D { Visible: false })
		{
			return false;
		}

		if (node is Godot.Node3D found && found.Name == name &&
			Engine_UiPointer.CanvasBounds(found) is { } bounds && bounds.Grow(4f).HasPoint(at))
		{
			return true;
		}

		foreach (Godot.Node child in node.GetChildren())
		{
			if (Find(child, name, at))
			{
				return true;
			}
		}

		return false;
	}
}
