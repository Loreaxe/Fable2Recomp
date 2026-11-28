using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// The co-op screen (24): the same two pages on every machine, the original's figures, the
/// shared Load Game fan for player two's save, player one's screen row on the couch, and the
/// mouse on whichever page this machine's player drives.
/// </summary>
/// <remarks>
/// <para>WHO DRIVES WHICH PAGE IS THE GUEST'S. `sub_824389D0` routes an event by the GUI player
/// it came from — player 1 to the left page (`sub_82438A08`), player 2 to the right
/// (`sub_82438CF8`). The online host's pad is player 1 and the joiner's player 2
/// (`sub_822A4E38` on the net role bytes), and in local co-op both pads reach the screen
/// (`sub_824047E8` skips the per-player filter for screen 0x18). Each page's state crosses the
/// wire in the screen's own message (`sub_8243A8F0`), so both machines already show both pages;
/// the remaster draws them the same way on both.</para>
/// <para>THE PADS AND THE MOUSE POST THE SCREEN'S OWN CODES (`CoopAction:&lt;code&gt;:&lt;player&gt;`, the
/// screen's input slot vf+24): each pad for its own player (Fable2Input.CoopPresses), the mouse for
/// this machine's page — player one on the host and on the couch, player two on a joiner. A row the
/// pointer picks is reached the way the pad reaches it — the focus stepped with up/down, the value
/// with left/right, each a press and its release — so the guest's own rows, sync and respawns all
/// stay the guest's.</para>
/// </remarks>
public partial class GameUi
{
	/// <summary>The co-op screen's state this frame, or null while it is not up.</summary>
	private Engine_UiCoopStated.State? _coopScreen;

	/// <summary>The Load Game fan for player two's save picker, cloned from the co-op file.</summary>
	private Engine_FrontEndSaveCards? _coopSaveCards;

	/// <summary>Seconds the fan has been up — portraits arrive with the guest's next sample.</summary>
	private double _coopSaveCardsAge;

	/// <summary>How many of the fan's portraits had resolved when it was last filled.</summary>
	private int _coopSaveCardsPortraits;

	/// <summary>Player one's screen row, shown on the couch.</summary>
	private Engine_UiCoopModeRow? _coopModeRow;

	/// <summary>The co-op screen's file-only build, for clean copies of its art.</summary>
	private Engine_UiTheme? _coopPattern;

	/// <summary>Player one's left/right last frame, so a held direction changes the mode once.</summary>
	private int _coopModeHeld;

	/// <summary>Player two's save picker was up last frame.</summary>
	private bool _coopPickerWasOpen;

	/// <summary>The save this owns player two's picker cursor on past the guest's six, else null.</summary>
	private int? _coopPickerOwned;

	/// <summary>The save the guest's cursor was sent to (`CoopPickCursor:N`) and has not stated yet, else -1.</summary>
	private int _coopPickerSent = -1;

	private ulong _coopPickerSentAt;

	/// <summary>The co-op screen's book while it stands, else null.</summary>
	private Engine_UiOwnedBook? CoopBook() =>
		OwnedScreenId() == CoopScreenId && _books.Book(CoopScreenId) is { Visible: true } book &&
		IsInstanceValid(book)
			? book
			: null;

	/// <summary>The page this machine's player drives: the joiner's is player two's.</summary>
	private static bool CoopDrivesRightPage => SessionRole == 2u && !CoopSession.LocalCoop;

	/// <summary>Once a frame: the screen's state, the shell's flags, the fan, the row, the presses.</summary>
	private void DriveCoopScreen(double delta)
	{
		Engine_UiOwnedBook? book = CoopBook();
		_coopScreen = book is null ? null : Engine_UiCoopStated.Current;
		Fable2PageProviders.CoopPlayerTwoAccepted = _coopScreen?.RightAccepted ?? false;
		Fable2PageProviders.CoopRemotePlayerTwo = SessionRole == 1u && !CoopSession.LocalCoop;

		// A SAVE WAS JUST PICKED: read it and send it now. The guest has loaded the save's HeroSave
		// block before it closes the picker (`sub_8243AEF8` → `sub_824482E0`, then `sub_8243B390`),
		// so the kit can be read at once rather than on the next two-second sample, and the record
		// goes out on the same pass rather than the next half-second restate.
		bool pickerOpen = _coopScreen is { PickerOpen: true };
		if (_coopPickerWasOpen && !pickerOpen)
		{
			_coopKitSample = 0.0;
			_coopHeroRestate = 0.0;
		}

		_coopPickerWasOpen = pickerOpen;
		DriveCoopPicker(pickerOpen ? _coopScreen : null);

		if (book is not null && _coopPattern is null)
		{
			_coopPattern = _presentation?.PatternBookFor("coopscreen");
		}

		DriveCoopSaveFan(book, delta);
		DriveCoopModeRow(book);
	}

