namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Text;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// Fable 3's boxes, drawn from the model the `GUI.*` natives fill (<see cref="Fable3GuiNatives"/>):
/// the front message or question box, the intermission card and the menu box. The counters and
/// timers are hud.bgf's `CornerBox`, on the shared HUD view (<see cref="Fable3Hud"/>).
/// </summary>
/// <remarks>
/// GODOT TAKES THE PAD; THE GUEST'S OWN ANSWER RUNS. For the boxes Godot owns (questions, the
/// numeric adjuster, messages, quest boxes, notices, the pause screen's message box) and the menu
/// box, the guest's pad handlers do nothing (display_box_hooks.cpp); a press here becomes the
/// guest tick action <see cref="Fable3BoxAnswers"/> names, which the guest's thread hands to the
/// box's own answer function — so the reply still reaches the scripts through the guest's message
/// bus, and the box still closes on the guest's own lifetime (@Box.Close). What it draws is the
/// box's own text; the labels are the box's stated overrides or the guest's defaults (GUI_YES /
/// GUI_NO, GUI_DIALOG_ACCEPT), each behind the shared keycap for its button
/// (<see cref="Fable3Prompts"/>).
///
/// The place and look are ours, laid out in a 720-high reference and scaled with the window so
/// the text stays legible at any size.
/// </remarks>
public partial class Fable3Boxes : Control
{
	private const float ReferenceHeight = 720f;
	private const float BoxWidth = 620f;
	private const float Padding = 16f;
	private const float ButtonGap = 24f;
	private const float BoxBottom = 150f;

	private static readonly Color Panel = new(0f, 0f, 0f, 0.7f);
	private static readonly Color TitleColour = new(1f, 0.86f, 0.55f);
	private static readonly Color Disabled = new(0.6f, 0.6f, 0.6f);

	private readonly Fable3GuiNatives _natives;
	private UiFont? _font;
	private string _culture = string.Empty;
	private ulong _drawn = ulong.MaxValue;

