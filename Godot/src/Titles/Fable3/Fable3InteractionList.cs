namespace Refii.Titles.Fable3;

using System;
using Godot;
using Refii.Core;
using Refii.Game;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// THE INTERACTION LIST (the Sanctuary's lists, the shops), drawn by Godot in the PC arrangement
/// from what the guest states, with the 360 banks' own pieces: a bar on every row, tight rows, the
/// cursor row larger, and above them a selection tab holding the cursor row's label behind its A
/// prompt. One arrangement on every device; the pad and the pointer are both live.
/// </summary>
/// <remarks>
/// <para>WHAT THE GUEST STATES. The list is a three-row window on the mode's rows, the cursor row
/// first: its row writer (TU3 sub_82972A70, disc sub_82963DE0) puts the row at the cursor into
/// `Buttons.Button3` and the next two into `Button2` / `Button1` through the button writer —
/// `@Interaction.Button` (label, enabled) — commits "" to a row past the end (`@Ui.Text`), and its
/// cursor and count arrive as `@Interaction.List`. `@Interaction.ListUp` says a list is being written
/// at all (until its hide or its mode's end), and the list root's slide is 1 hidden, 2 shown, 3 / 4
/// the scroll's entries (`@Ui.State`). Nothing of the guest's is read.</para>
/// <para>THE POINTER. Hover lights a row (Godot's own highlight: the guest's cursor is always the
/// first row of the window, so moving it under a resting pointer would run the list away). A click
/// on a row steps the guest's cursor to it — one `interaction-list:&lt;p&gt;:down` at a time, each
/// waiting for the cursor the guest states — and then presses A, the pad's own accept, once the
/// stated cursor is that row. The wheel and the two arrows step one row. A pad move drops a walk.
/// The tab's prompt is its button under the pointer (<see cref="Fable3Prompts"/>).</para>
/// <para>THE REAL-ESTATE AND CRIME MODES' ROWS are their populates' own (<see cref="Fable3PopulatedRows"/>):
/// while they are the list up, each window row's label is its stated tag in the game's book; a
/// real-estate row is asked by its id on a click (`realestate:&lt;p&gt;:row:&lt;id&gt;`) instead of walking
/// the cursor to it.</para>
/// <para>THE SANCTUARY (mode 3) is the same view on Godot's own list: its rows are the ones the mode kept,
/// as the guest states them while it fills the mode (<see cref="Fable3GuiWorld"/>; its row writer states
/// no window for it), the cursor is Godot's and goes to the guest as `guiworld:&lt;p&gt;:cursor:&lt;row&gt;`, a
/// click is the cursor and A, the wheel and the arrows move the cursor. No walk, no populated rows.</para>
/// <para>THE ART is the 360's: `buttonbox_24x24` (the list's own row backing) as the bar, and the
/// box screens' `selectionbar_*` four pieces as the tab and the arrows. Until the banks hold them a
/// flat panel stands in.</para>
/// </remarks>
public partial class Fable3InteractionList : Control
{
	public const string Root = "LowerElements.InteractionMenuScrollableList";

	/// <summary>The window's rows in order from the cursor (the row writer's Button3, Button2, Button1).</summary>
	public static readonly string[] RowElements =
	{
		Root + ".Buttons.Button3", Root + ".Buttons.Button2", Root + ".Buttons.Button1",
	};

	public const int WindowRows = 3;

	private const string BarArt = "art/gui_memmount/hudassests/version4/lowerelements/buttonbox_24x24.tex";
	private const string TabArt = "art/gui_memmount/hudassests/version4/middleelements/options/selectionbar_background.tex";
	private const string TabLinesArt = "art/gui_memmount/hudassests/version4/middleelements/options/selectionbar_lines.tex";
	private const string ArrowUpArt = "art/gui_memmount/hudassests/version4/middleelements/options/selectionbar_arrowtop.tex";
	private const string ArrowDownArt = "art/gui_memmount/hudassests/version4/middleelements/options/selectionbar_arrowbottom.tex";