	/// <summary>
	/// The pad of the player whose save picker this machine drives: local player two's, or pad 0
	/// on an online joiner (whose player is player two); -1 on the online host.
	/// </summary>
	private static int CoopPickerPad =>
		CoopSession.LocalCoop
			? (Fable2Input.PlayerTwoPad is >= 1 and < GameInput.MaxControllers ? Fable2Input.PlayerTwoPad : -1)
			: CoopDrivesRightPage ? 0 : -1;

	/// <summary>Player two's picker as stated, with this side's hold on the cursor past the guest's six.</summary>
	private Engine_CoopSavePicker.Picker CoopPicker(Engine_UiCoopStated.State state) =>
		new(state.Saves.Count, _coopPickerSent >= 0 ? _coopPickerSent : state.PickerCursor, state.OwnSave,
			state.ValidBits, _coopPickerOwned);

	/// <summary>
	/// PLAYER TWO'S SAVE PICKER PAST THE SIXTH SAVE (Engine_CoopSavePicker). On the guest's six the
	/// guest keeps its cursor and A; past them this holds the cursor, withholds player two's left,
	/// right and A from the guest, and commits through the guest's own commit (`CoopPickSave:N`,
	/// guest_tick_pump.cpp DispatchCoopPick). The page buttons are the load fan's, on player two's pad.
	/// </summary>
	private void DriveCoopPicker(Engine_UiCoopStated.State? state)
	{
		int pad = state is null ? -1 : CoopPickerPad;
		Fable2Input.CoopPickerPad = pad;
		if (state is not { } picker || pad < 0)
		{
			Fable2Input.CoopPickerWithheld = Engine_CoopSavePicker.Press.None;
			Fable2Input.TakeCoopPickerPresses();
			_coopPickerOwned = null;
			_coopPickerSent = -1;
			return;
		}

		if (_coopPickerSent >= 0 &&
			(picker.PickerCursor == _coopPickerSent || Time.GetTicksMsec() - _coopPickerSentAt > 1000UL))
		{
			_coopPickerSent = -1;
		}

		Engine_CoopSavePicker.Press presses = Fable2Input.TakeCoopPickerPresses();

		// The mouse's page turn, as on the Load Game fan: the wheel, or a click on a page arrow.
		if (_coopSaveCards is { Visible: true } fan)
		{
			int turn = fan.PointerTurn(Engine_UiPointer.Canvas, Engine_UiPointer.LeftPressed, out bool arrowClicked);
			if (arrowClicked)
			{
				Engine_UiPointer.TakeLeft();
			}

			presses |= turn > 0 ? Engine_CoopSavePicker.Press.PageNext
				: turn < 0 ? Engine_CoopSavePicker.Press.PagePrevious
				: Engine_CoopSavePicker.Press.None;
		}

		foreach (Engine_CoopSavePicker.Press press in new[]
		{
			Engine_CoopSavePicker.Press.Left, Engine_CoopSavePicker.Press.Right,
			Engine_CoopSavePicker.Press.PageNext, Engine_CoopSavePicker.Press.PagePrevious,
			Engine_CoopSavePicker.Press.Accept,
		})
		{
			if ((presses & press) != 0)
			{
				ApplyCoopPick(Engine_CoopSavePicker.Apply(CoopPicker(picker), press), press.ToString());
			}
		}

		Fable2Input.CoopPickerWithheld = Engine_CoopSavePicker.Withheld(CoopPicker(picker));
	}

