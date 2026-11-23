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
/// <para>THE MOUSE PRESSES PAD BUTTONS for this machine's page: the pointer's presses reach pad 0,
/// which is player one on the host and on the couch, and player two on a joiner. A row the
/// pointer picks is reached the way the pad reaches it — the focus stepped with up/down, the value
/// with left/right — so the guest's own rows, sync and respawns all stay the guest's.</para>
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

	/// <summary>Pad presses a pointer target needs, played one at a time.</summary>
	private readonly Queue<uint> _coopPresses = new();

	private double _coopPressClock;

	/// <summary>Player one's left/right last frame, so a held direction changes the mode once.</summary>
	private int _coopModeHeld;

	/// <summary>Player two's save picker was up last frame.</summary>
	private bool _coopPickerWasOpen;

	/// <summary>
	/// A press and its release: the pointer holds a press 0.1 s, and the guest's own repeat filter
	/// needs to see the release before the next press counts as one.
	/// </summary>
	private const double CoopPressSpacing = 0.22;

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

		if (book is not null && _coopPattern is null)
		{
			_coopPattern = _presentation?.PatternBookFor("coopscreen");
		}

		DriveCoopSaveFan(book, delta);
		DriveCoopModeRow(book);
		PlayCoopPresses(delta);
	}

	private void PlayCoopPresses(double delta)
	{
		_coopPressClock = System.Math.Max(0.0, _coopPressClock - delta);
		if (_coopScreen is null)
		{
			_coopPresses.Clear();
			return;
		}

		if (_coopPressClock > 0.0 || !_coopPresses.TryDequeue(out uint press))
		{
			return;
		}

		GameInput.QueuePointerPress(press);
		_coopPressClock = CoopPressSpacing;
	}

	/// <summary>Queues <paramref name="count"/> presses of one button.</summary>
	private void QueueCoopPresses(uint button, int count)
	{
		for (int i = 0; i < count; ++i)
		{
			_coopPresses.Enqueue(button);
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

		_coopSaveCards.Select(state.PickerCursor);
		PointCoopSaveFan(state);
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
		for (int i = 0; i < state.Saves.Count && i < Engine_FrontEndSaveCards.MaxCards; ++i)
		{
			// Card k (1-based) shows save k − base: `sub_8243B520(screen, k, texture, …)` writes
			// `loadgame.Group.Card{k}…screenshot`.
			int card = baseSlot + i;
			Texture2D? portrait = book.StatedTexture($"loadgame.Group.Card{card}.Group.screenshot");
			portraits += portrait is null ? 0 : 1;
			saves.Add(new Engine_FrontEndSaveCards.Save(state.Saves[i].Text, portrait));
		}

		if (portraits == _coopSaveCardsPortraits && _coopSaveCards.Visible)
		{
			return;
		}

		_coopSaveCardsPortraits = portraits;
		_coopSaveCards.SetSaves(saves, baseSlot);
		_coopSaveCards.Select(state.PickerCursor);
		_coopSaveCards.Visible = true;
		GD.Print($"[game-ui] co-op save fan: {saves.Count} save(s) from card {baseSlot}, " +
			$"{portraits} portrait(s), cursor {state.PickerCursor} — {_coopSaveCards.Describe()}");
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
		if (pointed < 0 || _coopPresses.Count != 0)
		{
			return;
		}

		int steps = pointed - state.PickerCursor;
		if (steps != 0 && (clicked || Engine_UiPointer.Moved))
		{
			QueueCoopPresses(steps > 0 ? GameInput.PadRight : GameInput.PadLeft, System.Math.Abs(steps));
		}

		if (clicked && state.Saves[pointed].Valid)
		{
			_coopPresses.Enqueue(GameInput.PadA);
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

			// Where the first share slider stood: `Sliders` (−124, 71) + `slider1` (0, −160).
			_coopModeRow.Position = new Vector3(-124f, -89f, 0f);
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

	/// <summary>0 shared, 1 side by side, 2 top and bottom — from the two split-screen settings.</summary>
	private static int CoopScreenMode() =>
		!GameSplitView.Wanted ? 0 : GameSplitView.WantsStacked ? 2 : 1;

	private void StepCoopScreenMode(int direction)
	{
		int mode = (CoopScreenMode() + direction + 3) % 3;
		var settings = new Dictionary<string, string>
		{
			[GameSplitView.SwitchKey] = mode == 0 ? "false" : "true",
		};
		if (mode != 0)
		{
			settings[GameSplitView.OrientationKey] = mode == 2 ? "horizontal" : "vertical";
		}

		bool written = Engine_UiScreenActions.WriteSettings(settings);
		GD.Print($"[game-ui] co-op screen row: {Engine_UiCoopModeRow.Labels[mode]}" +
			(written ? string.Empty : " (the setting could not be written)"));
	}

	/// <summary>
	/// The mouse on the co-op screen, on this machine's page. A button is its pad button —
	/// Accept A, Cancel or Reject B, Select Save Y, Load Hero X — and a henchman row's arrow
	/// steps the focus to that row and its value one way. Any other click is taken, so it cannot
	/// accept the screen by falling through as A.
	/// </summary>
	private void PointerCoop()
	{
		if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } at ||
			CoopBook() is not { } book || _coopScreen is not { } state || state.PickerOpen)
		{
			return;
		}

		Engine_UiPointer.TakeLeft();
		if (_coopPresses.Count != 0)
		{
			return;
		}

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
				_coopPresses.Enqueue(GameInput.PadA);
			}
			else if (Hit("Foreground.LeftPage.buttons.CancelGroup"))
			{
				_coopPresses.Enqueue(GameInput.PadB);
			}

			return;
		}

		foreach ((string path, uint button) in new[]
		{
			("Foreground.RightPage.buttons.AcceptGroup", GameInput.PadA),
			("Foreground.RightPage.buttons.CancelGroup", GameInput.PadB),
			("Foreground.RightPage.buttons.LoadGroup", GameInput.PadY),
			("Foreground.RightPage.ProfileSelector.Buttons.icon_button_x", GameInput.PadX),
			("Foreground.RightPage.ProfileSelector.Buttons.Title2", GameInput.PadX),
		})
		{
			if (Hit(path))
			{
				_coopPresses.Enqueue(button);
				return;
			}
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
			QueueCoopPresses(steps > 0 ? GameInput.PadDown : GameInput.PadUp, System.Math.Abs(steps));
			if (value != 0)
			{
				_coopPresses.Enqueue(value > 0 ? GameInput.PadRight : GameInput.PadLeft);
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

			return shown
				? $"slot {slot} entity 0x{entity:X8} at ({canvas.Origin.X:F0},{canvas.Origin.Y:F0}), " +
					$"{CoopFigureDistance(placed.Z):F1} m out " +
					$"heading {heading:F2} scale {(stated is { } s ? s.ToString("F0") : "authored")}"
				: $"slot {slot} entity 0x{entity:X8} NOT in the entity feed";
		}
	}
}