	// THE ARRANGEMENT, in the document's canvas units about the screen's centre, y up. The 360
	// file stands the cursor row at (-451, -226.5) (LowerElements (-0.99, -265.5), the list
	// (-450, 49), Buttons (0, -10)) with rows 60 apart; here the tab takes that place and the rows
	// follow 36 apart, each bar 256 x 32 (the file's 2.56 x 0.32), the cursor row's 264.
	public const float TabX = -451f;
	public const float TabY = -226.5f;
	public const float RowX = -451f;
	public const float FirstRowY = -263.5f;
	public const float RowPitch = 36f;
	public const float BarWidth = 256f;
	public const float CursorBarWidth = 264f;
	public const float BarHeight = 32f;
	public const float ArrowWidth = 128f;
	public const float ArrowGap = 4f;

	/// <summary>The other rows against the cursor row: the PC list's 14 pt beside its 16 pt.</summary>
	public const float OtherRowScale = 14f / 16f;
	private const float CursorTextAlpha = 0.9f;
	private const float OtherTextAlpha = 0.7f;
	private const float OtherBarAlpha = 0.4f;
	private const float HoverBarAlpha = 0.75f;
	private const float DisabledAlpha = 0.5f;
	private const float TextInset = 14f;

	/// <summary>How long a posted step is waited on before it is asked again, and how often it may be.</summary>
	public const double StepWait = 0.3;
	public const int StepTries = 8;

	/// <summary>Whether the list shows: its own slide 2, 3 or 4, and the document's root neither faded (2) nor inactive.</summary>
	public static bool ShownAt(int? listSlide, int? rootSlide, bool? rootActive) =>
		listSlide is 2 or 3 or 4 && rootSlide != 2 && rootActive != false;

	/// <summary>How many rows the window holds with the cursor on <paramref name="cursor"/> of <paramref name="count"/>.</summary>
	public static int RowsShown(int cursor, int count) => Math.Clamp(count - cursor, 0, WindowRows);

	/// <summary>Which arrows stand: a row above the cursor; a row below it (the down step's own bound, cursor under count - 1).</summary>
	public static (bool Up, bool Down) Arrows(int cursor, int count) => (cursor > 0, cursor < count - 1);

	/// <summary>`interaction-list:&lt;p&gt;:down` / `:up`: one step of the guest's cursor through the list's own input.</summary>
	public static string Step(int player, bool down) =>
		"interaction-list:" + Fable3ActingPlayer.Field(player) + (down ? ":down" : ":up");

	/// <summary>A window row's bar in canvas units (centre and size): offset 0 is the cursor row.</summary>
	public static (float X, float Y, float Width, float Height) RowBox(int offset) =>
		(RowX + ((offset == 0 ? CursorBarWidth - BarWidth : 0f) * 0.5f), FirstRowY - (offset * RowPitch),
			offset == 0 ? CursorBarWidth : BarWidth, BarHeight);

	/// <summary>The selection tab's box in canvas units.</summary>
	public static (float X, float Y, float Width, float Height) TabBox => (TabX + ((CursorBarWidth - BarWidth) * 0.5f), TabY, CursorBarWidth, BarHeight);

	/// <summary>An arrow's box in canvas units: above the tab, or below the last of <paramref name="rows"/> rows.</summary>
	public static (float X, float Y, float Width, float Height) ArrowBox(bool up, int rows) =>
		(RowX, up ? TabY + BarHeight + ArrowGap : FirstRowY - (Math.Max(rows, 1) * RowPitch) + (RowPitch - BarHeight) - ArrowGap, ArrowWidth, BarHeight);

	/// <summary>What a walk asks for this frame.</summary>
	public enum Ask
	{
		Nothing,
		StepDown,
		StepUp,
		Accept,
	}