	/// <summary>An outcome of the picker's: the hold on the cursor, and what the guest is asked.</summary>
	private void ApplyCoopPick(Engine_CoopSavePicker.Outcome outcome, string what)
	{
		if (!outcome.Taken)
		{
			return;
		}

		int? was = _coopPickerOwned;
		_coopPickerOwned = outcome.Owned;
		if (outcome.Commit >= 0)
		{
			HostBridge.PostFrontEndAction($"CoopPickSave:{outcome.Commit}");
			GD.Print($"[game-ui] co-op picker: {what} commits save {outcome.Commit} for player two");
		}
		else if (outcome.GuestCursor >= 0)
		{
			_coopPickerSent = outcome.GuestCursor;
			_coopPickerSentAt = Time.GetTicksMsec();
			HostBridge.PostFrontEndAction($"CoopPickCursor:{outcome.GuestCursor}");
			GD.Print($"[game-ui] co-op picker: {what} puts the guest's cursor on save {outcome.GuestCursor}");
		}
		else if (was != outcome.Owned)
		{
			GD.Print($"[game-ui] co-op picker: {what} — cursor on save {outcome.Owned?.ToString() ?? "(the guest's)"}");
		}
		else if (what == nameof(Engine_CoopSavePicker.Press.Accept))
		{
			GD.Print($"[game-ui] co-op picker: A on save {outcome.Owned} refused — its header did not parse");
		}
	}

	/// <summary>The player whose page this machine's pointer drives.</summary>
	private static int CoopPointerPlayer => CoopDrivesRightPage ? 2 : 1;

	/// <summary>One code to the screen for this machine's page.</summary>
	private static void PostCoopPress(int code) =>
		HostBridge.PostFrontEndAction(Fable2CoopScreenPresses.Lever(code, CoopPointerPlayer));

	/// <summary>
	/// <paramref name="count"/> steps of one direction, each its press and its release: the page's
	/// repeat filter takes a press only after the release that resets it (`sub_828818E8`, 32..35 at
	/// TU1 0x82881A88), and the lever posts them in order within one input pass.
	/// </summary>
	private static void PostCoopSteps(int direction, int count)
	{
		for (int i = 0; i < count; ++i)
		{
			PostCoopPress(direction);
			PostCoopPress(direction + Fable2CoopScreenPresses.ReleaseOffset);
		}
	}

	/// <summary>
	/// PLAYER TWO'S SAVES ON THE LOAD GAME FAN. The guest's picker (`sub_8243B070`) enumerates the
	/// saves, builds each card's text (`sub_8243B678`), stamps the screenshots and keeps the cursor
	/// (`+392`); it is opened by Y and driven by player two's left/right, A and B
	/// (`sub_82438CF8`). The fan only draws it — the main menu's own fan, so the cards rise, fan
	/// and carry their portraits the same way in both places — and follows the guest's cursor.
	/// </summary>
	private void DriveCoopSaveFan(Engine_UiOwnedBook? book, double delta)
	{
		if (book is null || _coopScreen is not { PickerOpen: true } state)
		{
			if (_coopSaveCards is { Visible: true })
			{
				_coopSaveCards.Visible = false;
				GD.Print("[game-ui] co-op save fan down");
			}

			_coopPickerCursorSaid = -1;
			return;
		}

		Node3D? live = Engine_UiTheme.FindByName(book, Engine_FrontEndSaveCards.PageName) as Node3D;
		if (_coopSaveCards is null || !IsInstanceValid(_coopSaveCards))
		{
			Node3D? pattern = _coopPattern?.ArtSourceNode(Engine_FrontEndSaveCards.PageName);
			_coopSaveCards = Engine_FrontEndSaveCards.FromPresentation(pattern ?? live, "CoopSaveCards");
			_coopSaveCards.Theme = _coopPattern ?? book.Book;
			GD.Print("[game-ui] co-op save fan built — art " +
				$"{(_coopSaveCards.HasShippedArt ? "cloned from the co-op 'loadgame'" : "NOT sourced (stand-ins)")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})");
		}

		// Beside the guest's own copy, on its place on the canvas.
		Node parent = live?.GetParent() ?? book;
		if (_coopSaveCards.GetParent() != parent)
		{
			_coopSaveCards.GetParent()?.RemoveChild(_coopSaveCards);
			parent.AddChild(_coopSaveCards);
		}

		if (live is not null)
		{
			_coopSaveCards.Transform = live.Transform;
		}

		// FILLED ON OPEN, AND AGAIN AS PORTRAITS LAND: the guest stamps the screenshots as it
		// opens, and the book holds them from its next sample.
		if (!_coopSaveCards.Visible)
		{
			_coopSaveCardsAge = 0.0;
			_coopSaveCardsPortraits = -1;
		}

		_coopSaveCardsAge += delta;
		if (_coopSaveCardsPortraits < 0 || _coopSaveCardsAge < 1.0)
		{
			FillCoopSaveFan(book, state);
		}

		int cursor = CoopPicker(state).Cursor;
		_coopSaveCards.Select(cursor);
		SayCoopPickerCursor(state, cursor);
		PointCoopSaveFan(state);
	}

	/// <summary>The cursor the fan last said it lifted, -1 while the fan is down.</summary>
	private int _coopPickerCursorSaid = -1;

	/// <summary>
	/// Names the save under the picker's cursor whenever it moves: its index, its caption and its page,
	/// and who holds the cursor — the guest on its six, this past them (Engine_CoopSavePicker).
	/// </summary>
	private void SayCoopPickerCursor(Engine_UiCoopStated.State state, int cursor)
	{
		if (cursor == _coopPickerCursorSaid || cursor < 0 || cursor >= state.Saves.Count)
		{
			return;
		}

		_coopPickerCursorSaid = cursor;
		GD.Print($"[game-ui] co-op picker: cursor on save {cursor} of {state.Saves.Count} " +
			$"'{CoopCaption(state.Saves[cursor].Text)}' — page " +
			$"{Engine_FrontEndSavePages.PageOf(cursor) + 1}/{Engine_FrontEndSavePages.PageCount(state.Saves.Count)}, " +
			$"{(_coopPickerOwned is null ? "the guest's cursor" : "held past the guest's six")}");
	}

	/// <summary>A card's caption as the log names it: its first line (the hero and the region).</summary>
	private static string CoopCaption(string text)
	{
		int end = text.IndexOf('\n');
		return end < 0 ? text : text[..end];
	}

	private void FillCoopSaveFan(Engine_UiOwnedBook book, Engine_UiCoopStated.State state)
	{
		// The first card and the cards' texts are the picker's opener's (`@CoopPicker.Open`); until
		// it has stated them, the fan waits.
		if (_coopSaveCards is null ||
			state.FirstCard is not (>= 1 and <= Engine_FrontEndSaveCards.MaxCards))
		{
			return;
		}

		int baseSlot = state.FirstCard;
		var saves = new List<Engine_FrontEndSaveCards.Save>();
		int portraits = 0;
		for (int i = 0; i < state.Saves.Count; ++i)
		{
			// Card k (1-based) shows save k − base: `sub_8243B520(screen, k, texture, …)` writes
			// `loadgame.Group.Card{k}…screenshot`. A save past the sixth card has the portrait the
			// opener's derivation gives it (`@CoopPicker.Portrait`).
			int card = baseSlot + i;
			Texture2D? portrait = card <= Engine_FrontEndSaveCards.MaxCards
				? book.StatedTexture($"loadgame.Group.Card{card}.Group.screenshot")
				: state.LaterPortraits.TryGetValue(i, out uint hash) && hash != 0u ? book.ResolveTexture?.Invoke(hash) : null;
			portraits += portrait is null ? 0 : 1;
			saves.Add(new Engine_FrontEndSaveCards.Save(state.Saves[i].Text, portrait));
		}

		if (portraits == _coopSaveCardsPortraits && _coopSaveCards.Visible)
		{
			return;
		}

		_coopSaveCardsPortraits = portraits;
		_coopSaveCards.PageTemplate = Said(PageText);
		_coopSaveCards.SetSaves(saves, baseSlot);
		_coopSaveCards.DimmedSave = state.OwnSave;
		_coopSaveCards.Select(CoopPicker(state).Cursor);
		_coopSaveCards.Visible = true;
		var order = new System.Text.StringBuilder();
		for (int i = 0; i < state.Saves.Count; ++i)
		{
			order.Append(i == 0 ? string.Empty : " | ").Append(i).Append(" '").Append(CoopCaption(state.Saves[i].Text)).Append('\'');
		}

		GD.Print($"[game-ui] co-op save fan: {saves.Count} save(s) from card {baseSlot}, " +
			$"{portraits} portrait(s), cursor {CoopPicker(state).Cursor}, steps over {state.OwnSave} — " +
			_coopSaveCards.Describe() + " — in order: " + order);
	}

	/// <summary>
	/// The mouse on the fan, for the player whose picker it is: pointing at a card walks the
	/// guest's cursor to it, a click on it loads it. A click anywhere else on the fan is taken so
	/// it cannot fall through as A.
	/// </summary>
	private void PointCoopSaveFan(Engine_UiCoopStated.State state)
	{
		if (_coopSaveCards is null || !CoopDrivesRightPage || Engine_UiPointer.Canvas is not { } at)
		{
			return;
		}

		int pointed = _coopSaveCards.SaveAt(at);
		bool clicked = Engine_UiPointer.TakeLeft();
		if (pointed < 0)
		{
			return;
		}

		if (!clicked && !Engine_UiPointer.Moved)
		{
			return;
		}

		// Past the guest's six, or while this holds the cursor: the picker's own outcome.
		Engine_CoopSavePicker.Outcome outcome = Engine_CoopSavePicker.Point(CoopPicker(state), pointed, clicked);
		if (outcome.Taken)
		{
			ApplyCoopPick(outcome, clicked ? "click" : "point");
			return;
		}

		// On the guest's six with the guest holding the cursor: its own presses walk it there.
		int steps = Engine_CoopSavePicker.Presses(state.PickerCursor, pointed, state.Saves.Count, state.OwnSave);
		if (steps != 0)
		{
			PostCoopSteps(steps > 0 ? Fable2CoopScreenPresses.Right : Fable2CoopScreenPresses.Left, System.Math.Abs(steps));
		}

		if (clicked && state.Saves[pointed].Valid)
		{
			PostCoopPress(Fable2CoopScreenPresses.Accept);
		}
	}

	/// <summary>
	/// PLAYER ONE'S SCREEN, ON THE COUCH: shared, side by side, or top and bottom — the launcher's
	/// Multiplayer setting, chosen where both players are looking. Player one's left/right (their
	/// page has no other row: the gold and experience sliders are gone) or the row's arrows step
	/// it, until player one accepts. The guest is told at accept, when player two's Player is
	/// built (CoopSession states it whenever the setting changes).
	/// </summary>
	private void DriveCoopModeRow(Engine_UiOwnedBook? book)
	{
		bool show = book is not null && CoopSession.LocalCoop && _coopScreen is { Closing: false };
		if (!show)
		{
			if (_coopModeRow is not null && IsInstanceValid(_coopModeRow))
			{
				_coopModeRow.Visible = false;
			}

			_coopModeHeld = 0;
			return;
		}

		Node3D? page = FindAnchor(book!, "Foreground.LeftPage");
		if (page is null)
		{
			return;
		}

		if (_coopModeRow is null || !IsInstanceValid(_coopModeRow))
		{
			_coopModeRow = Engine_UiCoopModeRow.FromPattern(
				_coopPattern?.ArtSourceNode(Engine_UiCoopModeRow.PatternName, 0), _coopPattern ?? book!.Book);
			if (_coopModeRow is null)
			{
				GD.Print("[game-ui] co-op screen row: no pickbox art to build it from");
				return;
			}
		}

		if (_coopModeRow.GetParent() != page)
		{
			_coopModeRow.GetParent()?.RemoveChild(_coopModeRow);
			page.AddChild(_coopModeRow);

			// Under player one's name and over their figure's head, where the page is bare: at the
			// first share slider's place (`Sliders` (−124, 71) + `slider1` (0, −160)) the label ran
			// across the figure's waist, which stands in front of it. The share sliders' x.
			_coopModeRow.Position = new Vector3(-124f, 184f, 0f);
		}

		_coopModeRow.Visible = true;
		_coopModeRow.Show(CoopScreenMode());

		bool locked = _coopScreen is { LeftAccepted: true } or { PickerOpen: true };
		int sideways = Fable2Input.MenuHorizontal;
		if (sideways != 0 && sideways != _coopModeHeld && !locked)
		{
			StepCoopScreenMode(sideways);
		}

		_coopModeHeld = sideways;
	}

	/// <summary>The row (Engine_CameraScreens.Row): the screen mode and the orientation settings.</summary>
	private static int CoopScreenMode() =>
		Refii.Camera.Engine_CameraScreens.Row(Refii.Camera.Engine_CameraScreens.Chosen, GameSplitView.WantsStacked);

	private void StepCoopScreenMode(int direction)
	{
		int row = CoopScreenMode() + direction;
		(Refii.Camera.Engine_CameraScreens.Mode mode, bool stacked) =
			Refii.Camera.Engine_CameraScreens.FromRow(row, GameSplitView.WantsStacked);
		var settings = new Dictionary<string, string>
		{
			[Refii.Camera.Engine_CameraScreens.ModeKey] = Refii.Camera.Engine_CameraScreens.Name(mode),
			[GameSplitView.OrientationKey] = stacked ? "horizontal" : "vertical",
		};

		// The old switch goes with the first choice made here: the mode setting replaces it.
		bool written = Engine_UiScreenActions.WriteSettings(settings, new[] { Refii.Camera.Engine_CameraScreens.LegacySplitKey });
		GD.Print($"[game-ui] co-op screen row: {Engine_UiCoopModeRow.Labels[Refii.Camera.Engine_CameraScreens.Row(mode, stacked)]}" +
			(written ? string.Empty : " (the setting could not be written)"));
	}

	/// <summary>
	/// The mouse on the co-op screen, on this machine's page. A button is its pad button's code —
	/// Accept A, Cancel or Reject B, Select Save Y — Load Hero X is Godot's own-hero toggle, as
	/// player two's X is, and a henchman row's arrow steps the focus to that row and its value one
	/// way. Any other click is taken, so it cannot accept the screen by falling through as A.
	/// </summary>
	private void PointerCoop()
	{
		if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } at ||
			CoopBook() is not { } book || _coopScreen is not { } state || state.PickerOpen)
		{
			return;
		}

		Engine_UiPointer.TakeLeft();

		bool Hit(string path, float grow = 6f) =>
			book.NodeFor(path) is { } node && node.IsVisibleInTree() &&
			Engine_UiPointer.CanvasBounds(node) is { } bounds && bounds.Grow(grow).HasPoint(at);

		if (!CoopDrivesRightPage)
		{
			if (_coopModeRow is { Visible: true } row && row.ArrowAt(at) is var arrow and not 0 &&
				!state.LeftAccepted)
			{
				StepCoopScreenMode(arrow);
			}
			else if (Hit("Foreground.LeftPage.buttons.AcceptGroup"))
			{
				PostCoopPress(Fable2CoopScreenPresses.Accept);
			}
			else if (Hit("Foreground.LeftPage.buttons.CancelGroup"))
			{
				PostCoopPress(Fable2CoopScreenPresses.Back);
			}

			return;
		}

		foreach ((string path, int code) in new[]
		{
			("Foreground.RightPage.buttons.AcceptGroup", Fable2CoopScreenPresses.Accept),
			("Foreground.RightPage.buttons.CancelGroup", Fable2CoopScreenPresses.Back),
			("Foreground.RightPage.buttons.LoadGroup", Fable2CoopScreenPresses.ButtonY),
		})
		{
			if (Hit(path))
			{
				PostCoopPress(code);
				return;
			}
		}

		if (Hit("Foreground.RightPage.ProfileSelector.Buttons.icon_button_x") ||
			Hit("Foreground.RightPage.ProfileSelector.Buttons.Title2"))
		{
			RequestCoopHeroBody(!Fable2PageProviders.CoopOwnHero);
			return;
		}

		// A henchman row: the focus to it (`+156`, up/down are 28/29), then its value.
		for (int row = 0; row < 3; ++row)
		{
			string box = $"Foreground.RightPage.Pickbox.pickbox{row + 1}.grp";
			int value = Hit(box + ".arrowleft", 10f) ? -1 : Hit(box + ".arrowright", 10f) ? 1 : 0;
			if (value == 0 && !Hit(box + ".frame"))
			{
				continue;
			}

			int steps = row - state.RightFocus;
			PostCoopSteps(steps > 0 ? Fable2CoopScreenPresses.Down : Fable2CoopScreenPresses.Up, System.Math.Abs(steps));
			if (value != 0)
			{
				PostCoopSteps(value > 0 ? Fable2CoopScreenPresses.Right : Fable2CoopScreenPresses.Left, 1);
			}

			return;
		}
	}