	public Fable3Boxes(Fable3GuiNatives natives) => _natives = natives;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Engine_UiHostScreens.Books = OpenHostScreen;
	}

	public override void _ExitTree()
	{
		if (Engine_UiHostScreens.Books == OpenHostScreen)
		{
			Engine_UiHostScreens.Books = null;
		}
	}

	public override void _Process(double delta)
	{
		if (_culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			Engine_UiFontLibrary fonts = Fable3Fonts.For(_culture);
			Fable3Prompts.UseFonts(fonts);
			_font = fonts.Get(UiFontBucket.Normal);
			QueueRedraw();
		}

		Engine_UiGuiBoxes boxes = _natives.Boxes;
		// The game's own dialog or menu box comes first; its headline, counters and messages do not hold Refii's off.
		// A system message box the guest waits on is the console's and stands over them (the guest cannot
		// step until it is answered).
		bool guestBox = (boxes.Boxes.Count != 0 || boxes.Menu is not null) && !Engine_UiSystemBoxes.Up;
		RefiiUp = !guestBox && Refii.Host.HostBridge.IsGuestUp && RefiiShown() is not null;
		string state = $"{RefiiShown() ?? "none"} guestBox={guestBox} guestUp={Refii.Host.HostBridge.IsGuestUp} font={_font is not null}";
		if (state != _refiiState)
		{
			_refiiState = state;
			GD.Print($"[fable3-boxes] refii: {state}");
		}
		// A box a mod's menu row raises draws and answers over the menu; the menu takes the pad otherwise.
		bool menuInput = !guestBox && !RefiiUp;
		bool menuUp = DriveModMenu(delta, menuInput);
		WatchModWheel();
		if (RefiiUp)
		{
			AnswerRefii(delta);
		}
		else if (!menuUp)
		{
			Answer(boxes, delta);
		}

		// The router gives the pad to a showing box (not an intermission) before the TopBox, and the
		// menu box takes A; the hook checks the guest's own queues again (PadTakenAheadOfTopBox).
		DriveTopBox(Refii.Game.GameInput.Pad0.Buttons, !RefiiUp && !menuUp && _natives.FrontOwned is null &&
			boxes.Menu is null && Refii.Host.HostBridge.IsGuestUp && !Fable3HostOptions.IsOpen, delta);

		string shown = RefiiUp ? RefiiShown() ?? string.Empty : string.Empty;
		if (boxes.Generation != _drawn || shown != _refiiDrawn)
		{
			_drawn = boxes.Generation;
			_refiiDrawn = shown;
			QueueRedraw();
		}
	}

	// ---------------------------------------------------------------- Refii's own boxes
	// A mod's menu box (Engine_UiModMenuBox), and the host's questions and notices (Engine_UiHostBoxes: a mod's
	// message box, the co-op and mod questions), drawn as Fable 3's own boxes when the game shows none; the
	// pad is held from the game meanwhile (Fable3Input, RefiiUp).

	/// <summary>A Refii box is up and has the pad.</summary>
	public static bool RefiiUp { get; private set; }

	private string _refiiDrawn = string.Empty;
	private string _refiiState = string.Empty;

	/// <summary>What Refii shows now, as a key that changes when it does; null for nothing.</summary>
	private static string? RefiiShown()
	{
		if (Engine_UiModMenuBox.Active is { Menu: { } menu } box)
		{
			return "menu:" + box.Id + ":" + menu.Highlight + ":" + menu.Text.GetHashCode() + ":" + menu.Options.Count;
		}

		if (Engine_UiHostBoxes.Question is { } question)
		{
			return "question:" + question.GetHashCode() + ":" + (Engine_UiHostBoxes.ThirdCaption?.GetHashCode() ?? 0);
		}

		return Engine_UiHostBoxes.Notice is { } notice ? "notice:" + notice.GetHashCode() : null;
	}

	private void AnswerRefii(double delta)
	{
		// The host's own boxes are for whoever is at the screen: either local player's pad answers.
		uint pressed = _edges.Pressed(Fable3ActingPlayer.One, PadDown(Fable3ActingPlayer.One)) |
			_edges.Pressed(Fable3ActingPlayer.Two, PadDown(Fable3ActingPlayer.Two));
		bool a = (pressed & Refii.Game.GameInput.ButtonA) != 0;
		bool b = (pressed & Refii.Game.GameInput.ButtonB) != 0;
		bool x = (pressed & Refii.Game.GameInput.ButtonX) != 0;
		if (Engine_UiModMenuBox.Active is { } box)
		{
			int direction = (pressed & Refii.Game.GameInput.DpadDown) != 0 ? 1 : (pressed & Refii.Game.GameInput.DpadUp) != 0 ? -1 : 0;
			box.Drive(delta, direction, a, b);
			if (a || b)
			{
				Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			}

			return;
		}

		if (Engine_UiHostBoxes.Question is not null)
		{
			// X is the third answer of a question that has one (a guest's three-button system box).
			bool third = x && Engine_UiHostBoxes.ThirdCaption is not null;
			if (a || b || third)
			{
				Engine_UiHostBoxes.Answer(a ? 0 : b ? 1 : 2);
				Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			}

			return;
		}

		if (Engine_UiHostBoxes.Notice is not null && (a || b))
		{
			Engine_UiHostBoxes.Dismiss();
			Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
		}
	}

	private void DrawRefii(Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		if (Engine_UiModMenuBox.Active is { Menu: { } menu })
		{
			DrawMenu(menu, size, scale, strings);
			return;
		}

		if (Engine_UiHostBoxes.Question is { } question)
		{
			var prompts = new List<(string, string)>
			{
				("A", Engine_UiHostBoxes.AcceptCaption ?? Label(null, "GUI_YES", strings)),
				("B", Engine_UiHostBoxes.CancelCaption ?? Label(null, "GUI_NO", strings)),
			};
			if (Engine_UiHostBoxes.ThirdCaption is { } third)
			{
				prompts.Add(("X", third));
			}

			DrawPanel(Engine_UiHostBoxes.PageTitle ?? string.Empty, question, prompts, size, scale);
			return;
		}

		if (Engine_UiHostBoxes.Notice is { } notice)
		{
			DrawPanel(string.Empty, notice, new List<(string, string)> { ("A", Label(null, "GUI_DIALOG_ACCEPT", strings)) }, size, scale);
		}
	}

	/// <summary>The box last answered and how long ago: one answer per box until the guest drops it.</summary>
	private uint _answered;
	private double _answeredAge;

	/// <summary>Each local player's presses, on that player's own pad.</summary>
	private readonly Fable3ActingPlayer.Edges _edges = new();

	/// <summary>The buttons down on local <paramref name="player"/>'s pad (GameInput.Seen: every source folded).</summary>
	private static uint PadDown(int player) => Refii.Game.GameInput.Seen(Fable3ActingPlayer.PadOf(player)).Buttons;

	/// <summary>The answer is the guest's to act on; a second press inside this is the same press.</summary>
	private const double AnswerLatch = 0.5;

	/// <summary>
	/// Reads each local player's pad for the menu box or the front owned box and posts the answer as
	/// the player who pressed (<see cref="Fable3ActingPlayer"/>): the box's own handler decides whose
	/// press counts (its owner `+28`, the adjuster's `+200`).
	/// </summary>
	private void Answer(Engine_UiGuiBoxes boxes, double delta)
	{
		uint downOne = PadDown(Fable3ActingPlayer.One);
		uint downTwo = PadDown(Fable3ActingPlayer.Two);
		uint pressedOne = _edges.Pressed(Fable3ActingPlayer.One, downOne);
		uint pressedTwo = _edges.Pressed(Fable3ActingPlayer.Two, downTwo);
		_answeredAge += delta;
		if (boxes.Menu is { } pointed && Refii.Host.HostBridge.IsGuestUp && !Fable3HostOptions.IsOpen && PointMenu(pointed))
		{
			return;
		}

		if (!Refii.Host.HostBridge.IsGuestUp || Fable3HostOptions.IsOpen)
		{
			return;
		}

		if (boxes.Menu is null && _natives.FrontOwned is { Kind: "card", Box.Buttons: { ShowA: true, HoldA: true } } held)
		{
			// The card's handler has no player test (display_box_hooks.cpp): whoever holds fills it.
			int holder = Fable3ActingPlayer.Holder((downOne & Refii.Game.GameInput.ButtonA) != 0, PointerHolds("A"),
				(downTwo & Refii.Game.GameInput.ButtonA) != 0);
			HoldCard(held, holder != 0, delta, holder != 0 ? holder : Fable3ActingPlayer.One);
		}
		else
		{
			_holding = 0u;
		}

		// Each player's press, player one's first. On the menu box either player moves and cancels
		// and only player one selects: its mapper turns 72 into the select for player 1 alone
		// (`cmpwi r11,0x48` / `cmpwi r4,1` / `li r29,0x1C`, TU3 0x82B512CC..0x82B512DC / disc
		// 0x82B133D4..0x82B133E4) — Fable3BoxAnswers.MenuPick answers nothing for player two.
		AnswerAs(boxes, Fable3ActingPlayer.One, pressedOne);
		AnswerAs(boxes, Fable3ActingPlayer.Two, pressedTwo);
	}

	/// <summary>One player's presses on the menu box or the front owned box.</summary>
	private void AnswerAs(Engine_UiGuiBoxes boxes, int player, uint pressedBits)
	{
		if (pressedBits == 0u)
		{
			return;
		}

		// An end screen with no box in front of it: the epilogue's close, the credits' skip.
		if (boxes.Menu is null && _natives.FrontOwned is null &&
			Fable3EndScreens.Answer((pressedBits & Refii.Game.GameInput.ButtonA) != 0, (pressedBits & Refii.Game.GameInput.ButtonB) != 0, player) is { } end)
		{
			Post(end, 0u);
			return;
		}

		var press = new Fable3BoxAnswers.Press(
			(pressedBits & Refii.Game.GameInput.ButtonA) != 0, (pressedBits & Refii.Game.GameInput.ButtonB) != 0,
			(pressedBits & Refii.Game.GameInput.ButtonX) != 0, (pressedBits & Refii.Game.GameInput.ButtonY) != 0,
			(pressedBits & Refii.Game.GameInput.DpadUp) != 0, (pressedBits & Refii.Game.GameInput.DpadDown) != 0,
			(pressedBits & Refii.Game.GameInput.DpadLeft) != 0, (pressedBits & Refii.Game.GameInput.DpadRight) != 0);

		if (boxes.Menu is { } menu)
		{
			if (press.Up || press.Down)
			{
				menu.Highlight = Fable3BoxAnswers.MenuStep(menu.Highlight, menu.Options.Count, press.Down);
				QueueRedraw();
			}
			else if (press.A && menu.Options.Count > 0 && Fable3BoxAnswers.MenuPick(menu.Highlight, player) is { } pick)
			{
				Post(pick, 0u);
			}
			else if (press.B && menu.Cancelable)
			{
				Post(Fable3BoxAnswers.MenuCancel(player), 0u);
			}

			return;
		}

		if (_natives.FrontOwned is not { } front)
		{
			return;
		}

		if (front.Kind == "adjuster" && boxes.Adjusting is not null && (press.Left || press.Right || press.Up || press.Down))
		{
			// The value is Godot's until the answer: it is stored where the guest's answer reads it.
			if (boxes.StepAdjuster(press.Right || press.Up))
			{
				QueueRedraw();
			}

			return;
		}

		if (front.Address == _answered && _answeredAge < AnswerLatch)
		{
			return;
		}

		string? action = Fable3BoxAnswers.ForBox(front.Kind, front.Address, press, boxes.Adjusting?.Value ?? 0, front.Download, player,
			front.Box.Buttons is { HoldA: true });
		if (action is not null)
		{
			Post(action, front.Address);
		}
	}

	// ---------------------------------------------------------------- the held info card
	// A card whose A must be held fills on Godot's clock (Fable3BoxAnswers.CardHoldSeconds) while
	// the pad's A, its key, or the mouse on the drawn A prompt is held, and drains when let go, as
	// the guest's own fill did; the step it reaches full posts the accept once
	// (display_box_hooks.cpp: PostButtonAnswer(1)). The card stays until its script removes it.

	private readonly Engine_UiHoldMeter _cardHold = new(Fable3BoxAnswers.CardHoldSeconds);
	private uint _holding;

	private void HoldCard(Fable3GuiNatives.OwnedBox card, bool held, double delta, int player)
	{
		if (_holding != card.Address)
		{
			_holding = card.Address;
			_cardHold.Reset();
		}

		float before = _cardHold.Progress;
		if (_cardHold.Step(delta, held))
		{
			Post(Fable3BoxAnswers.CardHold(card.Address, player), 0u);
		}

		if (_cardHold.Progress != before)
		{
			QueueRedraw();
		}
	}

	/// <summary>The fill of the held card <paramref name="box"/> as drawn, or -1 for a box with no hold.</summary>
	private float HoldShown(Engine_UiGuiBoxes.Box box)
	{
		if (box.Buttons is not { ShowA: true, HoldA: true })
		{
			return -1f;
		}

		return _natives.FrontOwned is { } front && ReferenceEquals(front.Box, box) && _holding == front.Address ? _cardHold.Progress : 0f;
	}

	/// <summary>The left button held down on this surface's drawn <paramref name="button"/> prompt, the menus' pointer free.</summary>
	private bool PointerHolds(string button) =>
		Engine_UiPointer.Free && Engine_UiInputMode.KeyboardMouse && Input.IsMouseButtonPressed(MouseButton.Left) &&
		Fable3Prompts.ButtonAt(_prompts, Engine_UiPointer.LocalTo(this)) == button;

	/// <summary>The prompts as last drawn, in this control's space.</summary>
	private readonly List<(Rect2 Rect, string Button)> _prompts = new();

	// ---------------------------------------------------------------- the TopBox's A and Y
	// The TopBox's buttons are its own input's (topbox_answer.cpp): their going down and up is handed
	// to it, and it decides — the message it shows, player one's pad, press or hold, the moving hero.
	// Only while the message up shows that button and nothing of Godot's or the guest's ahead of the
	// TopBox has the pad; a button handed down is always handed up. A button the message asks to be
	// held fills on Godot's clock (Fable3TopBoxHold) and its fill is stated back.

	private readonly bool[] _topBoxDown = new bool[2];
	private readonly Fable3TopBoxHold _topBoxHold = new();
	private readonly List<string> _topBoxLevers = new();

	private void DriveTopBox(uint down, bool open, double delta)
	{
		Fable3UiStatements.TopBoxMessage? up = Fable3UiStatements.TopBox.Current;
		for (int i = 0; i < 2; ++i)
		{
			bool a = i == 0;
			bool held = (down & (a ? Refii.Game.GameInput.ButtonA : Refii.Game.GameInput.ButtonY)) != 0;
			if (held && !_topBoxDown[i] && open && up is { } message && (a ? message.ShowA : message.ShowY))
			{
				_topBoxDown[i] = true;
				Refii.Host.HostBridge.PostFrontEndAction(Fable3BoxAnswers.TopBox(a, true, Fable3ActingPlayer.OfPad(0)));
			}
			else if (!held && _topBoxDown[i])
			{
				_topBoxDown[i] = false;
				Refii.Host.HostBridge.PostFrontEndAction(Fable3BoxAnswers.TopBox(a, false, Fable3ActingPlayer.OfPad(0)));
			}
		}

		_topBoxLevers.Clear();
		_topBoxHold.Step(delta, up?.Id ?? 0u,
			new Fable3TopBoxHold.Button(up is { ShowA: true, HoldA: true }, _topBoxDown[0], Fable3UiStatements.TopBoxHeld[0]),
			new Fable3TopBoxHold.Button(up is { ShowY: true, HoldY: true }, _topBoxDown[1], Fable3UiStatements.TopBoxHeld[1]),
			Fable3ActingPlayer.OfPad(0), _topBoxLevers);
		foreach (string lever in _topBoxLevers)
		{
			Refii.Host.HostBridge.PostFrontEndAction(lever);
		}
	}

	/// <summary>The menu box's rows as last drawn, top to bottom, in this control's space.</summary>
	private readonly List<Rect2> _menuRows = new();
	private Vector2 _pointerAt;

	/// <summary>
	/// THE POINTER ON THE MENU BOX. Its highlight is Godot's and its pick is a stated row
	/// (<see cref="Fable3BoxAnswers.MenuPick"/>: the guest's own highlight then select), so the
	/// pointer needs no walk: moving onto a row highlights it, a click on a row picks that row.
	/// A pointer at rest sets nothing, so the pad's steps stand until the mouse moves again.
	/// The pointer's player (<see cref="Fable3ActingPlayer.Pointer"/>). True when a click picked.
	/// </summary>
	private bool PointMenu(Engine_UiGuiBoxes.MenuBoxModel menu)
	{
		if (!Engine_UiPointer.Free || !Engine_UiInputMode.KeyboardMouse)
		{
			return false;
		}

		Vector2 at = Engine_UiPointer.LocalTo(this);
		bool moved = at.DistanceSquaredTo(_pointerAt) > 4f;
		if (moved)
		{
			_pointerAt = at;
		}

		int row = Fable3BoxAnswers.RowAt(_menuRows, at);
		if (row < 0 || row >= menu.Options.Count)
		{
			return false;
		}

		if (Engine_UiPointer.LeftPressed && Fable3BoxAnswers.MenuPick(row, Fable3ActingPlayer.Pointer) is { } pick)
		{
			Engine_UiPointer.TakeLeft();
			menu.Highlight = row;
			Post(pick, 0u);
			QueueRedraw();
			return true;
		}

		if (moved && menu.Highlight != row)
		{
			menu.Highlight = row;
			QueueRedraw();
		}

		return false;
	}

	private void Post(string action, uint address)
	{
		Refii.Host.HostBridge.PostFrontEndAction(action);
		_answered = address;
		_answeredAge = 0.0;
		// The press that answered is not a press on whatever the guest shows next.
		Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
		GD.Print($"[fable3-boxes] answered: {action}");
	}

	public override void _Draw()
	{
		Fable3Prompts.Begin(this);
		_menuRows.Clear();
		_prompts.Clear();
		if (_font is null)
		{
			return;
		}

		Engine_UiGuiBoxes boxes = _natives.Boxes;
		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / ReferenceHeight;
		Engine_BabelDatabase? strings = Fable3Strings.Current;
		if (ModMenuUp && !(boxes.Boxes.Count != 0 || boxes.Menu is not null))
		{
			DrawModMenu(size, scale, strings);
		}

		if (ModWheel.OpenPlayer != 0)
		{
			DrawModWheels(size, scale, strings);
		}

		if (RefiiUp)
		{
			DrawRefii(size, scale, strings);
			return;
		}

		if (!boxes.Any)
		{
			return;
		}

		Engine_UiGuiBoxes.Box? card = null;
		Engine_UiGuiBoxes.Box? front = null;
		foreach (Engine_UiGuiBoxes.Box box in boxes.Boxes)
		{
			if (box.Name == "IntermissionBox")
			{
				card ??= box;
			}
			else
			{
				front ??= box;
			}
		}

		if (card is not null)
		{
			DrawIntermission(card, size, scale);
		}

		if (boxes.Menu is { } menu)
		{
			DrawMenu(menu, size, scale, strings);
		}
		else if (front is not null)
		{
			DrawFront(front, size, scale, strings);
		}
	}

	private void DrawFront(Engine_UiGuiBoxes.Box box, Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		var buttons = new List<(string Button, string Label)>();
		if (_natives.OwnedKind(box) is "card" or "buttons" && box.Buttons is { } stated)
		{
			// The info card and the button box: the title and the labels they state (the button box
			// states none: GUI_ACCEPTANCE_BOX, its script's accept and help), each behind its button.
			if (stated.ShowA)
			{
				buttons.Add(("A", Label(stated.AcceptText, "GUI_DIALOG_ACCEPT", strings)));
			}

			if (stated.ShowY)
			{
				buttons.Add(("Y", Label(stated.HelpText, "GUI_DIALOG_HELP", strings)));
			}

			string heading = box.Title.Length == 0 ? string.Empty : Label(box.Title, box.Title, strings);
			DrawPanel(heading, box.Text, buttons, size, scale, HoldShown(box));
			return;
		}

		if (box.Kind == Engine_UiGuiBoxes.BoxKind.Question)
		{
			buttons.Add(("A", Label(box.Buttons?.AcceptText, "GUI_YES", strings)));
			buttons.Add(("B", Label(box.Buttons?.HelpText, "GUI_NO", strings)));
		}
		else
		{
			// A box stated without its A (TU3's non-interactive message box) draws no prompt row.
			if (box.Buttons is not { ShowA: false })
			{
				buttons.Add(("A", Label(null, "GUI_DIALOG_ACCEPT", strings)));
			}

			if (box.Kind == Engine_UiGuiBoxes.BoxKind.Adjuster)
			{
				// sub_8281C638(box, 0): B finishes it without the value (event 70).
				buttons.Add(("B", Label(null, "GUI_DIALOG_BACK", strings)));
			}

			// The download form: sub_8281AFA8 shows `paper.MessageBox.Action` and names its X
			// button by the tag at box+36 (0x8281B048 / 0x8281B07C); X (76) is taken only then
			// (sub_8281B390 at 0x8281B410) and opens the marketplace itself.
			if (box.AcceptCaption.Length > 0)
			{
				buttons.Add(("X", Label(box.AcceptCaption, "GUI_DIALOG_ACCEPT", strings)));
			}
		}

		// The adjuster's value is Godot's until it answers: shown under the question.
		string body = box.Kind == Engine_UiGuiBoxes.BoxKind.Adjuster && _natives.Boxes.Adjusting is { } adjusting
			? box.Text + "\n" + adjusting.Display
			: box.Text;
		DrawPanel(box.Title, body, buttons, size, scale);
	}

	/// <summary>The front box's panel: the title, the body and the prompts right-aligned on the last line.</summary>
	private void DrawPanel(string title, string body, List<(string Button, string Label)> buttons, Vector2 size, float scale,
		float hold = -1f)
	{
		float width = Mathf.Min(BoxWidth * scale, size.X - (2f * Padding * scale));
		float inner = width - (2f * Padding * scale);
		float lineHeight = _font!.Font.LineHeight * scale;
		int titleLines = title.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, title, inner, scale);
		int bodyLines = body.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, body, inner, scale);
		float height = ((titleLines + bodyLines + 1) * lineHeight) + (3f * Padding * scale);
		var rect = new Rect2(new Vector2((size.X - width) * 0.5f, size.Y - (BoxBottom * scale) - height), new Vector2(width, height));
		DrawRect(rect, Panel);

		Vector2 pen = rect.Position + new Vector2(Padding * scale, Padding * scale);
		if (titleLines > 0)
		{
			pen.Y += Engine_UiCanvasText.DrawWrapped(this, _font, title, pen, inner, scale, TitleColour);
		}

		if (bodyLines > 0)
		{
			Engine_UiCanvasText.DrawWrapped(this, _font, body, pen, inner, scale, Colors.White);
		}

		// The prompts, right-aligned on the last line: A rightmost, as the guest lays them.
		float x = rect.End.X - (Padding * scale);
		float y = rect.End.Y - (Padding * scale) - lineHeight;
		foreach ((string button, string label) in buttons)
		{
			float promptWidth = Fable3Prompts.Measure(_font, label, scale);
			x -= promptWidth;
			Fable3Prompts.Draw(this, _font, button, label, new Vector2(x, y), scale, Colors.White);
			_prompts.Add((new Rect2(new Vector2(x, y), new Vector2(promptWidth, lineHeight)), button));
			if (button == "A" && hold >= 0f)
			{
				// The hold's fill under its prompt, Fable 3's gold on the panel's dark.
				var track = new Rect2(new Vector2(x, y + lineHeight + (2f * scale)), new Vector2(promptWidth, 3f * scale));
				DrawRect(track, new Color(1f, 1f, 1f, 0.2f));
				DrawRect(new Rect2(track.Position, new Vector2(promptWidth * hold, track.Size.Y)), TitleColour);
			}

			x -= ButtonGap * scale;
		}
	}

	/// <summary>
	/// The intermission line as intermissionbox.bgf lays it out (canvas units about the screen
	/// centre, y up): `Layer.Group` at y −19 holding `TargetBox`, a 512×32 strip at 50% opacity
	/// at y −289, and `Text` (fertigo 16, white, box 0.9 of the width) at y −302 — a single line
	/// in a strip near the bottom centre, not a title card. It fades with the model's close fade.
	/// </summary>
	private void DrawIntermission(Engine_UiGuiBoxes.Box card, Vector2 size, float scale)
	{
		float alpha = card.Persistent ? 1f : Mathf.Clamp(card.Remaining / Engine_UiGuiBoxes.CloseFadeSeconds, 0f, 1f);
		if (card.Text.Length == 0)
		{
			return;
		}

		float inner = Mathf.Min(IntermissionTextWidth * scale, size.X - (2f * Padding * scale));
		int lines = Engine_UiCanvasText.MeasureWrappedLines(_font!, card.Text, inner, scale);
		float lineHeight = _font!.Font.LineHeight * scale;
		float textWidth = lines > 1 ? inner : Engine_UiCanvasText.Measure(_font, card.Text) * scale;
		float stripWidth = Mathf.Max(IntermissionStripWidth * scale, textWidth + (2f * Padding * scale));
		float stripHeight = Mathf.Max(IntermissionStripHeight * scale, (lines * lineHeight) + (Padding * scale));
		float centreY = (size.Y * 0.5f) - (IntermissionStripY * scale);
		var strip = new Rect2(new Vector2((size.X - stripWidth) * 0.5f, centreY - (stripHeight * 0.5f)), new Vector2(stripWidth, stripHeight));
		DrawRect(strip, new Color(0f, 0f, 0f, 0.5f * alpha));
		var pen = new Vector2((size.X - (lines > 1 ? inner : textWidth)) * 0.5f, centreY - (lines * lineHeight * 0.5f));
		Engine_UiCanvasText.DrawWrapped(this, _font, card.Text, pen, inner, scale, new Color(1f, 1f, 1f, alpha));
	}

	/// <summary>intermissionbox.bgf `Layer.Group` (−19) + `Group.TargetBox` (−289): the strip's centre.</summary>
	private const float IntermissionStripY = -308f;
	private const float IntermissionStripWidth = 512f;
	private const float IntermissionStripHeight = 32f;
	private const float IntermissionTextWidth = 0.9f * 1280f;

	private void DrawMenu(Engine_UiGuiBoxes.MenuBoxModel menu, Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		float width = Mathf.Min(BoxWidth * scale, size.X - (2f * Padding * scale));
		float inner = width - (2f * Padding * scale);
		float lineHeight = _font!.Font.LineHeight * scale;
		int titleLines = menu.Text.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, menu.Text, inner, scale);
		float rowHeight = lineHeight + (6f * scale);
		float height = (titleLines * lineHeight) + (menu.Options.Count * rowHeight) + (menu.Cancelable ? lineHeight : 0f) + (3f * Padding * scale);
		var rect = new Rect2(new Vector2((size.X - width) * 0.5f, (size.Y - height) * 0.5f), new Vector2(width, height));
		DrawRect(rect, Panel);

		Vector2 pen = rect.Position + new Vector2(Padding * scale, Padding * scale);
		if (titleLines > 0)
		{
			pen.Y += Engine_UiCanvasText.DrawWrapped(this, _font, menu.Text, pen, inner, scale, TitleColour) + (Padding * 0.5f * scale);
		}

		for (int i = 0; i < menu.Options.Count; ++i)
		{
			Engine_UiGuiBoxes.MenuOption option = menu.Options[i];
			if (i == menu.Highlight)
			{
				DrawRect(new Rect2(pen - new Vector2(6f * scale, 2f * scale), new Vector2(inner + (12f * scale), rowHeight)), new Color(1f, 1f, 1f, 0.15f));
			}

			_menuRows.Add(new Rect2(pen - new Vector2(6f * scale, 2f * scale), new Vector2(inner + (12f * scale), rowHeight)));
			Engine_UiCanvasText.DrawLine(this, _font, option.Label, pen, scale, option.Enabled ? Colors.White : Disabled);
			pen.Y += rowHeight;
		}

		if (menu.Cancelable)
		{
			string back = Label(null, "GUI_DIALOG_BACK", strings);
			float backWidth = Fable3Prompts.Measure(_font, back, scale);
			Fable3Prompts.Draw(this, _font, "B", back, new Vector2(rect.End.X - (Padding * scale) - backWidth, rect.End.Y - (Padding * scale) - lineHeight), scale, Colors.White);
		}
	}

	/// <summary>A stated label tag, else the guest's default tag, resolved.</summary>
	private static string Label(string? stated, string fallback, Engine_BabelDatabase? strings)
	{
		string tag = string.IsNullOrEmpty(stated) ? fallback : stated;
		string text = Engine_UiGuiText.Resolve(tag, strings);
		// A prompt tag Fable 3's text does not carry (GUI_DIALOG_BACK is Fable 2's) reads as its word, not the tag.
		return text != tag ? text : tag switch
		{
			"GUI_DIALOG_BACK" => "Back",
			"GUI_DIALOG_ACCEPT" => "Accept",
			"GUI_DIALOG_HELP" => "Help",
			"GUI_ACCEPTANCE_BOX" => "Accept",
			"GUI_YES" => "Yes",
			"GUI_NO" => "No",
			_ => text,
		};
	}
}