	/// <summary>
	/// A WALK of the guest's cursor to a row the pointer named: one step asked at a time, the next
	/// only once the guest states the cursor moved (or the wait ran out: the list's own gate refuses a
	/// step while its scroll plays), the accept only on the row itself. Pure.
	/// </summary>
	public sealed class Walk
	{
		private int _target = -1;
		private bool _accept;
		private int _askedFrom = -1;
		private double _askedAt;
		private int _tries;

		public bool Active => _target >= 0;
		public int Target => _target;

		/// <summary>Walks to the absolute row <paramref name="row"/>, accepting there when <paramref name="accept"/>.</summary>
		public void To(int row, bool accept)
		{
			_target = row;
			_accept = accept;
			_askedFrom = -1;
			_tries = 0;
		}

		public void Drop() => _target = -1;

		/// <summary>The next thing to ask with the guest's cursor stated at <paramref name="cursor"/> of <paramref name="count"/>.</summary>
		public Ask Next(int cursor, int count, double now)
		{
			if (_target < 0)
			{
				return Ask.Nothing;
			}

			if (_target >= count || cursor < 0)
			{
				Drop();
				return Ask.Nothing;
			}

			if (cursor == _target)
			{
				bool accept = _accept;
				Drop();
				return accept ? Ask.Accept : Ask.Nothing;
			}

			if (cursor == _askedFrom && now - _askedAt < StepWait)
			{
				return Ask.Nothing;
			}

			if (cursor != _askedFrom)
			{
				_tries = 0;
			}

			if (++_tries > StepTries)
			{
				Drop();
				return Ask.Nothing;
			}

			_askedFrom = cursor;
			_askedAt = now;
			return cursor < _target ? Ask.StepDown : Ask.StepUp;
		}
	}

	/// <summary>
	/// Whether a list has the pad: the guest is writing one, it holds a row, and it shows. The left
	/// stick is the list's then (its input takes the stick event, TU3 0x82972F2C..0x829731DC), so the
	/// world does not have the input and the cursor is free for the rows.
	/// </summary>
	public static bool HasPad
	{
		get
		{
			bool world = Fable3GuiWorld.ListUp;
			if (!world && (!Fable3UiStatements.InteractionListUp || Fable3UiStatements.InteractionList.Count <= 0))
			{
				return false;
			}

			Fable3Elements.Element? scene = Fable3Elements.Get(Fable3Elements.InteractionDocument, Fable3HudStatements.RootPath);
			return ShownAt(Fable3Elements.Get(Fable3Elements.InteractionDocument, Root)?.StatedState, scene?.StatedState, scene?.StatedActive);
		}
	}

	private readonly Walk _walk = new();
	private readonly Rect2[] _rowRects = new Rect2[WindowRows];
	private Rect2 _upRect, _downRect;
	private int _rows;
	private int _hover = -1;
	private Vector2 _pointerAt;
	private ulong _drawn = ulong.MaxValue;
	private bool _shown;
	private uint _padWas;
	private UiFont? _font;
	private string _culture = string.Empty;
	private Texture2D? _bar, _tab, _tabLines, _arrowUp, _arrowDown;
	private bool _artSaid;
	private double _artRetry;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _Process(double delta)
	{
		Fable3PopulatedRows.Observe(Fable3UiStatements.InteractionListUp);
		bool shown = HostBridge.IsInGame && HasPad;
		bool world = Fable3GuiWorld.ListUp;
		(int cursor, int count) = Window();
		if (!shown)
		{
			_walk.Drop();
			_hover = -1;
			if (_shown)
			{
				_shown = false;
				GD.Print("[fable3-interaction-list] down");
				QueueRedraw();
			}

			return;
		}

		LoadArt(delta);
		_rows = RowsShown(cursor, count);

		if (world)
		{
			_walk.Drop();
			Point(cursor, count);
			Redraw(cursor, count);
			return;
		}

		// A pad move is the player's own: it ends a walk the pointer began.
		uint pad = GameInput.Pad0.Buttons;
		bool padMoved = (pad & ~_padWas) != 0u || Mathf.Abs(GameInput.LeftStick.Y) > 0.5f;
		_padWas = pad;
		if (padMoved && _walk.Active)
		{
			_walk.Drop();
		}

		Point(cursor, count);
		switch (_walk.Next(cursor, count, Time.GetTicksMsec() / 1000.0))
		{
			case Ask.StepDown:
				HostBridge.PostFrontEndAction(Step(Fable3ActingPlayer.Pointer, down: true));
				break;
			case Ask.StepUp:
				HostBridge.PostFrontEndAction(Step(Fable3ActingPlayer.Pointer, down: false));
				break;
			case Ask.Accept:
				GameInput.QueuePointerPress(GameInput.PadA);
				break;
		}

		Redraw(cursor, count);
	}