	/// <summary>The co-op screen's figure stage — both pages' figures, the original's perspective.</summary>
	private Engine_UiFigureStage? _coopStage;

	/// <summary>When player two last picked a body (`Time.GetTicksMsec`), until it reaches the page.</summary>
	private ulong _coopHeroPickedAt;

	private static float CoopFigureDistance(float godotZ) =>
		Engine_UiFigureStage.CanvasPlaneDistance + (godotZ * Engine_UiFigureStage.MetresPerCanvasUnit);

	/// <summary>What the figures last stood as, so the line is printed once per change.</summary>
	private string _coopFiguresSaid = string.Empty;

	/// <summary>
	/// Per page, the figure the guest last filed there, when it reached this screen
	/// (`Time.GetTicksMsec`), and whether its wait for a body has been printed.
	/// </summary>
	private readonly (uint Entity, ulong At, bool Said)[] _coopFigureFiled = new (uint, ulong, bool)[2];

	/// <summary>
	/// THE CO-OP SCREEN'S FIGURES, AS THE ORIGINAL STANDS THEM. Each page has a `model_proxy.model`
	/// node and a guest-spawned creature for it: player two's henchman (`sub_82438818`) on the
	/// right, and on the left a `CreatureHeroMannequin*` the guest dresses as player one
	/// (`sub_826D7650`, 0x82436A2C). Both are drawn from the entity feed's composed bodies on
	/// <see cref="Engine_UiFigureStage"/>, which reproduces the guest's own GUI world: the node's
	/// canvas point is where the feet stand, its `rotation.y` the heading, its stated `scale.x`
	/// the one uniform size. The anchors are authored below the page (y −450) on purpose: the
	/// figures are 3.5× and stand from under the page's edge to the name line. They step aside
	/// while player two's save fan is up.
	/// </summary>
	private void DriveCoopFigures()
	{
		if (!HostBridge.IsInGame)
		{
			Engine_UiPreviewFigures.Reset();
		}

		Engine_UiOwnedBook? book = _coopScreen is { PickerOpen: false } ? CoopBook() : null;
		(uint Henchman, uint PlayerOne)? figures = book is null
			? null
			: (Engine_UiPreviewFigures.Entity(Engine_UiPreviewFigures.Henchman),
				Engine_UiPreviewFigures.Entity(Engine_UiPreviewFigures.PlayerOne));
		GameEntities? feed = GetParent()?.GetNodeOrNull<GameWorld>("GameWorld")?.Entities;
		if (book is null || figures is not { } stood)
		{
			feed?.Watch(0u, 0u);
			_coopStage?.HideAll();
			System.Array.Clear(_coopFigureFiled);
			return;
		}

		// Both figures are re-read on every walk while the screen stands: a pick redresses one at
		// once in the guest, and the feed's own cadence would show it up to half a second late.
		feed?.Watch(stood.PlayerOne, stood.Henchman);

		if (_coopStage is null || !IsInstanceValid(_coopStage))
		{
			_coopStage = new Engine_UiFigureStage { Name = "CoopFigures" };
		}

		if (_coopStage.GetParent() != book)
		{
			_coopStage.GetParent()?.RemoveChild(_coopStage);
			book.AddChild(_coopStage);
		}

		string said = Stand(0, "Foreground.LeftPage.model_proxy.model", stood.PlayerOne) + "; " +
			Stand(1, "Foreground.RightPage.model_proxy.model", stood.Henchman);
		if (said != _coopFiguresSaid)
		{
			_coopFiguresSaid = said;
			GD.Print($"[game-ui] co-op figures — {said}");
		}

		string Stand(int slot, string path, uint entity)
		{
			Node3D? anchor = FindAnchor(book, path);
			MeshInstance3D? body = entity == 0u ? null : feed?.NodeFor(entity);
			if (_coopFigureFiled[slot].Entity != entity)
			{
				_coopFigureFiled[slot] = (entity, Time.GetTicksMsec(), false);
			}

			if (anchor is null || entity == 0u)
			{
				_coopStage.Hide(slot);
				return anchor is null ? $"{path} not built" : $"slot {slot} has no figure from the guest";
			}

			// The stage sits on the canvas plane through the view's centre, at the anchor's depth,
			// so the page's mask still covers it the way it covers the anchor.
			Transform3D canvas = Engine_UiTheme.CanvasTransform(anchor);

			// THE DISTANCE IS THE LIVE ANCHOR'S, BY CHOICE. The owned book stacks its chrome units in
			// depth (the anchor reads z 900), which stands the figures 49 m out; the file's own chain
			// (z 350 · 1.04) would put them at the original's 36.4 m, about a third larger. The
			// remaster keeps the 49 m framing — the player's call, 2026-10-21.
			Vector3 placed = canvas.Origin;

			Transform3D parent = Engine_UiTheme.CanvasTransform(book);
			_coopStage.Transform = parent.AffineInverse() *
				new Transform3D(Basis.Identity, new Vector3(0f, 0f, canvas.Origin.Z));
			_coopStage.Frame(
				Engine_UiSceneBuilder.VisibleHalfWidth,
				Engine_UiSceneBuilder.CanvasHeight / Engine_UiSceneBuilder.PixelsPerUnit,
				(Vector2I)GetViewport().GetVisibleRect().Size);

			// The node's own heading: the build turns `rotation.y` into −y about Godot's up.
			float heading = -anchor.Transform.Basis.Orthonormalized().GetEuler().Y;
			float? stated = book.StatedScaleX(path);
			Skeleton3D? rig = body is not null && !body.Skeleton.IsEmpty
				? body.GetNodeOrNull<Skeleton3D>(body.Skeleton)
				: null;
			Mesh? before = _coopStage.MeshOf(slot);
			bool shown = _coopStage.Show(
				slot, entity, body?.Mesh, body?.Skin, rig, placed, heading,
				stated ?? Engine_UiFigureStage.AuthoredScalePercent);

			// HOW LONG A PICK TAKES TO REACH THE PAGE, from the press to the redressed body on the
			// stage — the guest redresses its mannequin within a tick of the press.
			if (slot == 1 && _coopHeroPickedAt != 0UL && shown && !ReferenceEquals(before, _coopStage.MeshOf(slot)))
			{
				GD.Print($"[game-ui] co-op figures — player two's pick on the page " +
					$"{Time.GetTicksMsec() - _coopHeroPickedAt} ms after the press");
				_coopHeroPickedAt = 0UL;
			}

			// HOW LONG A FILED FIGURE WAITS FOR ITS BODY: the guest files it (`@Preview.Add`) in the
			// tick it makes it; the feed assembles the body off-thread from the parts it states.
			if (shown && !_coopFigureFiled[slot].Said)
			{
				_coopFigureFiled[slot].Said = true;
				GD.Print($"[game-ui] co-op figures — slot {slot} entity 0x{entity:X8} on the page " +
					$"{Time.GetTicksMsec() - _coopFigureFiled[slot].At} ms after the guest filed it");
			}

			return shown
				? $"slot {slot} entity 0x{entity:X8} at ({canvas.Origin.X:F0},{canvas.Origin.Y:F0}), " +
					$"{CoopFigureDistance(placed.Z):F1} m out " +
					$"heading {heading:F2} scale {(stated is { } s ? s.ToString("F0") : "authored")}"
				: $"slot {slot} entity 0x{entity:X8} " + Refii.Entities.Engine_FigureBody.Describe(
					feed?.BodyStateOf(entity) ?? Refii.Entities.Engine_FigureBody.State.NotWalked);
		}
	}
}