	/// <summary>The cursor and count the view shows: the Sanctuary's own, else the guest's stated window.</summary>
	private static (int Cursor, int Count) Window()
	{
		if (Fable3GuiWorld.ListUp)
		{
			return (Fable3GuiWorld.Cursor, Fable3GuiWorld.Model!.Rows.Count);
		}

		(int cursor, int count, int _) = Fable3UiStatements.InteractionList;
		return (cursor, count);
	}

	private void Redraw(int cursor, int count)
	{
		ulong stamp = Fable3Elements.Generation ^ (Fable3UiStatements.Generation << 20) ^ ((ulong)(_hover + 1) << 60) ^
			(ulong)Engine_UiInputMode.Generation << 40 ^ (Fable3PopulatedRows.Generation << 52) ^ (Fable3GuiWorld.Generation << 8);
		if (!_shown || stamp != _drawn)
		{
			if (!_shown)
			{
				string rows = Fable3GuiWorld.ListUp ? " (the Sanctuary's)"
					: Fable3PopulatedRows.Up is { } populated ? (populated == Fable3PopulatedRows.Crime ? " (crime rows)" : " (real-estate rows)")
					: string.Empty;
				GD.Print($"[fable3-interaction-list] up{rows}: cursor {cursor} of {count}, window '{RowLabel(cursor, 0)}' | " +
					$"'{RowLabel(cursor, 1)}' | '{RowLabel(cursor, 2)}'");
			}

			_shown = true;
			_drawn = stamp;
			QueueRedraw();
		}
	}

	/// <summary>The pointer on the rows and the arrows. Player one's, as the mouse is.</summary>
	private void Point(int cursor, int count)
	{
		if (!Engine_UiPointer.Free || !Engine_UiInputMode.KeyboardMouse)
		{
			_hover = -1;
			return;
		}

		Vector2 at = Engine_UiPointer.LocalTo(this);
		if (at.DistanceSquaredTo(_pointerAt) > 4f)
		{
			_pointerAt = at;
			_hover = Fable3BoxAnswers.RowAt(new ArraySegment<Rect2>(_rowRects, 0, _rows), at);
		}

		bool onList = _hover >= 0 || _upRect.HasPoint(at) || _downRect.HasPoint(at);
		int wheel = onList ? Engine_UiPointer.TakeWheel() : 0;
		if (Fable3GuiWorld.ListUp)
		{
			PointWorld(cursor, count, at, wheel);
			return;
		}

		if (wheel != 0)
		{
			int from = _walk.Active ? _walk.Target : cursor;
			_walk.To(Math.Clamp(from + wheel, 0, Math.Max(count - 1, 0)), accept: false);
		}

		if (!Engine_UiPointer.LeftPressed)
		{
			return;
		}

		(bool up, bool down) = Arrows(cursor, count);
		int row = Fable3BoxAnswers.RowAt(new ArraySegment<Rect2>(_rowRects, 0, _rows), at);
		if (row >= 0)
		{
			Engine_UiPointer.TakeLeft();
			if (Fable3PopulatedRows.Up is { AsksById: true } populated && populated.At(cursor, row) is { } asked &&
				populated.Lever(Fable3ActingPlayer.One, asked.Id) is { } lever)
			{
				_walk.Drop();
				HostBridge.PostFrontEndAction(lever);
			}
			else
			{
				_walk.To(cursor + row, accept: true);
			}
		}
		else if (up && _upRect.HasPoint(at))
		{
			Engine_UiPointer.TakeLeft();
			_walk.To(cursor - 1, accept: false);
		}
		else if (down && _downRect.HasPoint(at))
		{
			Engine_UiPointer.TakeLeft();
			_walk.To(cursor + 1, accept: false);
		}
	}

	/// <summary>The pointer on the Sanctuary's list: the cursor and A through Godot's own cursor.</summary>
	private void PointWorld(int cursor, int count, Vector2 at, int wheel)
	{
		if (wheel != 0)
		{
			Fable3GuiWorld.Point(cursor + wheel, accept: false, Fable3ActingPlayer.Pointer);
		}

		if (!Engine_UiPointer.LeftPressed)
		{
			return;
		}

		(bool up, bool down) = Arrows(cursor, count);
		int row = Fable3BoxAnswers.RowAt(new ArraySegment<Rect2>(_rowRects, 0, _rows), at);
		if (row >= 0)
		{
			Engine_UiPointer.TakeLeft();
			Fable3GuiWorld.Point(cursor + row, accept: true, Fable3ActingPlayer.Pointer);
		}
		else if (up && _upRect.HasPoint(at))
		{
			Engine_UiPointer.TakeLeft();
			Fable3GuiWorld.Point(cursor - 1, accept: false, Fable3ActingPlayer.Pointer);
		}
		else if (down && _downRect.HasPoint(at))
		{
			Engine_UiPointer.TakeLeft();
			Fable3GuiWorld.Point(cursor + 1, accept: false, Fable3ActingPlayer.Pointer);
		}
	}

	private void LoadArt(double delta)
	{
		if (_bar is not null && _tab is not null)
		{
			return;
		}

		_artRetry -= delta;
		if (_artRetry > 0.0)
		{
			return;
		}

		_artRetry = 0.5;
		_bar ??= Fable3Art.Load(BarArt);
		_tab ??= Fable3Art.Load(TabArt);
		_tabLines ??= Fable3Art.Load(TabLinesArt);
		_arrowUp ??= Fable3Art.Load(ArrowUpArt);
		_arrowDown ??= Fable3Art.Load(ArrowDownArt);
		if (_bar is not null && _tab is not null && !_artSaid)
		{
			_artSaid = true;
			GD.Print($"[fable3-interaction-list] the 360's pieces: bar {_bar.GetSize()}, tab {_tab.GetSize()}, " +
				$"lines {(_tabLines is null ? "missing" : "ok")}, arrows {(_arrowUp is null || _arrowDown is null ? "missing" : "ok")}");
			_drawn = ulong.MaxValue;
		}
	}

	/// <summary>A canvas box (centre and size, y up) as this control's pixels.</summary>
	private static Rect2 Pixels((float X, float Y, float Width, float Height) box, Vector2 size, float scale) =>
		new(new Vector2((size.X * 0.5f) + ((box.X - (box.Width * 0.5f)) * scale), (size.Y * 0.5f) - ((box.Y + (box.Height * 0.5f)) * scale)),
			new Vector2(box.Width * scale, box.Height * scale));

	private static string Label(string element) =>
		Fable3Elements.Get(Fable3Elements.InteractionDocument, element + ".Title")?.StatedTextValue ?? string.Empty;

	/// <summary>
	/// Window row <paramref name="offset"/>'s label: the Sanctuary's kept row's tag, a populated row's tag in the
	/// book, else the row writer's.
	/// </summary>
	private static string RowLabel(int cursor, int offset)
	{
		if (Fable3GuiWorld.ListUp)
		{
			Fable3GuiWorldModel model = Fable3GuiWorld.Model!;
			int row = cursor + offset;
			return row >= 0 && row < model.Rows.Count
				? Engine_UiGuiText.Resolve(model.Rows[row].TextTag, Fable3Strings.Current)
				: string.Empty;
		}

		if (Fable3PopulatedRows.Up is { } populated)
		{
			return populated.At(cursor, offset) is { } row
				? Fable3PopulatedRows.Label(row, tag => Fable3Strings.Current is { } book && book.TryGetString(tag, out string text) ? text : null)
				: string.Empty;
		}

		return Label(RowElements[offset]);
	}

	/// <summary>A row's bar and label as the button writer left it: 50 is a row the game offers disabled. The Sanctuary's rows carry no such state.</summary>
	private static bool Enabled(int offset) =>
		Fable3GuiWorld.ListUp ||
		Fable3Elements.Get(Fable3Elements.InteractionDocument, RowElements[offset])?.StatedOpacity is not { } opacity || opacity > 50f;

	private void Bar(Texture2D? art, Rect2 rect, float alpha)
	{
		if (art is null)
		{
			DrawRect(rect, new Color(0f, 0f, 0f, 0.7f * alpha));
		}
		else
		{
			DrawTextureRect(art, rect, false, new Color(1f, 1f, 1f, alpha));
		}
	}

	public override void _Draw()
	{
		Fable3Prompts.Begin(this);
		_upRect = _downRect = default;
		if (!_shown || _rows <= 0)
		{
			return;
		}

		if (_culture != Engine_GameCulture.Current || _font is null)
		{
			_culture = Engine_GameCulture.Current;
			_font = Fable3Fonts.For(_culture).Get(UiFontBucket.Normal);
		}

		if (_font is null)
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / Fable3Canvas.CanvasHeight * Fable3Canvas.PixelsPerUnit;
		float line = _font.Font.LineHeight;
		// The cursor row's text is the file's 16 pt on its 32-unit bar; the font's own line fits it.
		float text = BarHeight * 0.62f * scale / line;
		(int cursor, int count) = Window();

		// The selection tab: the cursor row's label behind its A prompt, on the selection bar.
		Rect2 tab = Pixels(TabBox, size, scale);
		Bar(_tab, tab, 0.5f);
		if (_tabLines is not null)
		{
			DrawTextureRect(_tabLines, tab, false);
		}

		string chosen = RowLabel(cursor, 0);
		Fable3Prompts.Draw(this, _font, "A", chosen, tab.Position + new Vector2(TextInset * scale, (tab.Size.Y - (line * text * OtherRowScale)) * 0.5f),
			text * OtherRowScale, Colors.White);

		for (int row = 0; row < _rows; ++row)
		{
			Rect2 rect = Pixels(RowBox(row), size, scale);
			_rowRects[row] = rect;
			bool enabled = Enabled(row);
			float dim = enabled ? 1f : DisabledAlpha;
			Bar(_bar, rect, (row == 0 ? 1f : row == _hover ? HoverBarAlpha : OtherBarAlpha) * dim);
			float rowText = text * (row == 0 ? 1f : OtherRowScale);
			var colour = new Color(1f, 1f, 1f, (row == 0 ? CursorTextAlpha : OtherTextAlpha) * dim);
			Engine_UiCanvasText.DrawLine(this, _font, RowLabel(cursor, row),
				rect.Position + new Vector2(TextInset * scale, (rect.Size.Y - (line * rowText)) * 0.5f), rowText, colour);
		}

		(bool up, bool down) = Arrows(cursor, count);
		if (up)
		{
			_upRect = Pixels(ArrowBox(up: true, _rows), size, scale);
			Bar(_arrowUp, _upRect, 1f);
		}

		if (down)
		{
			_downRect = Pixels(ArrowBox(up: false, _rows), size, scale);
			Bar(_arrowDown, _downRect, 1f);
		}
	}
}
