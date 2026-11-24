using System.Linq;
using Godot;
using Refii.Core;
using Refii.Ui;
using Refii.Host;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// Renders Fable 2's own front end, in Godot, over the world. The guest owns which screen is up
/// and what state every widget is in; Godot owns the pixels. The screens are the title's real
/// <c>.bgf</c> data, stood up through <see cref="Engine_UiPresentationHost"/>.
///
/// It renders into its OWN SubViewport with its own World3D. A Gameface screen is a 3D scene
/// carrying cameras, lights and quads on a ~720-unit canvas, and Godot renders a viewport
/// through exactly one current camera — sharing the world's viewport means the last camera to
/// declare itself wins, and the front end re-declares its camera on every widget state change
/// (<see cref="Conceal"/> hides meshes but leaves the camera current). The two then compose UI
/// over scene on a transparent background.
///
/// Built at boot, hidden until the startup movies are done: standing up the slide graph costs a
/// visible fraction of a second and would hitch at the logo-to-title cut if deferred.
/// </summary>
public partial class GameUi : Node, IGameTitleUi
{
	/// <summary>
	/// Screen to show before the guest says otherwise — the title, which is what the guest puts
	/// up first. <see cref="FollowGuest"/> takes over once it reports a front end.
	/// </summary>
	[Export] public string Screen { get; set; } = "frontendstartupscreen";

	private Engine_UiPresentationHost? _presentation;
	private SubViewport? _viewport;
	private SubViewportContainer? _container;

	/// <summary>
	/// The main menu's model and its view — Godot's, not the guest's. The guest's menu has no C++
	/// item list and no C++ highlight to read: items, order, nesting and the selection all live
	/// in shipped Lua. So the list, the highlight and the navigation timing are ours and the
	/// guest is told the outcome by name; the art stays the game's.
	/// </summary>
	private Engine_FrontEndMenu? _menu;

	private Engine_FrontEndMenuView? _menuView;

	/// <summary>Debug stand-in for the main menu; replaces the Godot menu view while visible.</summary>
	private DebugMenu? _debugMenu;

	/// <summary>The front end is up (Title/startup or MainMenu) per the last screen change.
	/// Combined with <see cref="IsRevealed"/> per-frame — the reveal is not a screen change,
	/// so the debug menu's visibility cannot be applied only inside the change handler.</summary>
	private bool _frontendUp;

	/// <summary>
	/// The gender card "New Game" opens. The guest models it as a SUBSTATE of the same screen
	/// object — `screen+24` goes to 2 and the menu's button handler stops answering the list's
	/// codes — so only the input changes hands.
	///
	/// Hiding the rows is ours: the file puts every `ExpandableMenu` element at z 0 and the
	/// card's active pose at z -134, so the rows cut across the raised card, and all six root
	/// slides start the menu off-canvas at x -800 rather than parking it. Whatever the shipped
	/// Lua does here is not in the file.
	/// </summary>
	private Engine_FrontEndCardChoice? _cardView;

	/// <summary>
	/// The Load Game card fan. The guest enumerates the saves and builds each card's text — calls
	/// only it can make — and Godot draws and moves the fan from that.
	/// </summary>
	private Engine_FrontEndSaveCards? _saveCardView;

	/// <summary>
	/// The pause menu's model — the guest's own list (<see cref="Engine_PauseMenu.BuildHero"/>),
	/// our highlight. Outlives the view for the same reason <see cref="_menu"/> does: the
	/// player's place in the menu survives the screen coming and going.
	/// </summary>
	private Engine_FrontEndMenu? _pauseMenu;

	/// <summary>Which side the standing pause menu was built for.</summary>
	private bool _pauseMenuJoiner;

	/// <summary>
	/// The pause menu's view — the same widget as the front end's, drawing the same authored
	/// art (`pausemainmenu` is the same ExpandableMenu component), bound to the pause model.
	/// The guest's own button pool and highlight are hidden on every rebuild of the mirrored
	/// pause tree; dispatch is the tab lever (<see cref="HostBridge.PauseTabSelect"/>).
	/// </summary>
	private Engine_FrontEndMenuView? _pauseView;

	/// <summary>
	/// The `GUI.*` native router. Held here rather than statically because it carries this
	/// session's census, which must not outlive the node.
	/// </summary>
	private readonly Engine_UiGuiNatives _guiNatives = new();

	/// <summary>The front end's load cards and dialog as its own entries stated them.</summary>
	private readonly Engine_FrontEndStated _frontEndStated = new();
	private readonly Engine_UiWheelItems _wheelItems = new(line => GD.Print(line));
	private readonly Engine_UiPauseMapKey _pauseMapKey = new();
	private int _wheelItemsTexturesGeneration = -1;

	/// <summary>
	/// The display boxes and counters as the guest's `GUI.*` calls stated them. Owned state, not
	/// a mirror: nothing is read back out of the guest's queues and lifetimes age on our clock.
	/// </summary>
	private readonly Engine_UiGuiBoxes _boxes = new();

	/// <summary>
	/// The menu model the Gameface Lua would have held, built from the C++'s own statements —
	/// see <see cref="Engine_UiMenuModel"/>.
	/// </summary>
	private readonly Engine_UiMenuModel _menuModel = new();

	/// <summary>Presentation addresses the stated live list has named; see TracePresentations.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _menuModelSeenLive = new();

	/// <summary>The movie the guest asked for, from its own `Play*MovieInternal` call.</summary>
	private readonly Engine_UiGuiMovie _movies = new();

	/// <summary>The epilogue, next-episode and loading-screen channels.</summary>
	private readonly Engine_UiGuiScreens _screens = new();

	/// <summary>The expression meter, the golf swing meter and the minigame tally.</summary>
	private readonly Engine_UiGuiWidgets _widgets = new();

	private int _menuModelReported;

	/// <summary>What the `g_Menu` capture had recorded/dropped when it was last reported.</summary>
	private ulong _menuChunksRecorded = ulong.MaxValue;

	private ulong _menuChunksDropped;

	private ulong _boxesReported;

	private bool _guiRoutesReady;

	private Engine_UiOwnedBook? _pauseBook;

	/// <summary>The guest's pausemainmenu presentation address, for the live sample.</summary>
	private uint _pauseAddress;

	/// <summary>The mirrored `pausemainmenu` tree is standing — set by its build, cleared by
	/// its retirement. The owned view only shows over the guest's own backdrop.</summary>
	private bool _pauseTreeUp;

	/// <summary>Last applied tab-availability mask, so the model rebuild and the report fire
	/// on the change rather than every frame. Starts at all-available — the mask's own
	/// fail-open value — because that is what BuildHero constructs.</summary>
	private uint _pauseTabsMask = HostBridge.AllPauseTabs;

	/// <summary>
	/// The pause list's origin: the authored `Buttons`-group canvas position of `pausemainmenu`,
	/// read from the pattern book. The front end's constant is only the front end's.
	///
	/// THREE AXES, NOT TWO. `Buttons` does not sit on the presentation root's plane — the
	/// composed chain is `ExpandableMenu` then `Buttons` and both carry a Z, with `LeftFrame` ten
	/// units in front of `Buttons` inside that group. Using the root's Z is a different parent,
	/// not an offset.
	/// </summary>
	private Vector3 _pauseAnchor = new(
		Engine_FrontEndMenuView.CanvasAnchor.X, Engine_FrontEndMenuView.CanvasAnchor.Y, 0f);

	/// <summary>
	/// The owned surfaces the guest can raise over any screen: the three modal boxes, the
	/// received-item card, the MenuBox, and the two display-only panels — one shape, one object;
	/// see <see cref="Engine_UiSurfaceSet"/>.
	///
	/// The three input masks are supplied rather than asserted by the surfaces: everything under
	/// `Engine/` is shared with the editor, which has no pad and no <see cref="GameInput"/>.
	/// </summary>
	private readonly Engine_UiSurfaceSet _surfaces = new(
		standing => Fable2Input.ModalBoxOwnsInput = standing,
		standing => Fable2Input.ReceiveItemsOwnsInput = standing,
		standing => Fable2Input.MenuBoxOwnsInput = standing);

	/// <summary>This frame's press, in the form an owned surface takes it.</summary>
	private static Engine_UiSurfaceInput SurfaceInput(double delta) => new()
	{
		Accept = Fable2Input.MenuAcceptPressed,
		Back = Fable2Input.MenuBackPressed,
		Flip = Fable2Input.MenuFlipPressed,
		Equip = Fable2Input.MenuEquipPressed,
		Direction = Fable2Input.MenuDirection,
		Horizontal = Fable2Input.MenuHorizontal,
		TabPrev = Fable2Input.MenuTabPrevPressed,
		TabNext = Fable2Input.MenuTabNextPressed,
		Scroll = GameInput.RightStickY,
		Delta = delta,
	};

	/// <summary>
	/// The loading screen, on its own layer above this one. Polled every frame rather than driven
	/// from the level-change contract: the guest owns both whether one is up and how far it has
	/// faded — see <see cref="GameLoadingScreen"/>.
	/// </summary>
	private GameLoadingScreen? _loading;

	/// <summary>
	/// Whether the display takes the game's WIDE layout. Sampled from the viewport and re-read as
	/// the window changes, so a resize moves the HUD.
	/// </summary>
	private bool _isWidescreen = true;

	/// <summary>
	/// True while the host applies the shipped wide layout to a still-mirrored screen. The guest
	/// is deliberately left believing 4:3 — its authored canvas is the one canonical space every
	/// number it states arrives in, and Godot owns how that reaches any display. Never tell the
	/// guest it is on 16:9. Lives only as long as mirrored screens do.
	/// </summary>
	private bool HostAppliesWidescreen => _isWidescreen;

	/// <summary>
	/// True while anything modal is over the menu — the gender card, the load fan, or the
	/// warning box. One place: "is the row menu allowed on screen" is asked from several.
	/// </summary>
	private bool ModalPanelUp =>
		_cardView is { Visible: true } ||
		_saveCardView is { Visible: true } ||
		_warningView is { Visible: true } ||
		_newGameCommitPending ||
		_replacing ||
		OpenListBook is not null ||
		_hostPageBook.IsOpen;

	/// <summary>
	/// Set from the gender card's choice until its "New Game" / "New Game Female" post has gone
	/// out (<see cref="Engine_FrontEndCardChoice.ChooseBoyMilliseconds"/> later): the rows stay
	/// down and unpressable, so a second activation cannot queue behind the first.
	/// </summary>
	private bool _newGameCommitPending;

	/// <summary>
	/// The refusal box, cloned from the shipped `dialog` page. Modal in the guest's sense — it
	/// parks the screen in substate 1 and only an A press clears it; while it is up the menu
	/// takes no input.
	/// </summary>
	private Engine_FrontEndWarning? _warningView;

	/// <summary>Whether the guest was in game last frame — the multiplayer close is edge-triggered.</summary>
	private bool _multiplayerSawInGame;

	/// <summary>The in-game flag as read last frame, for the falling edge <see cref="LeaveGame"/> runs on.</summary>
	private bool _sawInGame;

	/// <summary>
	/// Drops what the game's own presentations fed, because the guest has left the game.
	///
	/// Quit to Title is not a level change and not a screen change. The game holder's
	/// shutdown (`sub_823117A8`) clears `byte_83496A5E` at 0x82311860, frees the world, then
	/// deletes the in-game GUI (`sub_823716D0` zeroes `dword_83496B08` at 0x82371700); the
	/// Start holder's Init constructs a NEW front-end GUI and runs the same Init as first boot.
	/// From that point `FollowInGameUi` never runs again, so nothing else retires what the HUD
	/// and the owned surfaces were reading — the element cache kept the HUD's last state under
	/// an address the guest had freed, and the rebuilt HUD stood over the title screen on it.
	///
	/// Address-keyed state goes because the next game reuses the addresses; the views stay,
	/// they are rebuilt from the banks and re-fed when the game names them again.
	/// </summary>
	private void LeaveGame()
	{
		GD.Print("[game-ui] the guest has left the game — dropping the HUD feed, the owned " +
			"boxes, the menu capture and the element cache");

		RetireDrawn("the guest left the game");
		Refii.Mods.ModScreen.Release();
		Engine_UiModalText.Clear();
		Engine_UiNumericRender.Clear();
		Engine_UiDisplayBoxes.Clear();

		// The HUD's feed is its address; with it at 0 the icons and the piece gate stop, and a
		// null gate hides every stated piece. Hidden outright as well: a piece with no ordinal
		// is not gated and would otherwise stand. Shown again when the game names hud.bgf.
		_hudAddress = 0;
		_hudStand?.Reset();
		_cyclerAddress = 0;
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.ElementActive = null;
			_hud.Visible = false;
			_hud.Values.Reset();
		}

		if (_hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
		{
			_hudTwo.ElementActive = null;
			_hudTwo.Visible = false;
		}

		if (_worldIcons is not null && GodotObject.IsInstanceValid(_worldIcons))
		{
			_worldIcons.Clear();
		}

		if (_worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo))
		{
			_worldIconsTwo.Clear();
		}

		_boxes.Clear();
		LeaveGuiNativeModels();
		_menuModel.Reset();
		_menuModelSeenLive.Clear();
		_estateSnapshot = default;
		HasInGameUi = false;
	}

	/// <summary>The key over the last co-op mannequin line; null before the first one.</summary>

	/// <summary>Joiner only: whether the live hero has become the kit's source.</summary>
	private bool _coopLiveSampling;

	/// <summary>True once <see cref="Reveal"/> has put the UI on screen.</summary>
	public bool IsRevealed { get; private set; }

	/// <summary>Whether the screen stood up at all.</summary>
	public bool IsBuilt => _presentation is not null;

	public Engine_UiTheme? PhotoModeTheme => _presentation?.PatternBookFor(Engine_UiMenuBox.Presentation);

	public override void _Ready()
	{
		// Run after GameWorld and GameEntities (ProcessPriority = 0) so camera transforms and
		// interpolated NPC body/head anchors are already placed for this frame, and right before
		// Engine_HudWorldSprites and Engine_HudReticule (ProcessPriority = 880).
		ProcessPriority = 875;
		PutPhotoModeOnModWheel();

		// THE MOVEMENT SOUND, for every list in one place: the guest's own slide sounds
		// (`SE_GUI_SLIDE_MENU_UP/DOWN`) on the front end's emitter out of a game and the in-game
		// GUI's in one — the same arms its own lists fire (`sub_826A26E8` 0x826A2734/0x826A2760
		// in game, the front-end root's 140/141).
		Engine_FrontEndMenuView.Moved = (_, direction) => MoveCue(direction);

		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PushWarning("[game-ui] no game data; UI not shown.");
			return;
		}

		// THE ONE MODS ROOT — the folder the launcher's panel writes and the mount reads. Walking up
		// from the data root guessed its depth: the installed data root is `assets/game`, so two
		// levels up was the app root and the list read a load order that does not exist.
		Engine_UiNetworkSource.ModsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;

		// Layer 5: over the world, under the videos on layer 10. A movie covers the front
		// end, which is what the boot sequence and every later cutscene want.
		var layer = new CanvasLayer { Name = "UiLayer", Layer = 5 };
		AddChild(layer);

		_container = new SubViewportContainer
		{
			Name = "UiView",
			// The container drives the SubViewport's size, so the front end is rendered at
			// the window's real resolution rather than upscaled from a fixed buffer.
			Stretch = true,
			// The front end is driven by the GUEST's input, read through GameInput. Letting
			// the sub-viewport take Godot's GUI input as well would give a second, host-side
			// route into it that the guest knows nothing about.
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_container);

		_viewport = Engine_UiSceneBuilder.CreateCanvasViewport("UiViewport", SubViewport.UpdateMode.Disabled);
		_container.AddChild(_viewport);

		// The viewport's one true camera, owned here and never freed. Presentation trees carry
		// their own cameras (the host's canvas camera, every mirrored screen's authored
		// perspective one), and freeing the current camera with its tree makes Godot promote
		// whichever it finds next. A camera parented OUTSIDE every presentation cannot be freed
		// by one; DriveUiCamera re-asserts it every frame.
		_uiCameraMount = new Node3D { Name = "UiCameraMount" };
		_viewport.AddChild(_uiCameraMount);
		_uiCamera = Engine_UiSceneBuilder.ActivateCamera(_uiCameraMount);

		_presentation = new Engine_UiPresentationHost
		{
			Name = "Presentation",
			GameDataRoot = dataRoot,
			StartScreen = Screen,

			// Framing camera OFF: it would fit the geometry to the viewport and hide the
			// placement errors the title's own camera aiming has to be checked against.
			UseFramingCamera = false,
			MountMirrorTree = false,
		};

		// The host's own _Ready runs on AddChild, so the screen is already standing up by the
		// time this returns and the theme has a presentation to catalogue.
		_viewport.AddChild(_presentation);

		// When asset mounting completes or modded text overlays arrive, update all UI references:
		System.Action<Refii.Text.Engine_BabelDatabase> adoptStrings = newStrings =>
		{
			if (_loading is not null)
			{
				_loading.Strings = newStrings;
			}
			if (_hud is not null)
			{
				_hud.Values.Strings = newStrings;
			}
			GameWhereabouts.Strings = newStrings;
			Engine_UiInventorySource.Strings = newStrings;

			// A new string book means the mount changed, which also dropped the text banks the
			// item labels resolve through (`Engine_BnkTextBankLoader.SetGameDirectory`); read
			// them again now rather than on the next list that needs them.
			if (HostBridge.IsInGame)
			{
				WarmTextBanks();
			}
		};
		_presentation.StringsChanged += adoptStrings;

		// The host's _Ready may already have adopted a book (a mount that finished before this ran):
		// handed out now, or the item, place and HUD text waits for a menu that happens to pass it on.
		if (_presentation.Strings is { } adopted)
		{
			adoptStrings(adopted);
		}

		_presentation.PatternBooksRetired += OnPatternBooksRetired;

		_books.Host = _presentation;
		_books.Registry = () => _hud?.Values;
		_books.BehaviourFired = PostPresentationEvent;
		_spells.Host = _presentation;
		// The screen names the row; this turns it into the same action the front-end fold uses,
		// so there is one dispatch for the whole feature.
		foreach (Engine_UiListBookScreen listBook in new[] { _multiplayer, _modsBook })
		{
			listBook.Host = _presentation;
			listBook.Books = _books;
			listBook.Action = command =>
			{
				if (command.StartsWith(Engine_UiNetworkSource.CommandPrefix, System.StringComparison.Ordinal))
				{
					DispatchNetwork(command[Engine_UiNetworkSource.CommandPrefix.Length..]);
				}
			};
		}

		// The host page book's two rows ARE the question's two answers.
		_hostPageBook.Host = _presentation;
		_hostPageBook.Books = _books;
		_hostPageBook.Action = command =>
			Engine_UiHostBoxes.Answer(command == Engine_UiHostBoxPage.AcceptCommand);
		RegisterCommands();
		// The front end's statements route from the start: the in-game routes wait for the HUD.
		_guiNatives.Register("@FrontEnd.SaveCard", _frontEndStated.SaveCard);
		_guiNatives.Register("@FrontEnd.Dialog", _frontEndStated.OpenDialog);
		_guiNatives.Register("@FrontEnd.SaveText", _frontEndStated.SaveText);
		_guiNatives.Register("@FrontEnd.SaveList", _frontEndStated.SaveList);
		_guiNatives.Register("@Coop.Enter", Engine_UiCoopStated.Enter);
		_guiNatives.Register("@Coop.Accepted", Engine_UiCoopStated.Accepted);
		_guiNatives.Register("@Coop.Closing", Engine_UiCoopStated.Closing);
		_guiNatives.Register("@CoopPicker.Open", Engine_UiCoopStated.PickerOpen);
		_guiNatives.Register("@CoopPicker.Shown", Engine_UiCoopStated.PickerShown);
		_guiNatives.Register("@CoopPicker.Cursor", Engine_UiCoopStated.PickerCursor);
		_guiNatives.Register("@CoopPicker.Portrait", Engine_UiCoopStated.PickerPortrait);
		_guiNatives.Register("@CrimeScreen.Open", Engine_UiCrimePage.Open);
		_guiNatives.Register("@Preview.Add", Engine_UiPreviewFigures.Add);
		_guiNatives.Register("@Preview.Remove", Engine_UiPreviewFigures.Remove);
		_guiNatives.Register("@PreviewItem.Set", Engine_UiPreviewItems.Set);
		_guiNatives.Register("@PreviewItem.Hide", Engine_UiPreviewItems.Hide);
		_guiNatives.Register("@Logbook.Stats", Engine_UiLogbookPage.StatsFilled);
		_guiNatives.Register("@ExpressionWheel.Item", _wheelItems.State);
		_guiNatives.Register("@PauseMap.Key", _pauseMapKey.State);
		_guiNatives.Register("@PickBox.Init", Engine_UiPickBoxes.Init);
		_guiNatives.Register("@PickBox.Set", Engine_UiPickBoxes.Set);
		_guiNatives.Register("@Widget.Init", Engine_UiRowWidgets.Init);
		_guiNatives.Register("@Widget.Select", Engine_UiRowWidgets.Select);
		_guiNatives.Register("@Slider.Set", Engine_UiRowWidgets.SetSlider);
		_guiNatives.Register("ShowCreditsScreen", StartCreditsRoll);
		_showing = Screen;

		BuildAchievementToast();
		if (HostBridge.IsInGame)
		{
			EnsureRebuiltHud();
		}

		// If Godot is STARTING on the menu, own it immediately rather than waiting to be told.
		// FollowGuest is the other way in and the one a real boot takes; this covers a harness
		// with no guest, so menu ownership is not gated on a guest report.
		if (string.Equals(Screen, Engine_UiFrontEndScreens.PresentationFor(Engine_UiModel.FrontEndScreen.MainMenu),
				System.StringComparison.OrdinalIgnoreCase))
		{
			AdoptMenu();
		}

		// The loading screen, above this layer and below the videos — see GameLoadingScreen.
		// Owned here because this class owns the layer ordering and the texture loader it needs.
		_loading = new GameLoadingScreen
		{
			Name = "LoadingScreen",
			Resolver = path => _presentation?.LoadTexture(path),
			// The game's own font and strings, from the library every Gameface screen uses. The
			// loading screen is not a presentation, but its tip arrives as a Babel KEY.
			Fonts = _presentation?.Fonts,
			Strings = _presentation?.Strings,
		};

		AddChild(_loading);

		// The screen fade. Owned here because a fade sits above the UI and below the loading art,
		// and this class owns the layer ordering. See Engine_UiScreenFade.
		_fade = new Engine_UiScreenFade { Name = "ScreenFade" };
		AddChild(_fade);

		GD.Print($"[game-ui] built '{Screen}' from {dataRoot} " +
			"in its own viewport (hidden until the logos end)");
	}

	/// <summary>
	/// Shows whichever presentation the guest currently has active. Idempotent — rebuilding a
	/// screen graph is not free, so a repeated report of the same screen does nothing.
	/// </summary>
	public void FollowGuest(Engine_UiModel.FrontEndScreen screen)
	{
		if (_presentation is null)
		{
			return;
		}

		string? presentation = Engine_UiFrontEndScreens.PresentationFor(screen);
		if (presentation is null)
		{
			// The front end is gone — take it down; nothing else frees the built screen, and
			// GameWindow conceals the layer only while the guest has NO live UI. None with the
			// front-end GUI still up is a screen that is neither the title nor the main menu.
			if (!HostBridge.FrontEndGuiIsUp)
			{
				LeaveFrontEnd();
			}

			return;
		}

		// THE FRONT END STANDS ON ITS FILE. Its presentation is headless (no .bgf tree is built,
		// gui_headless_presentation.cpp), so there is no element sample: the book plays its own
		// slides and takes what the guest TELLS the presentation (Engine_UiSlideStatements).
		bool owned = _books.DriveFrontEnd(presentation);
		PrebuildMenuAtTitle(presentation, owned);

		// GODOT OWNS EVERY SCREEN IT HAS A TEMPLATE FOR. The guest's tree is SAMPLED for its
		// data and never drawn once a template names the screen — a book that is not standing
		// yet (its art still reading) is a blank frame, not a frame of the guest's screen.
		// Only a presentation Godot has no template for at all is drawn as the guest's.
		// NEVER DRAWN. GODOT OWNS EVERY SCREEN. The guest's tree is a data source; a screen
		// with no template yet is a blank frame and a line in the log, not the guest's picture.
		ShowMirror(false);
		if (!owned && Engine_UiTemplates.ScreenFor(presentation) is null && _untemplated.Add(presentation))
		{
			GD.Print($"[game-ui] Godot has NO TEMPLATE for '{presentation}' — nothing is drawn " +
				"for it (the guest's tree is never shown)");
		}

		_frontendUp = screen != Engine_UiModel.FrontEndScreen.None;
		if (presentation == _showing)
		{
			return;
		}

		// Built on the file's authored scene: the slides are the book's, from the guest's statements.
		if (!_presentation.ShowByName(presentation))
		{
			GD.PushWarning($"[game-ui] the guest is on '{screen}' but '{presentation}' " +
				"is not among the shipped presentations");
			return;
		}

		// The tree that was just built is a NEW node and comes back visible. Restated here as
		// well as above, because between the two the mirror would flash for one frame on every
		// state change — which on the front end is several times a second.
		ShowMirror(false);

		// GODOT OWNS THE MENU from here on.
		//
		// This runs after EVERY ShowByName, not once, because ShowByName rebuilds the whole
		// node tree — the guest's twelve row components come back with it each time, and a
		// screen carrying both their menu and ours is the failure this prevents.
		//
		// Only the STATE is set here; the debug menu's visibility is applied per-frame in
		// _Process, because the reveal after the boot movies is not a screen change and
		// would otherwise never reach a gated ShowDebugMenu.
		_frontendUp = screen != Engine_UiModel.FrontEndScreen.None;
		if (_frontendUp && _frontEndLeft)
		{
			_frontEndLeft = false;
			_frontEndPrebuild.Reset();
			_menuPrebuild = 0;
		}
		if (screen == Engine_UiModel.FrontEndScreen.MainMenu)
		{
			AdoptMenu();
		}
		else
		{
			if (_menuView is not null)
			{
				// Off the main menu — the title, or a screen we do not draw. The view is kept
				// rather than freed so the highlight survives a trip out to the title and back,
				// which is what the original does: leaving the menu does not reset the selection.
				_menuView.Visible = false;

				// The card goes with it and does NOT survive: leaving it armed would put it back
				// over whatever comes next.
				if (_cardView is not null)
				{
					_cardView.Visible = false;
				}
			}
		}

		GD.Print($"[game-ui] following the guest onto '{presentation}' (stood on its file)");
		_showing = presentation;
	}


	/// <summary>
	/// Shows or hides the mirrored front-end tree. It is never freed and never stops being BUILT —
	/// the live art catalogue comes off it and the owned views place themselves against it — so
	/// ownership means only that it stops being drawn.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<string> _untemplated =
		new(System.StringComparer.Ordinal);

	private void ShowMirror(bool show)
	{
		if (_presentation?.MirrorTree is { } tree && IsInstanceValid(tree) && tree.Visible != show)
		{
			tree.Visible = show;
			GD.Print($"[game-ui] the mirrored '{_showing}' tree is now " +
				(show ? "DRAWN (no owned book stands)" : "hidden — Godot's own book is the screen"));
		}
	}

	/// <summary>
	/// Takes the whole front end down, because the guest no longer has one.
	///
	/// Idempotent, and it has to be: this is reached from a per-frame path, and the guest sits
	/// with no front end for the entire game.
	///
	/// The MENU MODEL survives. Only the view goes — the highlight is the player's place in a
	/// list, and the original does not reset it either. The card is different and is reset on
	/// the way in rather than kept; see <see cref="OpenScreen"/>.
	/// </summary>
	private void LeaveFrontEnd()
	{
		if (_showing is null)
		{
			return;
		}

		GD.Print($"[game-ui] the guest has left the front end — taking '{_showing}' down");

		// The load the player waits through starts here (Continue, New Game): its timeline runs
		// through the region build and the hold to the world settling (Engine_LoadTimeline).
		Refii.Core.Engine_LoadTimeline.Open("front end left");

		// Freed, not hidden: both views hold references INTO the tree about to go away (the row
		// view keeps the shipped `MenuItemButton` it duplicates slots from). AdoptMenu re-sources
		// them from the next build; the model they draw is untouched here.
		if (_menuView is not null)
		{
			_menuView.QueueFree();
			_menuView = null;
		}

		if (_cardView is not null)
		{
			_cardView.QueueFree();
			_cardView = null;
		}

		// The load fan goes with them. Choosing a save takes the guest out of the front end
		// immediately, so `DriveLoadCarousel`'s own tidy-up never runs again — it sits behind an
		// `IsRevealed` gate that is now false. Freed, not hidden, for the reason above;
		// `AdoptSaveCards` rebuilds it from the next front end.
		if (_saveCardView is not null)
		{
			_saveCardView.QueueFree();
			_saveCardView = null;
		}

		if (_warningView is not null)
		{
			_warningView.QueueFree();
			_warningView = null;
		}

		// The extras go too: `scrollingbackground` is a front-end presentation drawn behind the
		// by-name screen, otherwise retired only by the in-game sampler up to half a second later.
		RetireDrawn("the guest left the front end");

		// The owned book is hidden, not freed — its pattern book is cached for the session and
		// rebuilding costs a bank read — but nothing drawing the front end may still draw once
		// the guest has left it.
		if (_books.Book(_books.FrontEndBookId) is { } standing)
		{
			standing.Visible = false;
		}

		_books.ReleaseFrontEnd();

		_presentation?.ClearScreen();
		_presentation?.ReleasePatternBooks("frontendstartupscreen", "frontendmainmenu", "scrollingbackground", "logbookscreen");

		_showing = null;
		_frontEndLeft = true;
		_frontEndPrebuild.Reset();
		_menuPrebuild = 0;
	}

	private bool? _developerMenu;

	/// <summary>Whether the launcher's `developer_menu_enabled` setting asks for the overlay.</summary>
	private bool DeveloperMenuEnabled
	{
		get
		{
			if (_developerMenu is null)
			{
				try
				{
					_developerMenu = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _), "developer_menu_enabled", false);
				}
				catch (System.Exception)
				{
					_developerMenu = false;
				}
			}

			return _developerMenu.Value;
		}
	}

	/// <summary>Debug stand-in for the main menu: shows the clickable DebugMenu instead of the
	/// Godot menu view. Created on first use so a build without the debug fixture costs nothing.</summary>
	private void ShowDebugMenu()
	{
		_debugMenu ??= new DebugMenu { Name = "DebugMenu" };
		if (!IsAncestorOf(_debugMenu))
		{
			AddChild(_debugMenu);
		}
		_debugMenu.Visible = true;
		if (_menuView is not null)
		{
			_menuView.Visible = false;
		}
	}

	/// <summary>
	/// Takes the menu over from the presentation: the guest's rows go away, ours go up. Clone the
	/// shipped row components for their ART first, hide their group second — reversed, every row
	/// inherits the invisibility. The group may be ABSENT rather than hidden: the builder drops
	/// an inactive element's whole subtree, so on a slide where the menu is not up there is no
	/// "Buttons" node; the view then falls back to text-only rows.
	/// </summary>
	private void AdoptMenu()
	{
		if (_presentation?.Theme is not { } theme)
		{
			return;
		}

		// Two trees, two jobs: the group to HIDE is the live one (the copy on screen); the row to
		// CLONE is the file's, because the guest keeps its own components faded out.
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
		Node3D? template = pattern is null
			? null
			: Engine_FrontEndMenuView.RowTemplateIn(pattern);

		if (_menuView is null)
		{
			// The MODEL outlives the view: the view holds nodes from the tree freed when the
			// guest leaves the front end, while the model holds the list and the highlight —
			// the player's place in a menu, which survives a trip to the title and back.
			_menu ??= Fable2FrontEndRoot.BuildRoot();

			// The authored anchor and cursor pose, read from the book — see
			// Engine_FrontEndMenuView.Anchor for why the constant is 25 low. The cursor assembly
			// carries its visuals in a wrapper authored 210 up from its root, so it is rebased
			// against the same anchor rather than zero-rooted.
			Vector3 anchor = Engine_FrontEndMenuView.CanvasAnchor;
			if (pattern is not null)
			{
				Vector3 origin = Engine_UiTheme.CanvasTransform(pattern).Origin;
				anchor = new Vector3(origin.X, origin.Y, 0f);
			}

			Engine_UiTheme cursorBook = _presentation?.Templates ?? theme;
			Transform3D? cursorPose = null;
			if (cursorBook.ArtSourceNode("MenuHighlight") is { } highlightSource)
			{
				Transform3D authored = Engine_UiTheme.CanvasTransform(highlightSource);
				authored.Origin -= anchor;
				cursorPose = authored;
			}

			// Every time the menu comes up, not once: the pump's first sample may land after
			// the first build, and the player can change the setting and come back.
			ApplySubtitlesEmphasis();
			_menuView = new Engine_FrontEndMenuView
			{
				Name = "FrontEndMenu",
				Theme = theme,
				RowTemplate = template,
				Anchor = anchor,
				// The shipped highlight art, if this slide carries it. UpdateCursorColor only
				// changes its opacity and the scripts never move it, so a clone at its authored
				// spot is what the original does. From the pattern book, like the row.
				CursorTemplate = cursorBook.Art("MenuHighlight"),
				CursorPose = cursorPose,

				// The star and pip slides out of THIS file's button pool — every row sits on
				// one, the command rows on the empty slide. See Engine_FrontEndMenuView.ApplyPoses.
				Ratings = Engine_UiRowRating.Resolve(_presentation?.CurrentScreen),
			};

			// Parented to the HOST, not the built screen: that screen is destroyed and recreated
			// on every guest state change. Both have identity transforms, so they share the
			// authored canvas space. Non-null — the theme test above only passes with a live
			// presentation; the `?.` reads are for TEMPLATES, which a half-loaded host may lack.
			_presentation!.AddChild(_menuView);
			_menuView.Bind(_menu);

			GD.Print($"[game-ui] Godot owns the main menu now — " +
				$"{_menu.Visible.Count} entries, " +
				$"row art {(template is null ? "NOT sourced (text-only rows)" : "cloned from the presentation")}, " +
				$"cursor {(_menuView.CursorTemplate is null ? "stand-in" : "shipped")}");
		}

		// Only refresh the modal views if they have already been opened; building all three
		// subtrees and decoding their 4x textures on the `frontendmainmenu` transition frame
		// compounds the GPU upload spike when the menu first appears.
		if (_cardView is not null)
		{
			AdoptCardChoice(theme);
		}
		else if (_presentation?.MirrorTree is not null &&
			theme.ArtSourceNode(Engine_FrontEndCardChoice.GroupName) is { } cardGroup)
		{
			cardGroup.Visible = false;
		}

		if (_saveCardView is not null)
		{
			AdoptSaveCards(theme);
		}
		else if (_presentation?.MirrorTree is not null &&
			theme.ArtSourceNode(Engine_FrontEndSaveCards.PageName) is { } savePage)
		{
			savePage.Visible = false;
		}

		if (_warningView is not null)
		{
			AdoptWarning(theme);
		}
		else if (_presentation?.MirrorTree is not null &&
			theme.ArtSourceNode(Engine_FrontEndWarning.GroupName) is { } warningGroup)
		{
			warningGroup.Visible = false;
		}

		// The guest's own rows, out of the way. Hidden rather than freed: the tree is rebuilt
		// from the file on the next state change anyway, and freeing a node the theme's
		// catalogue still points at would leave a dangling entry behind.
		if (_presentation?.MirrorTree is not null)
		{
			if (group is not null)
			{
				group.Visible = false;
			}

			// And its highlight: `MenuHighlight` is a SIBLING of the row group, not a child, so
			// hiding the group leaves it painting at its authored spot. Cloned first, hidden second.
			// EVERY instance — the screen ships its furniture twice, the authored 4:3 layout and the
			// 16:9 copy on a marker child — and the count goes in the takeover line.
			int highlightsHidden = 0;
			foreach (Engine_UiTheme? catalogue in new[] { _presentation?.Templates, theme })
			{
				if (catalogue is null)
				{
					continue;
				}

				foreach (Node3D instance in catalogue.ArtSourceNodes("MenuHighlight"))
				{
					if (instance.Visible)
					{
						instance.Visible = false;
						++highlightsHidden;
					}
				}
			}

			if (highlightsHidden != 0)
			{
				GD.Print($"[game-ui] hid {highlightsHidden} guest MenuHighlight instance(s) — " +
					"our cloned cursor is the only highlight now");
			}
		}

		// Registered, so the hide survives the per-state-change rebuild: the hides above cover
		// the tree standing now, the registration every tree after it.
		_presentation!.SuppressedElementNames.Add("MenuHighlight");
		_presentation.SuppressedElementNames.Add(Engine_FrontEndMenuView.RowGroupName);

		// Not while any modal panel is up, and `ModalPanelUp` must list all of them: this runs
		// after EVERY rebuild, so an unconditional show puts the rows back over whatever is in
		// front of them a fraction of a second after they were hidden.
		_menuView.Visible = !ModalPanelUp;
		ApplyMenuAvailability();
	}

	/// <summary>
	/// Takes the gender card over from the presentation, as <see cref="AdoptMenu"/> takes the
	/// rows. Built HERE rather than on New Game: the art can only be cloned while
	/// `frontendmainmenu` is standing, and by then the screen may have been rebuilt under the
	/// catalogue. The shipped group is hidden on every rebuild — its `Intro` slide only parks it
	/// at x -831/883, and a live slide brings it back into the canvas.
	/// </summary>
	private void AdoptCardChoice(Engine_UiTheme theme)
	{
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndCardChoice.GroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndCardChoice.GroupName);

		if (_cardView is null)
		{
			// Cloned from the FILE, placed by the LIVE group's transform: the pattern book has the
			// art, the live tree is the authority on where on the canvas the group sits.
			_cardView = Engine_FrontEndCardChoice.FromPresentation(pattern ?? group);
			if (group is not null)
			{
				_cardView.Transform = Engine_UiTheme.CanvasTransform(group);
			}

			_presentation!.AddChild(_cardView);

			GD.Print($"[game-ui] gender card ready — art " +
				$"{(_cardView.HasShippedArt ? "cloned from 'choosecard'" : "NOT sourced (stand-ins)")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(group is null ? ", group ABSENT from this build" : string.Empty)}");
		}

		// The LIVE group, re-hidden on every rebuild including the first: its tree is rebuilt
		// from the file on every guest state change and comes back visible.
		if (group is not null && _presentation?.MirrorTree is not null)
		{
			group.Visible = false;
		}
	}

	/// <summary>
	/// Takes the Load Game fan over from the presentation, as <see cref="AdoptCardChoice"/> takes
	/// the gender card. Built HERE, not when the guest opens its carousel: the card art can only
	/// be cloned while `frontendmainmenu` is standing, and by substate 5 the screen has been
	/// rebuilt several times. The shipped `loadgame` page is hidden on every rebuild — its
	/// parking at y -598 is only the closed slide, and a live slide brings it back.
	/// </summary>
	private void AdoptSaveCards(Engine_UiTheme theme)
	{
		Node3D? page = theme.ArtSourceNode(Engine_FrontEndSaveCards.PageName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndSaveCards.PageName);

		if (_saveCardView is null)
		{
			// Cloned from the FILE, placed by the LIVE page's transform — same split as the
			// gender card: the guest leaves its own copy in whatever state its last slide put it.
			_saveCardView = Engine_FrontEndSaveCards.FromPresentation(pattern ?? page);
			_saveCardView.Theme = theme;
			if (page is not null)
			{
				_saveCardView.Transform = Engine_UiTheme.CanvasTransform(page);
			}

			_presentation!.AddChild(_saveCardView);

			GD.Print("[game-ui] load fan ready — art " +
				$"{(_saveCardView.HasShippedArt ? "cloned from 'loadgame'" : "NOT sourced (stand-ins)")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(page is null ? ", page ABSENT from this build" : string.Empty)}");
		}

		if (page is not null && _presentation?.MirrorTree is not null)
		{
			page.Visible = false;
		}
	}

	/// <summary>True while the warning box shows the GUEST's dialog rather than a host refusal.</summary>
	private bool _guestDialogShown;

	private bool _menuHiddenForDialog;

	private string? _guestDialogShownText;

	/// <summary>The statement the shown dialog was drawn from; a later raise takes only a newer one.</summary>
	private ulong _guestDialogShownSequence;

	private ulong _guestDialogClosedAt;

	private bool _guestDialogUnstatedReported;

	/// <summary>
	/// THE FRONT END'S OWN DIALOG. The guest raises it itself — Load Game with no saves or a
	/// refused save, a co-op or network failure arriving on the menu or the title, Downloadable
	/// Content with nobody signed in — through OpenDialog (`sub_826E40E0`, disc `sub_826CB158`),
	/// and waits in its dialog mode for the owner's own accept. The host's warning box draws the
	/// owner's statement of that call (Engine_FrontEndStated.Dialog), and A/B go back through
	/// `FrontEndDialog:36/37`. The host's own refusals never raise it, so the two cannot stand
	/// together.
	/// </summary>
	private bool DriveGuestDialog()
	{
		Fable2UiSource.FrontEndDialogState? raised = !HostBridge.IsInGame
			? Fable2UiSource.Active.GetFrontEndDialog()
			: null;
		Fable2UiSource.FrontEndDialogState? up = IsRevealed ? raised : null;
		if (up is not { } dialog)
		{
			if (_guestDialogShown)
			{
				_guestDialogShown = false;
				_guestDialogShownText = null;
				_guestDialogClosedAt = _guestDialogShownSequence;
				if (_warningView is not null)
				{
					_warningView.Visible = false;
				}

				if (_menuHiddenForDialog && _menuView is not null)
				{
					_menuView.Visible = true;
				}

				_menuHiddenForDialog = false;
				GD.Print("[game-ui] front-end dialog closed by the guest");
			}

			return false;
		}

		if (_warningView is null && _presentation?.Theme is { } theme)
		{
			AdoptWarning(theme);
		}

		if (_warningView is null)
		{
			return false;
		}

		// The raise's own statement; one an earlier raise was drawn from is never taken.
		if (_frontEndStated.LastDialog is not { } stated || stated.Sequence <= _guestDialogClosedAt)
		{
			if (!_guestDialogUnstatedReported)
			{
				_guestDialogUnstatedReported = true;
				GD.Print("[game-ui] front-end dialog raised with no statement yet");
			}

			return true;
		}

		_guestDialogUnstatedReported = false;
		string signature = $"{stated.Sequence}";
		if (signature != _guestDialogShownText)
		{
			_guestDialogShownText = signature;
			_guestDialogShownSequence = stated.Sequence;
			_warningView.Show(_presentation?.Theme, stated.Body, stated.Ok.Length > 0 ? stated.Ok : "GUI_OK",
				twoButton: stated.AllowCancel,
				cancelTag: stated.CancelTag.Length > 0 ? stated.CancelTag : null,
				glyph: stated.Glyph != Engine_FrontEndWarning.BlankGlyph);
			_warningView.AcceptOpens = null;
			GD.Print($"[game-ui] front-end dialog stated by 0x{stated.Owner:X8} — '{stated.Body}' " +
				$"({(stated.AllowCancel ? "two" : "one")}-button, glyph 0x{stated.Glyph:X8}) — {_warningView.LastReport}");
		}

		if (!_guestDialogShown && _menuView is { Visible: true })
		{
			_menuView.Visible = false;
			_menuHiddenForDialog = true;
		}

		_guestDialogShown = true;
		// ONLY THE MOUSE IS POSTED. The pad is not masked in mode 6 (GameInput.MaskMenuInput),
		// so A and B reach the owner's own input slot `sub_826DEE40` directly — 36 at
		// 0x826df9c0, 37 under `+206` at 0x826dfc98. Posting the pad's press as well answered
		// the dialog twice, and the second landed on the list once the first had closed it.
		int clicked = _warningView.Click();
		if (dialog.Accepting)
		{
			if (clicked > 0)
			{
				Engine_UiSource.Current.PostFrontEndAction("FrontEndDialog:36");
			}
			else if (clicked < 0 && stated.AllowCancel)
			{
				Engine_UiSource.Current.PostFrontEndAction("FrontEndDialog:37");
			}
		}

		return true;
	}

	/// <summary>
	/// Adopts the shipped `dialog` group — the box every refusing guard puts up.
	///
	/// Same shape as the gender card above, and hidden the same way: with Godot owning the
	/// front end, leaving the guest's own group live in the tree would draw a second box under
	/// ours. It is a PAGE of `frontendmainmenu` rather than a presentation of its own, which is
	/// why it is adopted here rather than loaded.
	/// </summary>
	private void AdoptWarning(Engine_UiTheme theme)
	{
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndWarning.GroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndWarning.GroupName);

		// Off the main menu — the title, where a host box can be raised too — the shown screen's
		// catalogue does not carry the page: the front end's own pattern book does, at the place
		// the file authors it. A catalogue build, not the live tree, so the paper's parked middle
		// column is there to grow a long host box (Engine_UiPaperBox).
		pattern ??= _presentation?.PatternBookFor(FrontEndMenuPresentation)?.ArtSourceNode(Engine_FrontEndWarning.GroupName);

		if (_warningView is null)
		{
			_warningView = Engine_FrontEndWarning.FromPresentation(pattern ?? group);
			if (group is not null)
			{
				_warningView.Transform = Engine_UiTheme.CanvasTransform(group);
			}
			else if (pattern is not null)
			{
				_warningView.Transform = Engine_UiTheme.CanvasTransform(pattern);
			}

			_presentation!.AddChild(_warningView);

			GD.Print("[game-ui] warning box ready — art " +
				$"{(_warningView.HasShippedArt ? "cloned from 'dialog'" : "NOT sourced")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(group is null ? ", group ABSENT from this build" : string.Empty)}");
		}

		if (group is not null && _presentation?.MirrorTree is not null)
		{
			group.Visible = false;
		}
	}

	/// <summary>How far this screen's owned row view stands in front of the mirrored tree.</summary>
	private static float ListLift(uint screenId) =>
		Engine_UiTemplates.Screen(screenId)?.ListLift ??
		(Engine_UiPresentationDepth.Step / 4f);

	/// <summary>Every row in a menu's tree, at any depth — for reporting what a resync moved.</summary>
	private static int CountRows(Engine_FrontEndMenu menu)
	{
		int count = 0;
		foreach (Engine_UiExpandableMenu.Item root in menu.Model.SpawnedItems)
		{
			_ = root;
			++count;
		}

		return count;
	}

	/// <summary>
	/// Takes the pause menu over from the mirrored `pausemainmenu` tree — the guest's list,
	/// values and art; our layout, highlight and navigation. Called on EVERY rebuild of that
	/// tree, which comes back with the guest's own button pool visible; the view itself is built
	/// once, from the FILE-ONLY pattern book.
	/// </summary>
	private void AdoptPauseMenu(Node3D built, uint address)
	{
		if (_presentation is null)
		{
			return;
		}

		_pauseAddress = address;

		// The owned book — chrome, right page, minimap, money — built once from the pattern book.
		// On the pause ROOT it draws instead of the mirrored tree; tab screens fall back to it.
		if (_pauseBook is null || !IsInstanceValid(_pauseBook))
		{
			// The one the loading screen prebuilt, when it got to it; otherwise built here.
			_pauseBook = _books.ClaimPrebuilt(0, PausePresentation) ?? _books.Ensure(0, PausePresentation);
		}

		// The banner, above the book — its own small owned tree, driven by the cycler's text.
		if (_pauseBanner is null || !IsInstanceValid(_pauseBanner))
		{
			_pauseBanner = _presentation.CloneTemplateTree(CyclerPresentation);
			if (_pauseBanner is not null)
			{
				_pauseBanner.Name = "PauseBanner";
				_pauseBanner.Position = new Vector3(0f, 0f, 600f);
				_pauseBanner.Visible = false;
				_presentation.MenuLayer.AddChild(_pauseBanner);
				GD.Print("[game-ui] the pause banner is owned — cloned from 'menucycler'");
			}
		}

		// The guest's own rows and highlight, out of THIS build. A throwaway catalogue rather than
		// FindByName: the screen ships its furniture twice (4:3 and 16:9), so a single lookup
		// hides one layout's copy and leaves the other painting.
		var liveArt = new Engine_UiTheme();
		liveArt.Catalogue(built, PausePresentation);
		int hidden = 0;
		foreach (string name in new[] { Engine_FrontEndMenuView.RowGroupName, "MenuHighlight" })
		{
			foreach (Node3D instance in liveArt.ArtSourceNodes(name))
			{
				if (instance.Visible)
				{
					instance.Visible = false;
					++hidden;
				}
			}
		}

		// THE SIDE CHANGED UNDER A STANDING VIEW: a join, a disconnect or a quit moves the
		// player between the hero's list and the joiner's, and the view is rebuilt for it.
		bool playerTwosPause = PauseIsPlayerTwos();
		if (_pauseView is not null && _pauseMenu is not null &&
			_pauseMenuJoiner != playerTwosPause)
		{
			GD.Print($"[game-ui] pause menu rebuilt — now " +
				(playerTwosPause ? "the joiner's variant" : "the hero's variant"));
			if (GodotObject.IsInstanceValid(_pauseView))
			{
				_pauseView.QueueFree();
			}

			_pauseView = null;
			_pauseMenu = null;
		}

		if (_pauseView is null)
		{
			Engine_UiTheme? book = _presentation.PatternBookFor(PausePresentation);
			Node3D? pattern = book is null ? null : Engine_UiButtonPool.FindButtonPool(book);
			Node3D? template = pattern is null
				? null
				: Engine_FrontEndMenuView.RowTemplateIn(pattern);

			// The anchor is the SCREEN's, not a constant: the slot table is authored in the
			// Buttons group's own space and each screen parks that group where it wants — this
			// one composes ExpandableMenu(-400,0) + Buttons(63,210) = (-337,210), while
			// CanvasAnchor is the front end's (-345,185).
			if (pattern is not null)
			{
				Vector3 origin = Engine_UiTheme.CanvasTransform(pattern).Origin;
				_pauseAnchor = origin;
			}

			// The cursor at the artist's spot: the MenuHighlight assembly carries its visuals in
			// a wrapper authored 210 up from its root, so the authored chain rebased into view
			// space is the pose — a zero-rooted clone paints the A badge at the top of the screen.
			Transform3D? cursorPose = null;
			if (book?.ArtSourceNode("MenuHighlight") is { } highlightSource)
			{
				Transform3D authored = Engine_UiTheme.CanvasTransform(highlightSource);
				authored.Origin -= _pauseAnchor;
				cursorPose = authored;
			}

			// REBUILT WHEN THE SIDE CHANGES. The two variants carry different rows (the joiner's
			// Weapons/Clothing are host tabs over the henchman's list); a menu built once and kept
			// across a join, a disconnect or a quit would keep the wrong variant for the rest of
			// the process.
			bool joiner = playerTwosPause;
			if (_pauseMenu is null || _pauseMenuJoiner != joiner)
			{
				_pauseMenu = Engine_PauseMenu.BuildHero(joiner);
				_pauseMenuJoiner = joiner;
			}

			_pauseView = new Engine_FrontEndMenuView
			{
				Name = "PauseMenu",
				// The end fades as the pause populate states them: `sub_826A19E0` (disc
				// `sub_8240DD98`) runs FadeOutBottomItemEarly and FadeOutTopItemEarly.
				ScreenId = 0,
				StatedEndFades = () => StatedMenuEndFades(_pauseAddress),
				Theme = book,
				RowTemplate = template,
				CursorTemplate = book?.Art("MenuHighlight"),
				CursorPose = cursorPose,
				Ratings = Engine_UiRowRating.Resolve(_presentation.DocumentFor(PausePresentation)),
				Visible = false,
			};

			// Parented to the HOST rather than to the built tree, which is freed on every
			// guest state change — same lifetime split as the front-end view.
			_presentation.MenuLayer.AddChild(_pauseView);
			_pauseView.Bind(_pauseMenu);
			_pauseView.ClipRect = Engine_UiButtonPool.ListClip(_presentation.DocumentFor(PausePresentation));

			GD.Print($"[game-ui] Godot owns the pause menu now — {_pauseMenu.Visible.Count} " +
				$"entries, row art {(template is null ? "NOT sourced (text-only rows)" : "cloned from the pattern book")}, " +
				$"cursor {(_pauseView.CursorTemplate is null ? "stand-in" : "shipped")}" +
				$"{(cursorPose is null ? " (pinned-slot fallback)" : " at its authored spot")}, " +
				$"anchor ({_pauseAnchor.X}, {_pauseAnchor.Y}, z {_pauseAnchor.Z})");
		}

		// The pool's own plane — no lift. In `pausemainmenu.bgf` `Buttons` is authored at z 0,
		// `LeftFrame` at -10 and `MenuHighlight` at -11, which the builder's Z flip turns into
		// 0, +10, +11 toward the camera: cover over rows, badge over cover. `LeftFrame` lives
		// INSIDE `ExpandableMenu`, so our stand-in rows belong where `Buttons` is — not on the
		// presentation root's plane. The anchor's Z is part of the same composed chain as its X
		// and Y; see `_pauseAnchor`.
		_pauseView.Position = new Vector3(
			_pauseAnchor.X - Engine_UiTemplates.WideListShift(0), _pauseAnchor.Y,
			built.Position.Z + _pauseAnchor.Z +
				(Engine_UiTemplates.Screen(0)?.ListLift ?? 0f));

		// THE OWNED BOOK SHARES THE MIRROR'S SLAB. Its cover (+10), badge (+11) and Back bar
		// (+15) are authored relative to `Buttons`, so the book must sit where the rows' base is
		// — left at z 0 while the rows rode the mirror's slab, every row painted over the leather.
		if (_pauseBook is not null && IsInstanceValid(_pauseBook))
		{
			_pauseBook.Position = new Vector3(0f, 0f, built.Position.Z);
		}

		_pauseTreeUp = true;

		if (hidden != 0)
		{
			GD.Print($"[game-ui] hid {hidden} guest pause row/highlight instance(s) in this build");
		}
	}

	/// <summary>The bank entry name of the guest's pause presentation.</summary>
	private const string PausePresentation = "pausemainmenu";

	/// <summary>The pause banner's presentation — owned as a whole-tree clone.</summary>
	private const string CyclerPresentation = "menucycler";

	private Node3D? _pauseBanner;
	private uint _cyclerAddress;
	private string _bannerShown = string.Empty;

	/// <summary>
	/// Keeps the owned banner standing and its title current — the cycler's single live text
	/// is the current screen's NAME ("Paused", "Abilities", …), as the guest tells it.
	/// </summary>
	private void DrivePauseBanner(Engine_UiModel.PauseState? pause)
	{
		if (_pauseBanner is null || !IsInstanceValid(_pauseBanner))
		{
			return;
		}

		bool up = _pauseTreeUp && IsRevealed && pause is not null && pause.Value.ScreenId != 31;
		_pauseBanner.Visible = up;
		if (!up || _cyclerAddress == 0)
		{
			return;
		}

		// The cycler's one meaningful text — the current screen's name, as the guest last told
		// it (SetText / SetTextFromTag on its headless element, Engine_UiSlideStatements).
		string title = Engine_UiSlideStatements.LatestTextOf(_cyclerAddress) ?? string.Empty;

		if (title.Length == 0 || title == _bannerShown ||
			_presentation?.Theme is not { } theme)
		{
			return;
		}

		_bannerShown = title;
		if (Engine_UiTheme.FindByName(_pauseBanner, "Text_15") is MeshInstance3D label)
		{
			theme.SetMeshText(label, title);
		}
	}

	/// <summary>
	/// The in-game screens whose list Godot owns, and the presentation-independent fact that
	/// each of them has one.
	///
	/// The ids are the guest's own: 14 abilities, 15 items/weapons, 16 clothing, 17 logbook,
	/// 18 options, 21 crime, 22 shop, 23 world map, 25 gift, 26 selection, 27 decoration.
	/// Screen 0 is the pause ROOT, which <see cref="DrivePauseMenu"/> owns through the tab
	/// lever instead — a different dispatch, so a different driver.
	/// </summary>
	private static bool ScreenOwnsList(uint screenId) =>
		Engine_UiTemplates.Screen(screenId) is { OwnsList: true };

	/// <summary>
	/// True while a screen's second input LEVEL is up — the options category page — read off the
	/// element and slide value its TEMPLATE names.
	///
	/// The options screen has two input levels and only the first is a list: A on a category sets
	/// `this+24`, runs `g_Menu:SetExpandableMenuDisabled(true)` and opens a PAGE, where 28/29 move
	/// the row, 30/31 change its VALUE and 36 commits the whole page — nothing is written while
	/// scrolling. `sub_826D9FC8` puts `Foreground.ExpandableMenu` on slide 2 when the page opens
	/// and slide 3 when it closes — the guest's told slide entry (Engine_UiSlideStatements).
	///
	/// Fails to FALSE: no template, an unresolved element or an unread slide all leave the list
	/// level standing, which the owned view can drive.
	/// </summary>
	private static bool ScreenPageOpen(uint screenId, uint address, Engine_UiDrawnPresentation drawn) =>
		Engine_UiTemplates.Screen(screenId) is { PageElement.Length: > 0 } template &&
		drawn.Screen is { } screen &&
		Engine_UiScreenPanel.Resolve(screen, template.PageElement) is { } node &&
		node.Ordinal >= 0 &&
		Engine_UiSlideStatements.Of(address, screen.Nodes[0].Element.NameHash, node.Ordinal) is { } told &&
		told.Slide == template.PageSlide;

	/// <summary>
	/// One frame of the owned in-game list. True when it consumed the input.
	///
	/// The guest supplies the rows and HOLDS THE CURSOR — see <see cref="Engine_UiScreenMenu"/>.
	/// Directional input becomes a press and a release on the guest's stick latch, A and B become
	/// action codes 36 and 37 on the screen's handler, and the highlight drawn is read back out
	/// of the layout the guest produced. Yields the frame outright while a modal box is pending.
	/// </summary>
	/// <summary>The world map's region minimap markers, standing under the live `Page2.Contents`.</summary>
	private Engine_UiMinimapMarkers? _minimapMarkers;

	private const uint WorldMapScreenId = 23;

	/// <summary>
	/// The world map's minimap page: the stick's page turn (host-owned, the guest's own page
	/// index never sees our stick) and the markers the guest would have put on its GUI list —
	/// see <see cref="Engine_UiMinimapPage"/>. Every other screen drops the turn, so the map
	/// opens on the readout next time.
	/// </summary>
	private void DriveMinimap(uint screenId, Engine_UiOwnedBook book,
		HostBridge.WorldMapRegion worldMap, Engine_UiExpandableMenu.Item? row, double delta)
	{
		if (screenId != WorldMapScreenId)
		{
			Engine_UiMinimapPage.Reset();
			_minimapMarkers?.Feed(System.Array.Empty<Engine_UiMinimapPage.Marker>());
			return;
		}

		// A click on the right-hand page turns between the region and its map.
		int flip = Fable2Input.MenuHorizontal;
		if (flip == 0 && Engine_UiWorldMapPage.RegionOf(row) >= 0 && PageClicked(book))
		{
			flip = Engine_UiMinimapPage.Shown ? -1 : 1;
		}

		Engine_UiMinimapPage.DriveFlip(
			Engine_UiWorldMapPage.RegionOf(row) >= 0, flip, (float)delta);

		Node3D? contents = Engine_UiMinimapPage.Standing(row)
			? book.NodeFor(Engine_UiMinimapPage.Contents)
			: null;
		if (contents is null)
		{
			_minimapMarkers?.Feed(System.Array.Empty<Engine_UiMinimapPage.Marker>());
			return;
		}

		if (_minimapMarkers is null || !GodotObject.IsInstanceValid(_minimapMarkers))
		{
			_minimapMarkers = new Engine_UiMinimapMarkers
			{
				Name = "MinimapMarkers",
				ResolveTexture = hash => book.ResolveTexture?.Invoke(hash),
			};
		}

		if (_minimapMarkers.GetParent() != contents)
		{
			_minimapMarkers.GetParent()?.RemoveChild(_minimapMarkers);
			contents.AddChild(_minimapMarkers);
		}

		_minimapMarkers.Feed(Engine_UiMinimapPage.Markers(
			row, worldMap, book.AuthoredPosition("Foreground.Book.Pages." + Engine_UiMinimapPage.MapImage)));
	}

	private bool DriveScreenMenu(double delta, Engine_UiModel.PauseState? pause)
	{
		// Every frame, before any early return: a list that goes away clears the statement.
		StateHighlightedRow();
		StateScreenListFold();

		// Re-validate the held node: a Godot node freed elsewhere throws on EVERY access, and an
		// exception once a frame out of _Process reads as a hang.
		if (_screenMenuView is not null && !GodotObject.IsInstanceValid(_screenMenuView))
		{
			_screenMenuView = null;
			_screenMenuAddress = 0;
			_screenMenuScreen = uint.MaxValue;
		}

		if (!IsRevealed || _presentation is null || pause is not { } state ||
			state.ModalUp || !ScreenOwnsList(state.ScreenId) ||
			FindListPresentation(state.ScreenId) is not (uint address, Engine_UiDrawnPresentation drawn))
		{
			if (_screenMenuView is not null)
			{
				_screenMenuView.Visible = false;
			}

			// Start leaving Options with a host page open comes through HERE, not the page
			// branch below: the page goes with its screen, or the next Options visit opens on
			// the page path with the list hidden. A modal over the page keeps the screen id.
			if (Engine_UiHostOptionsPage.Open &&
				(pause is not { } optionsGone || optionsGone.ScreenId != Engine_UiDetailPages.OptionsScreenId))
			{
				Engine_UiOptionsPending.Leave();
			}

			Fable2Input.ScreenMenuOwnsInput = false;

			// A MODAL OVER THE LIST KEEPS THE VIEW. Dropping the address here made AdoptScreenMenu
			// free and rebuild the whole list view — rows re-cloned, carousel restarted — after
			// every box over it. The list is hidden under the box and resumes as it stood.
			if (pause is not { ModalUp: true } boxOver || !ScreenOwnsList(boxOver.ScreenId))
			{
				_screenMenuAddress = 0;
			}

			// SAY WHY WE DECLINED. A screen that comes up mirrored looks identical to one we
			// never reached, and telling them apart used to cost a whole run — these screens
			// are several menu levels deep, so a narrowing step is expensive. Printed once per
			// distinct reason so a screen the host simply does not own stays quiet after the
			// first time.
			// 31 IS "NOTHING IS UP", not a screen we declined. Reporting it printed
			// "screen 31 left MIRRORED — this screen is not in the owned set" for the whole time
			// the player was simply walking around, which reads as a menu we failed to own.
			if (pause is { } declined && declined.ScreenId != 0 &&
				declined.ScreenId != (uint)Engine_UiModel.InGameScreen.None)
			{
				// THREE DECLINES, NOT TWO. "No populated pool" was printed for a screen whose
				// presentation the guest had not built yet, which is a different failure with a
				// different fix — one is a pool we read too early, the other is a sample we
				// took too late, and the report is what tells them apart.
				// Key first, string only on a change. The modal mode rides in the key only on the
				// branch that prints it, so a mode changing behind a different reason cannot
				// re-fire the line.
				int reason = declined.ModalUp
					? 0
					: !ScreenOwnsList(declined.ScreenId)
						? 1
						: _followedScreen != declined.ScreenId
							? 2
							: 3;

				long note = ((long)declined.ScreenId << 40) | ((long)reason << 32) |
					(reason == 0 ? declined.ModalMode : 0u);
				if (_screenMenuDeclined != note)
				{
					_screenMenuDeclined = note;
					GD.Print($"[game-ui] screen {declined.ScreenId} left MIRRORED — " +
						(reason switch
						{
							0 => $"a modal is up (mode {declined.ModalMode})",
							1 => "this screen is not in the owned set",
							2 => "the guest has not built its presentation yet",
							_ => "no drawn presentation has a populated ExpandableMenu pool",
						}));
				}

				// THE LAG WINDOW: the screen is up but its list has not been sampled yet, so there
				// is no row to choose from. Only what needs no row goes through — B as the screen's
				// own back at root, and the tab cycle. A and the stick WAIT for the list: sent as
				// the options-page codes (28/29/36) they went to the guest's own cursor, and the
				// first press after opening a tab acted on a row the player never chose.
				if (IsRevealed && !declined.ModalUp && ScreenOwnsList(declined.ScreenId))
				{
					Fable2Input.ScreenMenuOwnsInput = true;
					if (Fable2Input.MenuBackPressed)
					{
						PostScreenButton(declined.ScreenId, Engine_UiScreenInput.Back);
					}
					else if (Fable2Input.MenuTabPrevPressed)
					{
						PostScreenButton(declined.ScreenId, Engine_UiScreenInput.TabPrev);
					}
					else if (Fable2Input.MenuTabNextPressed)
					{
						PostScreenButton(declined.ScreenId, Engine_UiScreenInput.TabNext);
					}

					return true;
				}

				// The one-off panels answer a different vocabulary and are reached only through the
				// GameAction chain, which is dead here: the query panel (10) closes on 37,
				// minigame-completed (11) and quest-completion (12) on 6 (`sub_823CDC08` /
				// `sub_823D3F98`). Each screen STATES which button does what — see
				// `Engine_UiScreenActions` — so this is one path rather than four branches.
				if (IsRevealed && !declined.ModalUp &&
					Engine_UiScreenActions.OwnsRevealedInput(declined.ScreenId))
				{
					Fable2Input.ScreenMenuOwnsInput = true;
					Engine_UiScreenActions.RunRevealed(
						declined.ScreenId, Fable2Input.MenuAcceptPressed, Fable2Input.MenuBackPressed);
					return true;
				}
			}

			return false;
		}

		_screenMenuDeclined = null;
		if (state.ScreenId != Engine_UiDetailPages.OptionsScreenId && Engine_UiHostOptionsPage.Open)
			Engine_UiOptionsPending.Leave();

		// The options page level is not a list and must not be drawn as one: the owned view comes
		// down, the guest's page stays mirrored, and input becomes the page's own vocabulary.
		if (ScreenPageOpen(state.ScreenId, address, drawn) || Engine_UiHostOptionsPage.Open)
		{
			bool hostPage = Engine_UiHostOptionsPage.Open;
			if (_screenMenuView is not null)
			{
				_screenMenuView.Visible = hostPage;
			}

			// The captions are the guest's while the page is open — every LEFT/RIGHT rewrites
			// `.grp.text` and A commits — so the seven pickbox values are dropped from the host
			// set and the slider labels kept. Must be set on this path too, or the committed
			// captions written on the list level stay up.
			// Any screen whose provider fills a page gets the same treatment; only a screen that
			// declared a page element reaches here at all, and only one that needs the sliders
			// pays for reading them.
			// THE PAGE'S ROW AND VALUES ARE THE GUEST'S, as its row widgets state them
			// (Engine_UiOptionsPending). The host paints the captions from them and steps
			// nothing of its own.
			if (!hostPage)
			{
				Engine_UiOptionsPending.Enter(_screenMenu?.Script.Highlighted?.Name ?? string.Empty);
			}

			if (Engine_UiDetailPages.Owns(state.ScreenId) &&
				_books.Book(state.ScreenId) is { } pageBook)
			{
				HostBridge.OptionSliders pageSliders =
					Engine_UiDetailPages.NeedsOptionSliders(state.ScreenId)
						? HostBridge.GetOptionSliders()
						: default;
				Engine_UiExpandableMenu.Item? pageRow = _screenMenu?.Script.Highlighted;
				pageBook.HostValues = Engine_UiDetailPages.Values(
					state.ScreenId, pageRow,
					default, default, new(), default, default, pageSliders,
					pageBook.Book?.Strings, optionsPageOpen: true);
				// The page's placements, switches and slides follow the same row while the
				// list is dormant — values alone left the picked page's contents where the
				// list level last put them.
				pageBook.HostPositions =
					Engine_UiDetailPages.Positions(state.ScreenId, pageSliders, pageRow);
				pageBook.HostActive = Engine_UiDetailPages.Active(state.ScreenId, pageRow);
				pageBook.HostOpacity = Engine_UiDetailPages.Opacities(state.ScreenId, pageRow);
				pageBook.HostRotations = Engine_UiDetailPages.Rotations(state.ScreenId, pageRow);
				pageBook.HostTints = Engine_UiDetailPages.Tints(state.ScreenId, pageRow);
				if (state.ScreenId == Engine_UiDetailPages.OptionsScreenId)
					Engine_UiHostOptionsPage.Apply(pageBook, pageRow);
				_providerFilledThisFrame = true;
			}

			Fable2Input.ScreenMenuOwnsInput = true;
			if (_books.Book(state.ScreenId) is { } optionsBook)
			{
				_screenLevel.Pointer(optionsBook, delta);
			}

			_screenLevel.Drive(SurfaceInput(delta));
			return true;
		}

		Engine_UiOptionsPending.Leave();

		// The spell-assignment blade is owned: the list comes down and Godot's widget takes the
		// input — up/down move the d-pad slot, left/right move the carousel AND COMMIT
		// (`sub_826B5330` writes as it moves), A copies the slot's will down into every later
		// slot that offers it, B closes the level through the guest's own mode byte. The guest
		// enters it off an A-press on a spell row (Lua UI event 23), so this branch only follows.
		bool blade =
			(Engine_UiTemplates.Screen(state.ScreenId)?.HasSpellBlade ?? false) &&
			AbilitiesBladeOpen();

		// `SetExpandableMenuDisabled`, as the guest states it — the blade raises it while its
		// carousel is up (`sub_826C2480`), the shop's confirm, the options page and the gift
		// adjuster too. `g_Menu:onUpdate` checks it BEFORE reading the stick, so a disabled menu
		// does not scroll. Carried on the model so the rule holds wherever it is driven from.
		_screenMenu?.Script.SetDisabled(_screenMenu.StatedInputLock);

		if (blade)
		{
			if (_screenMenuView is not null)
			{
				_screenMenuView.Visible = false;
			}

			// The blade page's switches still have to be stated: the early return below
			// starves the book's channels, so they froze at the pre-open state — the
			// spell-selector row hides every page, and the blade stood on bare paper. The
			// provider's blade arm (`AbilitiesAssignmentOpen`) answers Page4 alone, and this
			// is the only frame it can run on.
			if (Engine_UiDetailPages.Owns(state.ScreenId) &&
				_books.Book(state.ScreenId) is { } bladeBook)
			{
				bladeBook.HostActive = Engine_UiDetailPages.Active(
					state.ScreenId, _screenMenu?.Script.Highlighted);
				bladeBook.HostPositions = Engine_UiDetailPages.Positions(
					state.ScreenId, default, _screenMenu?.Script.Highlighted);
			}

			Fable2Input.ScreenMenuOwnsInput = true;
			if (_books.Book(state.ScreenId) is { } pointedBlade)
			{
				_spells.FollowBlade(pointedBlade);
				_spells.BladePointer(pointedBlade, delta);
			}

			_spells.DriveBlade(SurfaceInput(delta));
			return true;
		}

		using (Refii.Core.Engine_FramePhases.Measure("ui.list.view"))
		{
			AdoptScreenMenu(address, drawn, state.ScreenId);
		}

		// THE LIST FOLLOWS THE GAME'S DATA. The bridge's generation moves whenever a level,
		// experience, an item, gold or a drawn weapon changes (mod_events.cpp's entry points);
		// the open list re-reads its sources in place then — a purchase, a use or a sale shows at
		// once, as the guest's own handler restates its rows, instead of on the next visit.
		uint dataGeneration = HostBridge.UiDataGeneration();
		if (dataGeneration != _uiDataSeen)
		{
			_uiDataSeen = dataGeneration;
			if (_screenMenu?.Resource() == true)
			{
				_screenMenuView?.Refresh();
			}
		}

		// A ROW SEEN IS VIEWED. The guest's highlight ticks clear an item's or a quest's "!" on
		// the row their cursor stands on (`*(item+20) = 1` in `sub_829B5160`, `Quest+78 = 1` in
		// `sub_829AF638`/`sub_829AFE58`); that cursor no longer moves, so ours states it. The
		// pump writes the flag and moves the data generation, and the re-read above restates
		// the row, its header and (on the next pause build) the tab.
		if (_screenMenu?.Script.Highlighted is { Unviewed: true } seen)
		{
			uint id = ((uint)seen.IdHigh << 16) | ((uint)seen.IdLow & 0xFFFFu);
			if (id != 0u && id != _viewedPosted)
			{
				_viewedPosted = id;
				HostBridge.PostFrontEndAction(seen.EventType is 42 or 43
					? $"{Engine_UiQuestSource.QuestAction}viewed:0x{id:X8}"
					: $"{Engine_UiInventorySource.InventoryAction}viewed:{id}");
			}
		}

		// The detail page follows our cursor because the guest's cannot: screen 14 fills its
		// right-hand page on a highlight-change edge keyed off the GUEST's Lua highlight, which
		// stops moving once Godot owns the cursor — see Engine_UiAbilityPage.
		//
		// Set EVERY frame rather than on a move edge: `HostValues` is read in the book's normal
		// text pass, which already skips a field whose display has not changed.
		if (Engine_UiDetailPages.Owns(state.ScreenId) &&
			_books.Book(state.ScreenId) is { } abilityBook)
		{
			// The levels are the page's one live input: hero component 57 at
			// `stats + 0x110 + 4*id`, the guest's own `4 * (Type + 68)` at `0x82896ad0`.
			// Everything else on the page is shipped GDB and Babel.
			// The region request goes FIRST — the pump samples on its own thread and answers a
			// tick later, so naming the region now makes the next frame's answer this cursor's.
			Engine_UiDetailPages.Prepare(state.ScreenId, _screenMenu?.Script.Highlighted);

			// REFILLED ON A CHANGE, NOT EVERY DISPLAY FRAME: the guest's values move only on a
			// guest frame, and the page's other inputs are the cursor, the page, the data
			// generation and the fold. Six bridge reads and eight dictionary builds per frame
			// were a third of the UI's per-frame cost with a book open.
			(uint, string, int, uint, bool) detailKey = (
				state.ScreenId, _screenMenu?.Script.Highlighted?.Name ?? string.Empty,
				Fable2PageProviders.BookPage, HostBridge.UiDataGeneration(),
				_screenMenu is not null);
			bool refill = Engine_GuestFrame.Changed || detailKey != _detailKey ||
				!ReferenceEquals(_detailBook, abilityBook);
			_detailKey = detailKey;
			_detailBook = abilityBook;

			// Only the options screen draws them.
			HostBridge.OptionSliders sliders =
				refill && Engine_UiDetailPages.NeedsOptionSliders(state.ScreenId)
					? HostBridge.GetOptionSliders()
					: _detailSliders;
			_detailSliders = sliders;
			HostBridge.WorldMapRegion worldMap = HostBridge.GetWorldMapRegion();
			HostBridge.HeroVitals vitals = refill ? HostBridge.GetHeroVitals() : _detailVitals;
			HostBridge.HeroDerived hero = refill ? Fable2Bridge.GetHeroDerived() : _detailHero;
			_detailVitals = vitals;
			_detailHero = hero;
			if (refill)
			{
				abilityBook.HostValues = Engine_UiDetailPages.Values(
					state.ScreenId,
					_screenMenu?.Script.Highlighted,
					vitals,
					hero,
					Fable2Bridge.GetCompletedQuests(),
					Engine_UiCrimePage.Stated,
					worldMap,
					sliders,
					abilityBook.Book?.Strings);
				abilityBook.HostTextures =
					Engine_UiDetailPages.Textures(state.ScreenId, _screenMenu?.Script.Highlighted);
				abilityBook.HostPositions =
					Engine_UiDetailPages.Positions(state.ScreenId, sliders, _screenMenu?.Script.Highlighted, hero);
				abilityBook.HostActive =
					Engine_UiDetailPages.Active(state.ScreenId, _screenMenu?.Script.Highlighted);
				// The opacity claims ride here too: this path marks the frame filled, so the owned
				// screen driver that also sets them stands down, and without this line no item
				// book ever received one (the clothing footer, the shop's front-page parchment).
				abilityBook.HostOpacity =
					Engine_UiDetailPages.Opacities(state.ScreenId, _screenMenu?.Script.Highlighted);
				abilityBook.HostRotations =
					Engine_UiDetailPages.Rotations(state.ScreenId, _screenMenu?.Script.Highlighted);
				abilityBook.HostTints =
					Engine_UiDetailPages.Tints(state.ScreenId, _screenMenu?.Script.Highlighted);
				if (state.ScreenId == Engine_UiDetailPages.OptionsScreenId)
					Engine_UiHostOptionsPage.Apply(abilityBook, _screenMenu?.Script.Highlighted);
			}

			DriveMinimap(state.ScreenId, abilityBook, worldMap, _screenMenu?.Script.Highlighted, delta);

			// THIS PATH FILLS THE BOOK TOO. DriveOwnedScreenProvider stands down on this flag,
			// and it was only ever set by the page path above — so for the ability book it ran
			// afterwards with `default` vitals and blanked the experience it had just written.
			_providerFilledThisFrame = true;
			_providerFilledScreen = state.ScreenId;
			abilityBook.ScrollKey = _screenMenu?.Script.Highlighted?.Name ?? string.Empty;
			if (refill)
			{
				abilityBook.HostSlides = Engine_UiDetailPages.Slides(
					state.ScreenId, _screenMenu?.Script.Highlighted, worldMap,
					_screenMenu is not null, vitals, hero);
			}

			// THE ABILITY PREVIEW MOVIE, on the tarot frame. The decoded frame is a viewport
			// texture, registered once and painted through the book's own texture channel over
			// the still the provider put there.
			if (Video is { } video)
			{
				// THE HOST NAMES THE ABILITY MOVIE. The guest's own choice is frozen on the row
				// its highlight last reached, which is the first one — see
				// Engine_UiAbilityPage.MovieFor. Asked every frame; an unchanged name is free.
				string wanted = Engine_UiDetailPages.Movie(
					state.ScreenId, _screenMenu?.Script.Highlighted, vitals);

				// THE QUAD IS THE TAROT FRAME, where the still and the movie share one quad. The
				// guest's movie write (`sub_829A5A18`, sprite+564) stores only through the
				// element's node (`*(elem+4)`), which a headless element does not have.
				int quad = abilityBook.OrdinalOf(Engine_UiAbilityPage.TarotFrame) ?? -1;

				// A mod's ability shows its still card: no movie, and the last one taken down.
				bool noMovie = Engine_UiAbilityPage.WantsNoMovie(_screenMenu?.Script.Highlighted);
				if (noMovie)
				{
					wanted = string.Empty;
				}

				if (wanted.Length != 0 && quad >= 0)
				{
					// The slot is registered BEFORE the open: `MovieStarted` fires inside
					// `PlayEmbedded`, and the window's conceal handler reads `HasEmbeddedMovie`
					// there — registering after lost the race once and concealed the whole UI
					// viewport for the rest of the session ("a full-screen movie started over
					// it", with no front end ever coming to reveal it again).
					_movieSlots[address] = quad;
					_embeddedMovieAsked = true;
					video.PlayEmbedded(wanted, quad);
				}

				if (quad >= 0 && wanted.Length != 0)
				{
					video.EmbedOrdinal = quad;
					_movieSlots[address] = quad;
					if (video.FrameTexture is { } frame &&
						abilityBook.PathOfOrdinal(quad) is { } hosting)
					{
						var textures = new System.Collections.Generic.Dictionary<string, uint>();
						if (abilityBook.HostTextures is { } stated)
						{
							foreach (var pair in stated)
							{
								textures[pair.Key] = pair.Value;
							}
						}

						textures[hosting] = Engine_UiHostTextures.Register(frame);
						abilityBook.HostTextures = textures;
					}
				}
				else
				{
					if (_movieSlots.Remove(address) && !HasEmbeddedMovie)
					{
						video.EmbedOrdinal = -1;
					}

					// A row with no movie — a category — takes the preview down rather than
					// letting it run out as an orphan with no quad.
					video.StopEmbedded();
				}
			}
		}

		if (_screenMenuView is null || _screenMenu is null)
		{
			Fable2Input.ScreenMenuOwnsInput = false;
			return false;
		}

		// THE LIST'S SLIDE AND ITS FOCUS DIM, from the screen's own book: `Foreground.ExpandableMenu`'s
		// x as its playing slide tracks or the guest's told write put it, `Buttons`' opacity as
		// the guest told it (`sub_82426078`, the page or blade taking focus). A frame with no
		// stood x keeps the anchor.
		if (_books.Book(state.ScreenId) is { } listBook)
		{
			if (_screenMenuFollowOrdinal >= 0 &&
				listBook.StoodValue(_screenMenuFollowOrdinal, Engine_UiSlidePlayback.Channel.PositionX) is { } stoodX &&
				float.IsFinite(stoodX))
			{
				Vector3 at = _screenMenuView.Position;
				_screenMenuView.Position = new Vector3(
					_screenMenuAnchor.X + (stoodX - _screenMenuFollowAuthoredX) -
						Engine_UiTemplates.WideListShift(state.ScreenId),
					at.Y, at.Z);
			}

			if (_screenMenuButtonsOrdinal >= 0 &&
				listBook.StoodValue(_screenMenuButtonsOrdinal, Engine_UiSlidePlayback.Channel.Opacity) is { } focus)
			{
				_screenMenuView.FocusOpacity = Mathf.Clamp(focus / 100f, 0f, 1f);
			}
		}

		_screenMenuView.Visible = true;
		Fable2Input.ScreenMenuOwnsInput = true;
		Engine_UiModMenus.Focus(_screenMenuView, _screenMenu.PopulationScript, Engine_UiCommandList.Screen);

		// The cursor is Godot's: the guest's Lua menu, its cursor and its stick latch are cut.
		_screenMenuView.GuestCursor = false;

		// A MOD'S MENU opened over this list is the host's alone: the guest's pool does not carry
		// its rows, so the view is navigated directly and B returns to the screen's own list.
		if (_screenMenuView.OpenedDepth > 0)
		{
			DriveOpenedModMenu(_screenMenuView, _screenMenu.PopulationScript, delta);
			return true;
		}

		_screenMenuView.Navigate(Fable2Input.MenuDirection, delta);
		StateHighlightedRow();

		// Change-keyed row report: one line when the list settles, one more per press.
		ReportMenuView(_screenMenuView);

		// A MOD'S ROW ACTIONS on this list (ui/templates.json "rowActions"): a prompt beside the page
		// buttons, the button held from the guest, mod_menu_row on the press.
		if (DriveRowActions(state.ScreenId)) return true;

		// Y ON AN ABILITY IS UNLEARN. A mod's ability is the mod's refund; one of the guest's
		// fourteen is the guest's own unlearn called BY TYPE (Engine_UiAbilitySource.AskUnlearn),
		// the way A buys by type — never the guest's Y lever (code 39), whose confirm resolves the
		// ability from the guest menu's cached highlight and not from Godot's cursor.
		if (Fable2Input.MenuFlipPressed && _screenMenu is { } flipMenu &&
			flipMenu.Script.Highlighted is { } flipRow)
		{
			if (flipMenu.HighlightedIsModAbility)
			{
				if (Engine_UiModAbilities.TryGet(flipRow.Name, out Engine_UiModAbilities.Ability modAbility))
				{
					Fable2ModAbilityRefund.AskRefund(modAbility);
				}

				return true;
			}

			if (Engine_UiAbilitySource.AskUnlearn(flipRow.Name))
			{
				return true;
			}
		}

		// A SCREEN'S OWN FLIP ACTION (ui/templates.json "flipAction"): a mod's hook.
		if (Fable2Input.MenuFlipPressed &&
			Engine_UiTemplates.Screen(state.ScreenId) is { FlipAction.Length: > 0 } flipScreen)
		{
			Engine_UiScreenActions.Run(flipScreen.FlipAction, Engine_UiCommandList.Revealed);
			return true;
		}

		// THE RIGHT PAGE'S OTHER HALF. The item books author Page1 and Page2 on the same
		// right-hand leaf. The page is the HOST's — the template's `pages`, drawn by the owned
		// book — so the turn is a host state and nothing is sent to the guest: its own page
		// widget (`sub_82990E48`, codes 26/27) turns a presentation nobody draws, and the pump
		// drops those codes.
		//
		// Edge-detected, not held: `Fable2Input.MenuHorizontal` repeats while the stick is over,
		// and a page that turns every repeat is unusable.
		if (Engine_UiTemplates.Screen(state.ScreenId)?.Provider is
				Fable2PageProviders.ItemBook or Fable2PageProviders.Abilities or
				Fable2PageProviders.RealEstate)
		{
			int sideways = Fable2Input.MenuHorizontal;

			// A click on the page's own arrows turns it.
			if (sideways == 0 && _books.Book(state.ScreenId) is { } turnedBook &&
				PageArrowClicked(turnedBook) is var arrow && arrow != 0)
			{
				sideways = arrow;
				_screenPageHeld = 0;
			}

			if (sideways != 0 && sideways != _screenPageHeld)
			{
				Fable2PageProviders.BookPage = sideways > 0 ? 1 : 0;
				GD.Print($"[game-ui] screen {state.ScreenId}: page turned — now on Page" +
					$"{Fable2PageProviders.BookPage + 1}");
			}

			_screenPageHeld = sideways;
		}

		// The tab cycle through the screen's own handler, which keeps the guest's gates in the
		// path: an open submenu eats it, unavailable tabs are skipped. The shoulders are masked
		// from the guest under an owned list (`WithoutListInput`), so this is the one press.
		if (Fable2Input.MenuTabPrevPressed || Fable2Input.MenuTabNextPressed)
		{
			bool next = Fable2Input.MenuTabNextPressed;
			uint code = next ? Engine_UiScreenInput.TabNext : Engine_UiScreenInput.TabPrev;
			uint depth = PostScreenButton(state.ScreenId, code);
			GD.Print($"[game-ui] screen {state.ScreenId}: {(next ? "RB" : "LB")} forwarded as " +
				$"{Fable2PortedScreens.Lever(state.ScreenId, code, Engine_UiSource.Current.ActingPlayer) ?? $"ScreenAction({code})"} — queue depth {depth}");
			return true;
		}

		// The right-hand page first: the wheel over it scrolls it, its bar drags it. The list
		// takes only what lands on its own rows or bar, so the two sides never share a notch.
		PointerPage(_books.Book(state.ScreenId));
		SwallowOverPages(_books.Book(state.ScreenId));

		if (Fable2Input.MenuAcceptPressed || PointerList(_screenMenuView))
		{
			// A mod's row first: an `opens` menu, an Opens:/Mod:lua: command, or a row a mod
			// patched in (which raises its event and then commits like any other).
			if (_screenMenuView is { } modView && Engine_UiModMenus.Activated(modView,
					_screenMenu.PopulationScript, _screenMenu.Script.Highlighted, _screenMenu.Script.Highlighted?.Command,
					Engine_UiCommandList.Screen))
			{
				modView.Refresh();
				ReportMenuView(modView);
				return true;
			}

			// By IDENTITY where the row can be named, by A press where it cannot.
			// `ScreenAction(36)` posts `OnPressA` into the presentation's logic queue and the
			// screen asks Lua which row that meant, so it commits `CurrentHighlightIdx`, not our
			// cursor; naming the row pushes the same `GUI:PushButtonEvent` the shipped
			// `SelectMenuItem` would. Keep the fallback — a list populated before the spawn
			// capture was watching has labels with no identity, and inventing an
			// `(EventType, id)` pair activates a different row.
			//
			// Refresh on the PRESS: a host-owned fold changes the visible list immediately and
			// states nothing the follower would run on.
			bool held = Engine_UiDyeFlow.Active || Engine_UiAugmentFlow.Active;

			// The shared A arm (`sub_826D58F0`) plays the select cue below the root
			// (`SE_GUI_MENU_BOX_SELECT` 0x826D59B0, skipped when `IsHighlightedItemAtRoot`);
			// a commit by identity never runs that arm.
			bool belowRoot = _screenMenu?.Script.Highlighted is { Parent: { Parent: not null } };
			if (_screenMenu is { } pressed && pressed.Commit())
			{
				if (belowRoot)
				{
					Cue(frontEnd: false, "select");
				}

				if ((Engine_UiDyeFlow.Active || Engine_UiAugmentFlow.Active) != held)
				{
					pressed.FollowDyeFlow();
				}

				_screenMenuView?.Refresh();
				ReportMenuView(_screenMenuView);
			}
			else
			{
				uint depth = HostBridge.ScreenAction(Engine_UiScreenInput.Select);
				GD.Print($"[game-ui] screen {state.ScreenId}: A press forwarded as " +
					$"ScreenAction({Engine_UiScreenInput.Select}) — queue depth {depth}");
			}

			return true;
		}

		if (Fable2Input.MenuBackPressed)
		{
			// B collapses one level; at the root it goes back to the pause menu. One guest
			// decision, `sub_826D5AF0`, branching on `IsHighlightedItemAtRoot()`:
			//
			//   FALSE — `OnPressB` into the presentation's UIContract, where `CloseMenuItem`
			//           closes the highlighted row's PARENT and puts the cursor on it.
			//   TRUE  — `sub_826A2C88(screenMap[0], …)` = `SetScreen(mgr, 0)` (`826a2cc4 li r4,
			//           0`, `826a2cd0 bl sub_8236E630`, id landing at `8236eb20 stw r28, 4(r9)`)
			//           plus `sub_8298DF98` rebuilding the pause root's rows — screen id 0.
			//
			// The `31` that path also writes goes to `*(*(GUI+56)+152)`, the dynamic presentation
			// SLOT cache `sub_823DC9D8` early-outs on, not the screen id. Leaving the GUI is a
			// different pair (`sub_8236ED98` / `sub_82370178`) no book screen's B reaches.
			//
			// The fold is ours, so `Collapse()` while there is something to close and forward
			// once there is not. `sub_826C1D98` answers from the fold depth stated every frame
			// (StateListFoldDepth), so the forwarded press reads the fold the player is on.
			// ONE FOLD. A level the GUEST laid out (pool rows) closes through the guest — its
			// `sub_826C2560` case 37 folds it and re-lays the pool, which the model follows —
			// or two folds drift apart: ours closed, the guest's open, the highlight solved
			// against the guest's sixteen rows on our eight. Only a host-only level is ours to
			// collapse.
			if (_screenMenu is { } menu && menu.Collapse())
			{
				// The guest's own collapse below the root cancels (`SE_GUI_MENU_BOX_CANCEL`,
				// 0x826D5BBC); this one is ours.
				Cue(frontEnd: false, "cancel");
				_screenMenuView?.Refresh();
				GD.Print($"[game-ui] screen {state.ScreenId}: B collapsed one level — " +
					$"cursor {menu.Script.CurrentHighlightIdx}/{menu.Script.Visible.Count}");
				return true;
			}

			// B at the top of a held list (a dye's, an augment's) puts it away and returns to the
			// items; the guest's screen never left them.
			if (Engine_UiDyeFlow.Active || Engine_UiAugmentFlow.Active)
			{
				Engine_UiDyeFlow.End();
				Engine_UiAugmentFlow.End();
				_screenMenu?.FollowDyeFlow();
				_screenMenuView?.Refresh();
				return true;
			}

			// The queue depth separates a press the guest acted on from one still queued. A
			// CLIMBING depth means the drain is stuck inside an action, not that the pump was
			// never asked: `BeginDrain` swaps the whole queue out and `NoteRan` runs only after a
			// dispatch returns, so an action that never returns leaves the queue at 0 and the
			// next press starting again from 1.
			uint depth = PostScreenButton(state.ScreenId, Engine_UiScreenInput.Back);
			GD.Print($"[game-ui] screen {state.ScreenId}: B press forwarded as " +
				$"{Fable2PortedScreens.Lever(state.ScreenId, Engine_UiScreenInput.Back, 1) ?? $"ScreenAction({Engine_UiScreenInput.Back})"} at fold depth {_statedFold.Depth} — " +
				$"queue depth {depth}");
		}

		return true;
	}

	/// <summary>
	/// True while the abilities screen's spell-assignment blade is up — the guest's own mode byte
	/// (`screen14+620`), read through the bridge. Never key on element state: `abilitiesscreen.bgf`
	/// ships its `Adjuster` components authored ACTIVE. The mode byte has exactly two writers
	/// (enter `sub_826C2480`, exit `sub_826C25C0`) and IS the state.
	/// </summary>
	private static bool AbilitiesBladeOpen() => Fable2Bridge.AbilitiesAssignmentOpen();

	/// <summary>
	/// The presentation address the guest's screen manager holds for a screen id, or 0 when it
	/// states none — the only reliable join between a screen and its presentation. Never search
	/// the drawn set for the first tree that contains what you want: the previous screen's
	/// presentation is still drawn when the new one arrives.
	/// </summary>
	private uint PresentationFor(uint screenId)
	{
		if (HostBridge.GetInGameScreens()?.Owned is not { } owned)
		{
			return 0;
		}

		foreach ((uint address, int id) in owned)
		{
			if (id == (int)screenId)
			{
				return address;
			}
		}

		return 0;
	}

	/// <summary>
	/// The drawn presentation carrying an <c>ExpandableMenu</c> pool with rows in it. Found, not
	/// named — most of these screens live inside `pausemainmenu`, but the gift screen and the
	/// shop are their own. A pool with no filled button has not populated yet and is skipped.
	/// </summary>
	private (uint Address, Engine_UiDrawnPresentation Drawn)? FindListPresentation(uint screenId)
	{
		// Which presentation belongs to this screen is the guest's own statement: its screen
		// manager holds one per id. Fails OPEN — when the guest states nothing for this id the
		// first-match walk stands rather than leaving a screen with no list.
		uint stated = PresentationFor(screenId);

		foreach ((uint address, Engine_UiDrawnPresentation drawn) in _drawn)
		{
			if (drawn.Screen is null ||
				Engine_ExpandableMenuItems.FindGroup(drawn.Screen) is null)
			{
				continue;
			}

			if (stated != 0 && address != stated)
			{
				continue;
			}

			// The pause presentation owns screen 0's list and nothing else's: `pausemainmenu`
			// keeps its root entries in its pool even while a tab screen is up in another
			// presentation, and it is the first entry this walk reaches.
			if (screenId != 0 && string.Equals(drawn.Name, PausePresentation,
					System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (_screenLists.TryGetValue(address, out Engine_UiScreenMenu? list) && list.Count > 0)
			{
				return (address, drawn);
			}
		}

		return null;
	}

	/// <summary>
	/// Stands the owned view over one presentation's list and hides the guest's own rows.
	/// Rebuilt when the presentation changes — the view holds nodes cloned from that build.
	/// </summary>
	/// <summary>
	/// The in-game screen that is up, read the way the preview gate reads it — the pause
	/// state answers a sample before the manager's `Current` does. 0 when nothing is up.
	/// </summary>
	private uint UpScreenId()
	{
		if (HostBridge.GetPauseState() is { ScreenId: not 0 and not 31 } pause)
		{
			return pause.ScreenId;
		}

		return _inGameScreen is { } up && (uint)up != 0u && (uint)up != 31u ? (uint)up : 0u;
	}

	/// <summary>
	/// The `FadeOutTopItemEarly` / `FadeOutBottomItemEarly` statements the model holds for
	/// <paramref name="presentation"/>, or null when the guest stated no menu there.
	/// </summary>
	private (bool Top, bool Bottom)? StatedMenuEndFades(uint presentation) =>
		presentation != 0 && _menuModel.Menus.TryGetValue(presentation, out Engine_UiMenuModel.Menu? menu)
			? (menu.FadesTopEnd, menu.FadesBottomEnd)
			: null;

	private void AdoptScreenMenu(uint address, Engine_UiDrawnPresentation drawn, uint screenId)
	{
		if (_screenLists.TryGetValue(address, out Engine_UiScreenMenu? list))
		{
			_screenMenu = list;
		}

		if (_screenMenu is null)
		{
			return;
		}

		if (_screenMenuAddress == address && _screenMenuView is not null &&
			_screenMenuScreen == screenId)
		{
			// The pool's own plane — the cover overlaps the tabs by authored z; see the pause
			// view's placement note.
			_screenMenuView.Position = new Vector3(
				_screenMenuAnchor.X - Engine_UiTemplates.WideListShift(screenId), _screenMenuAnchor.Y,
				drawn.Node.Position.Z + ListLift(screenId));
			return;
		}

		Engine_UiTheme? book = _presentation!.PatternBookFor(drawn.Name);
		Node3D? pattern = book is null ? null : Engine_UiButtonPool.FindButtonPool(book);

		// THE ANCHOR IS THE SCREEN'S OWN. The slot table is authored in the Buttons group's
		// space and each screen parks that group where it wants; a constant put every row 25
		// units low on the pause screen and there is no reason to expect these to agree either.
		if (pattern is not null)
		{
			Vector3 origin = Engine_UiTheme.CanvasTransform(pattern).Origin;
			_screenMenuAnchor = origin;
		}

		// THE ROWS RIDE THE GUEST'S SLIDE. `Foreground.ExpandableMenu` is authored at x −400
		// and every book's root `Intro` runs it in from −800 (`Outro` back out) while the
		// guest still plays the presentation we draw from; `ExpandableMenu.LoseFocus` dims
		// `Buttons` to 10. The two ordinals are resolved once here and read per frame below.
		_screenMenuFollowOrdinal = -1;
		_screenMenuButtonsOrdinal = -1;
		if (drawn.Screen is { } screenDoc)
		{
			if (Engine_UiStatedPanel.FindByName(screenDoc.Roots, "ExpandableMenu") is { } menuNode)
			{
				_screenMenuFollowOrdinal = menuNode.Ordinal;
				_screenMenuFollowAuthoredX = menuNode.PositionX;
				if (Engine_UiStatedPanel.FindByName(new[] { menuNode }, "Buttons") is { } buttons)
				{
					_screenMenuButtonsOrdinal = buttons.Ordinal;
				}
			}
		}

		Transform3D? cursorPose = ScreenMenuCursorPose(book);
		_screenMenuPresentation = drawn.Name;

		_screenMenuView?.QueueFree();
		_screenMenuView = new Engine_FrontEndMenuView
		{
			Name = "ScreenMenu",
			Theme = book,
			RowTemplate = pattern is null
				? null
				: Engine_FrontEndMenuView.RowTemplateIn(pattern),
			CursorTemplate = book?.Art("MenuHighlight"),
			CursorPose = cursorPose,

			// The end-slot fades this presentation's populate stated; ScreenId is the template
			// fallback for a list with no stated menu — see Engine_FrontEndMenuView.FadesTopEnd.
			ScreenId = screenId,
			StatedEndFades = () => StatedMenuEndFades(address),

			// The star and pip states, read out of THIS screen's document — every screen
			// authors its own button pool, so the slides differ per file.
			Ratings = Engine_UiRowRating.Resolve(drawn.Screen),
			Visible = false,
		};

		// Parented to the HOST's menu layer, not the built tree: that tree is freed whenever the
		// guest changes the screen's state, and a view inside it would go with it.
		_presentation.MenuLayer.AddChild(_screenMenuView);
		_screenMenuView.Bind(_screenMenu.Menu);
		_screenMenuView.Position = new Vector3(
			_screenMenuAnchor.X - Engine_UiTemplates.WideListShift(screenId), _screenMenuAnchor.Y,
			// THE ANCHOR'S Z IS READ AND REPORTED BUT NOT YET APPLIED HERE. The pause view is
			// having the same correction proved on it first; these screens draw correctly today
			// and moving both planes in one change would leave no way to tell which one the
			// next run is showing. See `_pauseAnchor`.
			drawn.Node.Position.Z + ListLift(screenId));
		_screenMenuView.ClipRect = Engine_UiButtonPool.ListClip(drawn.Screen);

		_screenMenuAddress = address;
		_screenMenuScreen = screenId;

		GD.Print($"[game-ui] Godot owns screen {screenId}'s list in '{drawn.Name}' — " +
			$"{_screenMenu.Describe()}, row art " +
			$"{(_screenMenuView.RowTemplate is null ? "NOT sourced (text-only rows)" : "cloned from the pattern book")}, " +
			$"anchor ({_screenMenuAnchor.X}, {_screenMenuAnchor.Y}, z {_screenMenuAnchor.Z}), clip " +
			$"{(_screenMenuView.ClipRect is { } band ? $"y {band.Position.Y:0}..{band.End.Y:0}" : "NONE (pool unmasked)")}");
	}

	/// <summary>
	/// Every owned book — see <see cref="Engine_UiBookSet"/>. The host is set once the
	/// presentation host exists.
	/// </summary>
	private readonly Engine_UiBookSet _books = new();

	/// <summary>The multiplayer book — friends, requests, invitations and games.</summary>
	private readonly Engine_UiListBookScreen _multiplayer =
		new(Engine_UiMultiplayerPage.ScreenId, Engine_UiMultiplayerPage.MenuName, "MultiplayerMenu");

	/// <summary>The Active Mods book — the enabled mods, each one's page its description.</summary>
	private readonly Engine_UiListBookScreen _modsBook =
		new(Engine_UiMultiplayerPage.ModsScreenId, Engine_UiMultiplayerPage.ModsMenuName, "ModsMenu");

	/// <summary>The list book standing, if any; at most one is open at a time.</summary>
	private Engine_UiListBookScreen? OpenListBook =>
		_multiplayer.IsOpen ? _multiplayer : _modsBook.IsOpen ? _modsBook : null;

	/// <summary>
	/// The host page book — a host question asked on a page (<see cref="Engine_UiHostBoxPage"/>):
	/// the question's title and body in the scroll region, Continue and Back as the rows. Driven
	/// with the front end's host box (GameUiHostSurfaces), never through <see cref="OpenListBook"/>.
	/// </summary>
	private readonly Engine_UiListBookScreen _hostPageBook =
		new(Engine_UiHostBoxPage.ScreenId, Engine_UiHostBoxPage.MenuName, "HostBoxMenu");

	/// <summary>The rebuilt, Godot-authored HUD. There is no mirrored one.</summary>
	private Engine_HudView? _hud;

	/// <summary>
	/// Player two's HUD on split screen: the same view, sharing <see cref="_hud"/>'s values and
	/// element feed, holding player two's pieces only.
	/// </summary>
	private Engine_HudView? _hudTwo;

	/// <summary>The presentation the rebuilt HUD replaces. Never mirrored.</summary>
	private const string HudPresentation = "hud";

	private void EnsureRebuiltHud()
	{
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			return;
		}

		BuildRebuiltHud();
	}

	private void EnsureSplitScreenHud()
	{
		if (_hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
		{
			return;
		}

		if (_presentation is null || _hud is null || !GodotObject.IsInstanceValid(_hud) ||
			_hudScreen is not { } screen)
		{
			return;
		}

		_hudTwo = new Engine_HudView
		{
			Name = "HudPlayerTwo",
			Theme = _presentation.Theme,
			TopBoxWrapWidth = Engine_UiHudTopBox.WrapWidth,
			TopBoxHeightRange = Engine_UiHudTopBox.HeightRange,
			Screen = screen,
			Art = _hud.Art,
			ResolveTexture = _hud.ResolveTexture,
			Layout = _hud.Layout,
			// Player one's feed, with player two's own numbers stated over it where the guest's
			// one HUD states only the local player's (DrivePlayerTwoHud).
			Values = new Engine_HudValues { Under = _hud.Values },
			ReadsValues = false,
			Seat = Engine_UiSeats.Seat.PlayerTwo,
			Suppressed = true,
			Visible = false,
			ProcessMode = ProcessModeEnum.Disabled,
		};
		_presentation.AddChild(_hudTwo);
		_hudTwo.Build();
		if (_worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo))
		{
			_worldIconsTwo.Values = _hudTwo.Values;
		}
	}

	/// <summary>
	/// Stands the rebuilt HUD up. Unconditional — there is no mirrored HUD to fall back to.
	///
	/// THE LAYOUT IS DERIVED, NOT TYPED. `hud.bgf` binds 52 meters and 18 text fields and says
	/// per element which value it answers to and where the artist put it; the only thing this
	/// adds is the ANCHOR. Hand-listing seventy pieces would be a host-side copy that drifts
	/// against the banks, and one wrong name loses a piece silently.
	///
	/// Mounted under the PRESENTATION so it shares the canvas camera — which is what makes
	/// `Engine_HudLayout.CanvasExtent` the right frame of reference for its anchors.
	/// </summary>
	private void BuildRebuiltHud()
	{
		if (_presentation is null)
		{
			return;
		}

		Engine_UiScreen? screen = _presentation.DocumentFor(HudPresentation);
		if (screen is null)
		{
			GD.PushWarning($"[hud] '{HudPresentation}' is not in the banks; no HUD.");
			return;
		}

		_hudScreen = screen;
		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
		_hud = new Engine_HudView
		{
			Name = "Hud",
			Theme = _presentation.Theme,
			TopBoxWrapWidth = Engine_UiHudTopBox.WrapWidth,
			TopBoxHeightRange = Engine_UiHudTopBox.HeightRange,
			Screen = screen,
			Art = new Engine_HudArt { Shipped = _presentation.PatternBookFor(HudPresentation) },
			ResolveTexture = hash =>
				Engine_UiHostTextures.TryGet(hash) ?? (_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null),
			Layout = Engine_HudLayout.FromBindings(
				screen, bindings,
				HostAppliesWidescreen ? Engine_UiWidescreen.Read(screen.Document) : null),
		};

		Engine_UiMinimapMod.ResolveTexture = _hud.ResolveTexture;
		Engine_UiMinimapMod.PauseRoot = () => _presentation?.DocumentFor(PausePresentation);

		// Every later build re-derives from the files as they stand (G.23); this first one has
		// just been derived below.
		_hudLayoutFresh = true;
		_hud.Derive = DeriveHudLayout;
		_hud.Rebuilt += OnHudRebuilt;

		// THE STRING BOOK, so the Lua-driven half of the text channel can be read at all: that
		// route states a Babel TAG ID rather than a string, and without the book those names
		// simply never resolve. Same database every screen already uses.
		_hud.Values.Strings = _presentation.Strings;
		GameWhereabouts.Strings = _presentation.Strings;

		_presentation.AddChild(_hud);

		// Mod overrides, one file per piece id — see Engine_HudArt.LoadOverrides. Under
		// `user://` because it is the PLAYER's folder, outside the export and outside the
		// repository; nothing of Fable 2's is written there by us.
		var ids = new System.Collections.Generic.List<string>();
		foreach (Engine_HudLayout.Piece piece in _hud.Layout.Pieces)
		{
			ids.Add(piece.Id);
		}

		// THE TRANSLATED UI, WRITTEN OUT AS EDITABLE SCENES. One `.tscn` per chrome unit under
		// `user://ui/components/<screen>/`, beside the template snapshot — the pair is the
		// authoring set: instance a component in the editor, place it, name it in a template,
		// and it stands through the same code every shipped screen uses.
		//
		// Desktop exports once here, but most pattern books are NOT otherwise open yet. On
		// Android that authoring pass would build and retain thousands of unused UI nodes
		// before the world loads. Actual menu/HUD construction still requests its own book.
		Engine_UiComponentLibrary.Result? exported = Engine_UiComponentLibrary.ExportAtStartup(
			Engine_UiComponentLibrary.ShouldSkipStartupExport(Engine_Platform.IsAndroid),
			() => Engine_UiComponentLibrary.ExportAll(name => _presentation.PatternBookFor(name)));
		if (exported is { } result)
			GD.Print($"[ui-components] {result} -> {Engine_UiComponentLibrary.Root}");
		else
			GD.Print("[ui-components] Android: authoring export skipped; screen art loads on demand");

		// The mods' arrangement, then the player's, over the derived one — shipped < mods < the
		// player's file. Both only move pieces, so the banks stay the source of what a piece IS.
		DirAccess.MakeDirRecursiveAbsolute("user://hud");
		int modArranged = _hud.Layout.ApplyModLayout();
		// The platform's own arrangement (Android: clear of the touch controls), beneath the
		// player's file.
		Fable2HudLayout.ApplyPlatformDefaults(_hud.Layout);

		// The derived arrangement, written out for the player's layout tool and for modders: the
		// piece ids are the contract for BOTH override files. It is the arrangement BENEATH the
		// player's file — shipped, then the mods' layout in load order — so the tool's "back to
		// the place beneath" returns a piece there, not to where the player's own file put it.
		_hud.Layout.Save(HudLayoutSnapshotPath, Engine_HudLayout.SnapshotLayer);
		int arranged = _hud.Layout.ApplyOverrides(HudLayoutPath);

		int replaced = _hud.Art.LoadOverrides("user://hud", ids);
		_hud.Build();
		GD.Print($"[hud] {bindings.Describe()}; {replaced} piece(s) replaced from user://hud" +
			(modArranged == 0 ? string.Empty : $"; {modArranged} piece(s) moved by mods") +
			(arranged == 0 ? string.Empty : $"; {arranged} piece(s) moved by {HudLayoutPath}"));

		if (Engine_Views.Split)
		{
			EnsureSplitScreenHud();
		}

		ApplyHudWidgetCopies();

		// THE WORLD ICONS, beside the layout-driven pieces. Their positions are the guest's —
		// a projection of world space, restated every frame — so they are a reader, not a
		// layout entry. A step in front of the HUD pieces so a marker is never under a bar.
		_worldIcons = new Engine_HudWorldIcons
		{
			Name = "WorldIcons",
			Theme = _presentation.Theme,
			Art = _hud.Art.Shipped,
			Values = _hud.Values,
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		_worldIcons.Bind(screen);
		MountHudWidget(Engine_HudWidgets.WorldIcons, false, _worldIcons);

		// PLAYER TWO'S WORLD ICONS on split screen: the same pooled prompts, seated in their half,
		// each drawn when player two sees its thing (both players' in-range tests run,
		// coop_hero_hooks.cpp), with player two's own hold meter.
		_worldIconsTwo = new Engine_HudWorldIcons
		{
			Name = "WorldIconsTwo",
			Theme = _presentation.Theme,
			Art = _hud.Art.Shipped,
			Values = _hudTwo?.Values ?? _hud.Values,
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = _worldIcons.ResolveTexture,
			Visible = false,
			ProcessMode = ProcessModeEnum.Disabled,
		};
		_worldIconsTwo.Bind(screen);
		MountHudWidget(Engine_HudWidgets.WorldIcons, true, _worldIconsTwo);

		// THE WORLD-SPACE ICON CLASS the guest drew through its renderer — property signs,
		// income, opinions, quest markers. Same reader shape, projected by Godot's own camera.
		_worldSprites = new Engine_HudWorldSprites
		{
			Name = "WorldSprites",
			Theme = _presentation.Theme,
			Position = new Vector3(0f, 0f, 52f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		MountHudWidget(Engine_HudWidgets.WorldSprites, false, _worldSprites);

		// The same sprites through player two's camera, in player two's half, on split screen.
		_worldSpritesTwo = new Engine_HudWorldSprites
		{
			Name = "WorldSpritesPlayerTwo",
			Theme = _presentation.Theme,
			Position = new Vector3(0f, 0f, 52f),
			ResolveTexture = _worldSprites.ResolveTexture,
			SightView = 1,
			Visible = false,
			ProcessMode = ProcessModeEnum.Disabled,
		};
		MountHudWidget(Engine_HudWidgets.WorldSprites, true, _worldSpritesTwo);

		// THE AIM RETICULE, which the guest pushed as 2D sprites from a record, never as an
		// element — see Engine_HudReticule. In front of the world sprites.
		_reticule = new Engine_HudReticule
		{
			Name = "Reticule",
			Look = ReticuleLook,
			Position = new Vector3(0f, 0f, 54f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		MountHudWidget(Engine_HudWidgets.Reticule, false, _reticule);

		// Player two's petals in player two's half, on split screen.
		_reticuleTwo = new Engine_HudReticule
		{
			Name = "ReticulePlayerTwo",
			Look = ReticuleLook,
			Position = new Vector3(0f, 0f, 54f),
			ResolveTexture = _reticule.ResolveTexture,
			OnlyPlayer = 1,
			Visible = false,
			ProcessMode = ProcessModeEnum.Disabled,
		};
		MountHudWidget(Engine_HudWidgets.Reticule, true, _reticuleTwo);

		// TIME CONTROL'S DIAL, which the guest keeps as a sprite record on the hero's component
		// 118 and never as an element — see Engine_HudSlowTimeDial. Behind the reticule.
		_slowTimeDial = new Engine_HudSlowTimeDial
		{
			Name = "SlowTimeDial",
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		MountHudWidget(Engine_HudWidgets.SlowTimeDial, false, _slowTimeDial);

		// Player two's, in their half on split screen: their own component 118's record.
		_slowTimeDialTwo = new Engine_HudSlowTimeDial
		{
			Name = "SlowTimeDialTwo",
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = _slowTimeDial.ResolveTexture,
			Visible = false,
			ProcessMode = ProcessModeEnum.Disabled,
		};
		MountHudWidget(Engine_HudWidgets.SlowTimeDial, true, _slowTimeDialTwo);

		BuildAchievementToast();
	}

	/// <summary>
	/// SPLIT SCREEN, PER PLAYER. While the world is drawn through two views
	/// (<see cref="Engine_Views.Split"/>), each local player's UI stands in their own half: player
	/// one's HUD in their half (Engine_UiSeats: left or top), laid out as on a screen that size, and
	/// player two's view of the same HUD in theirs (right or bottom); the spells wheel in the half of whoever opened it; the menu
	/// input and masks on the pad of whoever owns the open menu. A shared screen and solo play take
	/// none of this — every seat is the whole window and the menu owner is not stated. The menus
	/// stand in the half of whoever opened them (<see cref="Engine_UiPresentationHost.MenuLayer"/>).
	/// </summary>
	private void SeatPlayerUi(Vector2 window)
	{
		bool split = Engine_Views.Split;
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.Seat = split ? Engine_UiSeats.Seat.PlayerOne : Engine_UiSeats.Seat.Whole;
			_hud.HidesPartner = CoopSession.Side != CoopSession.Role.Solo && !CoopSession.LocalCoop;
			_hud.LocalPlayerTwo = CoopSession.IsClient && !CoopSession.LocalCoop;
			if (split)
			{
				EnsureSplitScreenHud();
			}
		}

		if (_hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
		{
			_hudTwo.Window = window;
			bool suppressed = !split || _fullCutscene || _hudAddress == 0;
			_hudTwo.Suppressed = suppressed;
			ProcessModeEnum hudTwoMode = suppressed ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
			if (_hudTwo.ProcessMode != hudTwoMode)
			{
				_hudTwo.ProcessMode = hudTwoMode;
			}
		}

		_spells.Window = window;

		Fable2Input.PlayerTwoPad = CoopSession.LocalCoop ? CoopSession.LocalPadTwo : -1;
		// The owner's pad drives the host menu in all local co-op: on a shared screen player two's
		// pause is player two's too.
		Fable2Input.MenuOwner = CoopSession.LocalCoop ? StatedMenuOwner() : 0;

		// THE MENUS IN THE OPENER'S HALF. The book is laid out for the whole window; on split
		// screen it is drawn whole inside the half of the player who opened it, scaled to fit,
		// so the other player keeps their view of the world.
		if (_presentation is not null && GodotObject.IsInstanceValid(_presentation))
		{
			int owner = split ? Fable2Input.MenuOwner : 0;
			if (owner != 0)
			{
				Engine_UiSeats.Frame half = Engine_UiSeats.Of(window, Engine_UiSeats.For(owner, true));
				Vector2 full = Engine_HudLayout.CanvasExtent(window);
				Vector2 room = half.Extent * half.Scale;
				float fit = full.X > 0f && full.Y > 0f ? Mathf.Min(room.X / full.X, room.Y / full.Y) : 1f;
				_presentation.SeatMenus(fit, half.Centre);
			}
			else
			{
				_presentation.SeatMenus(1f, Vector2.Zero);
			}
		}
		if (Fable2Input.MenuOwner != _menuOwnerSaid)
		{
			_menuOwnerSaid = Fable2Input.MenuOwner;
			GD.Print(_menuOwnerSaid == 0
				? "[split-ui] no menu owner stated — host menus read pad 0, masks hold every pad"
				: $"[split-ui] the open menu is player {_menuOwnerSaid}'s — host menus read " +
					$"pad {(_menuOwnerSaid == 2 ? Fable2Input.PlayerTwoPad : 0)}, only that pad is masked");
		}
	}

	private int _menuOwnerSaid;

	/// <summary>
	/// The expression wheel's owner: the player its last open stated
	/// (<see cref="Fable2Bridge.GetExpressionWheelPlayer"/>), kept through the close so a closing
	/// wheel stays in its owner's half. 1 until a wheel has opened.
	/// </summary>
	private int _wheelOwner = 1;

	private int _wheelSectorTwo = -1;

	/// <summary>
	/// The wheel sector a stick points at, in the guest's numbering: dead zone |v|² < 0.1, then
	/// the nearest of eight directions, sector i at −45° + i·45° (0 = SE, counter-clockwise,
	/// Y up). −1 at rest.
	/// </summary>
	/// <remarks>
	/// MEASURED ON THE GUEST, not read off `sub_826AA060`'s constants: `QuickWheel:point:0` lights
	/// the south-east wedge (Dog), `:2` the north-east (Fun), `:6` the south-west (Turn Safety).
	/// The decode this replaced (3π/4 − iπ/4 against −Y: 0 = NE, clockwise) is the same compass
	/// mirrored top to bottom, so a pointer aimed at Fun selected Dog and one at Fun's neighbour
	/// toggled the safety. A pad hid it: the guest re-points its own cursor from the raw stick.
	/// </remarks>
	internal static int WheelSector(Vector2 stick)
	{
		if (stick.LengthSquared() < 0.1f)
		{
			return -1;
		}

		int best = -1;
		float bestDistance = float.MaxValue;
		for (int i = 0; i < 8; ++i)
		{
			float angle = (i - 1) * 0.7853982f;
			float dx = stick.X - Mathf.Cos(angle);
			float dy = stick.Y - Mathf.Sin(angle);
			float distance = (dx * dx) + (dy * dy);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = i;
			}
		}

		return best;
	}

	private int ExpressionWheelOwner()
	{
		int stated = Fable2Bridge.GetExpressionWheelPlayer();
		if (stated != 0)
		{
			_wheelOwner = stated == 2 ? 2 : 1;
		}

		return _wheelOwner;
	}

	/// <summary>
	/// Whether the pause standing is player two's: the online client's own, or local player two's
	/// — the guest opens player two's reduced pause root for whoever is player two (the henchman
	/// receiver's pause, `sub_826A2C88(…, 0)`) and states the owner (<see cref="Fable2Bridge.GetMenuOwner"/>).
	/// </summary>
	/// <summary>
	/// The co-op travel rule's notice: a region exit waits until both players are at it, and the
	/// one who walked in is told once per approach (coop_hero_hooks.cpp; Fable 3's
	/// TEXT_TUTORIAL_EXIT_LEVEL_BLOCKED_BY_PLAYER_DISTANCE, which Fable 2's text banks do not
	/// carry), for five seconds as Fable 3 shows it.
	/// </summary>
	private void TellExitRefused()
	{
		int refused = Fable2Bridge.TakeExitRefused();
		if (refused == 0)
		{
			return;
		}

		string text = !CoopSession.LocalCoop
			? "Your partner must be with you to travel."
			: (refused & 1) != 0
				? "Player two must be with you to travel."
				: "Player one must be with you to travel.";
		_boxes.InfoBoxEx(ExitRefusedBox, 5f, text);
	}

	private const string ExitRefusedBox = "RefiiCoopExitRefused";

	/// <summary>Refii's own notice boxes: no guest manager holds them, so the HUD box shows them.</summary>
	private static readonly System.Collections.Generic.HashSet<string> RefiiNoticeBoxes =
		new() { Engine_UiCoopNotices.BoxName, ExitRefusedBox, Engine_UiNotices.ModBoxName };

	/// <summary>A book button on an in-game screen: the spine lever on a ported screen
	/// (<see cref="Fable2PortedScreens.Lever"/>), else the screen's own input.</summary>
	private static uint PostScreenButton(uint screen, uint code) =>
		Fable2PortedScreens.Lever(screen, code, Engine_UiSource.Current.ActingPlayer) is { } lever
			? HostBridge.PostFrontEndAction(lever)
			: HostBridge.ScreenAction(code);

	/// <summary>The open pause and the screens it opened are player two's.</summary>
	internal static bool PauseIsPlayerTwos() =>
		CoopSession.IsClient || (CoopSession.LocalCoop && Fable2Bridge.GetMenuOwner() == 2);

	/// <summary>
	/// Which player owns the open menu, as the guest states it: 0 when no menu is open. The spells
	/// wheel's open flags are per player (`QM+36` one, `QM+37` two). The pause and every screen
	/// opened from it carry their owner in the guest (`sub_8236E400`, "player two owns the menu",
	/// sampled each guest tick: <see cref="Fable2Bridge.GetMenuOwner"/>), read while the guest says a
	/// pause screen is up — the root, or a screen opened from it.
	/// </summary>
	private int StatedMenuOwner()
	{
		// A consent question is the partner's to answer, on their pad and in their half.
		if (_consentOwner != 0)
		{
			return _consentOwner;
		}

		// A mod's menu box is its player's to answer, on their pad.
		if (Engine_UiModMenuBox.Active is { } modBox)
		{
			return modBox.Player;
		}

		// A box built for one player (the core co-op Lua's @Box.Seat): the showing box's player.
		int boxed = _boxes.SeatOf(_boxes.GuestMessageBox);
		if (boxed == 0 && Engine_UiDisplayBoxes.Showing is { } display)
		{
			boxed = _boxes.SeatOf(display.Address);
		}

		if (boxed != 0)
		{
			return boxed;
		}

		int wheel = _spells.WheelPlayer;
		if (wheel != 0)
		{
			return wheel;
		}

		return HostBridge.GetPauseState() is { } pause && (pause.RootActive || pause.ModalMode == 1)
			? Fable2Bridge.GetMenuOwner()
			: 0;
	}

	/// <summary>
	/// Hands player two's HUD the element feed player one's was just given: one `hud.bgf`
	/// presentation, one read, two views — resolved again with player two's own slides folded in
	/// whenever they state any (<see cref="ResolvePlayerTwo"/>).
	/// </summary>
	private void ShareHudFeed()
	{
		if (_hud is null || _hudTwo is null || !GodotObject.IsInstanceValid(_hudTwo))
		{
			return;
		}

		if (ResolvePlayerTwo())
		{
			_hudTwo.Resolve = _hudTwoResolve;
			_hudTwo.LiveSlides = _hudTwoSlideBuffer;
			_hudTwo.ElementActive = _hudTwoActive;
		}
		else
		{
			_hudTwo.Resolve = _hud.Resolve;
			_hudTwo.LiveSlides = SlidesForPlayerTwo(_hud.LiveSlides);
			_hudTwo.ElementActive = _hud.ElementActive;
			_hudTwo.ActiveInputs = _hud.ActiveInputs;
			_hudTwo.StatedOpacity = _hud.StatedOpacity;
		}

		_hudTwo.LiveTextures = TexturesForPlayerTwo(_hud.LiveTextures);
		_hudTwo.LiveTransform = _hud.LiveTransform;
		_hudTwo.LiveTexts = _hud.LiveTexts;
	}

	/// <summary>Player two's own element slides over the shared ones (ordinal → slide).</summary>
	private readonly System.Collections.Generic.Dictionary<int, int> _hudTwoSlides = new();

	private int[]? _hudTwoSlideBuffer;

	private System.Collections.Generic.IReadOnlyList<int>? SlidesForPlayerTwo(
		System.Collections.Generic.IReadOnlyList<int>? shared)
	{
		if (shared is null || (_hudTwoSlides.Count == 0 && _hudTwoQuerySlides.Count == 0 && _hudTwoDpadSlides.Count == 0 &&
			_hudTwoOverrideSlides.Count == 0))
		{
			return shared;
		}

		if (_hudTwoSlideBuffer is null || _hudTwoSlideBuffer.Length != shared.Count)
		{
			_hudTwoSlideBuffer = new int[shared.Count];
		}

		for (int i = 0; i < shared.Count; ++i)
		{
			_hudTwoSlideBuffer[i] = shared[i];
		}

		foreach ((int ordinal, int slide) in _hudTwoSlides)
		{
			if (ordinal >= 0 && ordinal < _hudTwoSlideBuffer.Length)
			{
				_hudTwoSlideBuffer[ordinal] = slide;
			}
		}

		foreach ((int ordinal, int slide) in _hudTwoQuerySlides)
		{
			if (ordinal >= 0 && ordinal < _hudTwoSlideBuffer.Length)
			{
				_hudTwoSlideBuffer[ordinal] = slide;
			}
		}

		foreach ((int ordinal, int slide) in _hudTwoDpadSlides)
		{
			if (ordinal >= 0 && ordinal < _hudTwoSlideBuffer.Length)
			{
				_hudTwoSlideBuffer[ordinal] = slide;
			}
		}

		foreach ((int ordinal, int slide) in _hudTwoOverrideSlides)
		{
			if (ordinal >= 0 && ordinal < _hudTwoSlideBuffer.Length)
			{
				_hudTwoSlideBuffer[ordinal] = slide;
			}
		}

		return _hudTwoSlideBuffer;
	}

	/// <summary>Player two's own element textures over the shared ones (ordinal → texture hash).</summary>
	private readonly System.Collections.Generic.Dictionary<int, uint> _hudTwoTextures = new();

	private uint[]? _hudTwoTextureBuffer;

	private uint[]? TexturesForPlayerTwo(uint[]? shared)
	{
		if (shared is null || _hudTwoTextures.Count == 0)
		{
			return shared;
		}

		if (_hudTwoTextureBuffer is null || _hudTwoTextureBuffer.Length != shared.Length)
		{
			_hudTwoTextureBuffer = new uint[shared.Length];
		}

		System.Array.Copy(shared, _hudTwoTextureBuffer, shared.Length);
		foreach ((int ordinal, uint texture) in _hudTwoTextures)
		{
			if (ordinal >= 0 && ordinal < _hudTwoTextureBuffer.Length)
			{
				_hudTwoTextureBuffer[ordinal] = texture;
			}
		}

		return _hudTwoTextureBuffer;
	}

	/// <summary>One guest word, or 0 when the address does not read.</summary>
	private static uint PeekWord(uint address)
	{
		System.Span<uint> word = stackalloc uint[1];
		return address != 0u && HostBridge.Peek(address, word) == 1 ? word[0] : 0u;
	}

	private static readonly Engine_HudValues.Handle[] ExperienceTexts =
	{
		Engine_HudValues.Handle.For("GenericExpText"), Engine_HudValues.Handle.For("StrengthExpText"),
		Engine_HudValues.Handle.For("SkillExpText"), Engine_HudValues.Handle.For("WillExpText"),
	};

	private readonly int[] _experienceShownTwo = new int[4];
	private bool _experienceUpTwo;
	private double _experienceLingerTwo;
	private double _experienceStepClock;
	private ulong _experienceClockUsec;

	/// <summary>
	/// PLAYER TWO'S EXPERIENCE COUNTER, in their half. The guest keeps one counter (HUD+0x68) for
	/// the local player, but decides visibility for BOTH heroes in `sub_82539F38` (authority and an
	/// expression of type 17); each hero's show setter states it (stated value 17, bit 1 hero two:
	/// experience_counter_stated.cpp). Its numbers are each pool's spendable balance — component
	/// 164's `+16` points at float[4] (`lwz r11, 0x10(r29); lfsx f0, r31, r11`, 0x8253944c) —
	/// snapped when the counter appears (`sub_82539318`, for the local hero only, so player two's
	/// balances are read from their component) and stepped toward the balance once per guest
	/// frame (`sub_82539648`: +1 within ten, else up to nineteen); it lingers five seconds.
	/// </summary>
	private void DrivePlayerTwoExperience()
	{
		ulong now = Time.GetTicksUsec();
		double delta = _experienceClockUsec == 0ul ? 0.0 : System.Math.Min((now - _experienceClockUsec) * 1e-6, 0.25);
		_experienceClockUsec = now;

		var overlay = _hudTwo?.Values;
		bool split = Engine_Views.Split && overlay is not null && overlay.Under is not null;
		uint hero = split && CoopSession.LocalCoop && Fable2Bridge.CoopSessionRaw() is { } coop ? coop.PlayerTwo : 0u;
		uint pools = hero != 0u ? HostBridge.EntityComponentAddress(hero, 164u) : 0u;
		uint balances = pools != 0u ? PeekWord(pools + 16u) : 0u;
		if (balances < 0x10000u || _hud?.Bindings is not { } bindings)
		{
			foreach (Engine_HudValues.Handle handle in ExperienceTexts)
			{
				overlay?.Override(handle, null);
			}

			_hudTwoSlides.Clear();
			_experienceUpTwo = false;
			return;
		}

		bool visible = (HostBridge.Stated(HostBridge.StatedValue.ExperienceShown) & 2u) != 0u;
		System.Span<uint> balance = stackalloc uint[4];
		bool read = HostBridge.Peek(balances, balance) == 4;

		if (visible && !_experienceUpTwo && read)
		{
			for (int i = 0; i < 4; ++i)
			{
				_experienceShownTwo[i] = (int)System.BitConverter.Int32BitsToSingle(unchecked((int)balance[i]));
			}
		}

		_experienceUpTwo = visible;
		_experienceLingerTwo = visible ? 5.0 : System.Math.Max(0.0, _experienceLingerTwo - delta);
		bool shown = visible || _experienceLingerTwo > 0.0;

		// The guest's own step, once per guest frame (30 a second).
		_experienceStepClock += delta;
		while (read && _experienceStepClock >= 1.0 / 30.0)
		{
			_experienceStepClock -= 1.0 / 30.0;
			for (int i = 0; i < 4; ++i)
			{
				int target = (int)System.BitConverter.Int32BitsToSingle(unchecked((int)balance[i]));
				int diff = target - _experienceShownTwo[i];
				if (diff > 0)
				{
					_experienceShownTwo[i] += diff <= 10 ? 1 : System.Math.Min(diff, 19);
				}
			}
		}

		for (int i = 0; i < 4; ++i)
		{
			overlay!.Override(ExperienceTexts[i], ExperienceText(_experienceShownTwo[i]));
		}

		// SlideFader2 `ExpAmounts`: 2 in, 3 out fast.
		foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader2, Refii.Core.Engine_GameHash.Fnv1("ExpAmounts")))
		{
			_hudTwoSlides[ordinal] = shown ? 2 : 3;
		}
	}

	/// <summary>An experience pool's value as the counter writes it (`sub_8253A028`).</summary>
	private static string ExperienceText(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Where the edited arrangement lives — the editor writes it, every boot reads it.</summary>
	public const string HudLayoutPath = Engine_HudLayout.LayoutPath;

	/// <summary>The derived layout as it ran, written every boot for the editor and modders.</summary>
	public const string HudLayoutSnapshotPath = Engine_HudLayout.SnapshotPath;

	private Engine_HudWorldIcons? _worldIcons;

	/// <summary>Player two's copy of the pooled world icons, in their half while split.</summary>
	private Engine_HudWorldIcons? _worldIconsTwo;

	/// <summary>Per half, the pooled slots' canvas positions this frame (index 1: player two's seat).</summary>
	private System.Collections.Generic.Dictionary<int, Vector2>? _pooledCanvasTwo;
	private Engine_HudWorldSprites? _worldSprites;

	/// <summary>Player two's copy of the world sprites, drawn only while the screen is split.</summary>
	private Engine_HudWorldSprites? _worldSpritesTwo;

	private Engine_HudReticule? _reticule;

	/// <summary>sub_82179778: four petals, r = (1 − m)·18 + 8, s = (1 − t)·12 + 12; sub_822939B0's phases 0 / π/4
	/// (flt_82099484 / flt_82100E60); the ±0.8 clamp; mode 0 targeta, 1–2 targetb, above that target.</summary>
	private static readonly Engine_HudReticule.Style ReticuleLook = new(4, 18f, 8f, 12f, 12f,
		new[] { 0f, Mathf.Pi / 4f }, 0.8f,
		mode => mode == 0 ? "art\\gui\\persistant\\targeta.tex" : mode <= 2 ? "art\\gui\\persistant\\targetb.tex" : "art\\gui\\persistant\\target.tex");

	/// <summary>Player two's reticule layer, drawn only while the screen is split.</summary>
	private Engine_HudReticule? _reticuleTwo;

	private Engine_HudSlowTimeDial? _slowTimeDial;
	private Engine_HudSlowTimeDial? _slowTimeDialTwo;

	/// <summary>`GUI.PlayFullCutscene`'s game-flow state (`sub_82305FE8` builds it; its type
	/// slot is `li r3,7`).</summary>
	private const uint FullCutsceneStateVtable = 0x820AED30u;

	/// <summary>The guest's letterbox bars: rows 0..100 and H−100..H of its 720-line backbuffer
	/// (`sub_82296368` 0x822964a4), stated here as a fraction of the height.</summary>
	private const float LetterboxFraction = 100f / 720f;

	private CanvasLayer? _letterbox;

	private Engine_UiCreditsRoll? _creditsRoll;

	private readonly System.Collections.Generic.List<Engine_UiCreditsRoll.Line> _creditLines = new();

	private const uint CreditsScreenId = 29;

	private ulong _creditsOpenedMs;

	private readonly System.Collections.Generic.List<string> _credits = new();

	private readonly System.Collections.Generic.List<UiNode> _creditSlots = new();

	/// <summary>`CreditsEnd` was posted for this roll.</summary>
	private bool _creditsEndPosted;

	/// <summary>Screen 29 was up last frame.</summary>
	private bool _creditsWasUp;

	/// <summary>
	/// `GUI.ShowCreditsScreen` (TU1 `sub_82307488`, no arguments): SetScreen 29 and
	/// EnterGuiModeQuiet, whose entry of screen 29 (0x82370168) is the roll's start
	/// (`sub_826B79D8`: text group `GUI_CREDITS` (`dword_8349AF04`), 24 rows parked). The call is
	/// the roll's clock.
	/// </summary>
	private void StartCreditsRoll(Engine_UiModel.GuiNativeCall call)
	{
		_creditsOpenedMs = Time.GetTicksMsec();
		_creditsEndPosted = false;
		_credits.Clear();
		_creditSlots.Clear();
		GD.Print("[game-ui] credits: GUI.ShowCreditsScreen states the roll's start");
	}

	/// <summary>
	/// The end credits — screen 29 up — rolled by the guest's rule from the `GUI_CREDITS` group
	/// (<see cref="Engine_UiCreditsRoll.Roll"/>), on the clock `GUI.ShowCreditsScreen` started.
	/// The screen leaving (the guest's own close at the last line, `sub_826B7D00` 0x826b7e3c, or a
	/// skip) ends it.
	/// </summary>
	private void DriveCreditsRoll()
	{
		_creditLines.Clear();
		bool up = HostBridge.IsInGame && HostBridge.GetPauseState() is { ScreenId: CreditsScreenId };
		if (!up && _creditsWasUp)
		{
			_creditsOpenedMs = 0;
			_creditsEndPosted = false;
		}

		_creditsWasUp = up;
		if (up && _creditsOpenedMs != 0 && _presentation?.Strings is { } strings &&
			_presentation.DocumentFor(Engine_UiCreditsRoll.Presentation) is { } document)
		{
			if (_creditSlots.Count == 0)
			{
				_credits.Clear();
				foreach (uint key in strings.GroupMembers("GUI_CREDITS"))
				{
					_credits.Add(strings.TryGetString(key, out string line) ? line : string.Empty);
				}

				foreach (UiNode node in document.Nodes)
				{
					if (node.Type == BgfElementType.Text)
					{
						_creditSlots.Add(node);
					}
				}
			}

			double seconds = (Time.GetTicksMsec() - _creditsOpenedMs) / 1000.0;
			_creditLines.AddRange(Engine_UiCreditsRoll.Roll(seconds, _credits, _creditSlots));

			// The roll's end is the screen's close: the ported update no longer quits it.
			if (!_creditsEndPosted && _credits.Count > 0 && seconds >= Engine_UiCreditsTiming.EndSeconds(_credits.Count))
			{
				_creditsEndPosted = true;
				HostBridge.PostFrontEndAction("CreditsEnd");
				GD.Print($"[game-ui] credits: the roll ended at {seconds:F1} s ({_credits.Count} lines) — CreditsEnd");
			}
		}

		if (_creditsRoll is null)
		{
			if (_creditLines.Count == 0 || _presentation?.Fonts is not { } fonts)
			{
				return;
			}

			// Above the video (layer 10): the roll plays over `Credits.bik`.
			var layer = new CanvasLayer { Name = "Credits", Layer = 11 };
			_creditsRoll = new Engine_UiCreditsRoll { Name = "CreditsRoll", Fonts = fonts };
			layer.AddChild(_creditsRoll);
			AddChild(layer);
		}

		_creditsRoll.Show(_creditLines);
	}

	private bool _fullCutscene;

	/// <summary>True while the top game-flow command is the full cutscene, as the stack's own
	/// push and pop state it.</summary>
	private static bool FullCutscenePlaying() =>
		HostBridge.GuestAddressToTu1(HostBridge.Stated(HostBridge.StatedValue.FlowTopVtable)) == FullCutsceneStateVtable;

	/// <summary>
	/// THE FULL CUTSCENE REPLACES THE WHOLE GUI. `sub_82296368` tests the top game-flow state's
	/// type and, for 7, draws two opaque black bars INSTEAD of the GUI — no HUD, no markers, no
	/// reticule. The in-game layers go down with it and come back on the falling edge.
	/// </summary>
	private void DriveFullCutscene()
	{
		bool up = HostBridge.IsInGame && FullCutscenePlaying();
		if (up == _fullCutscene)
		{
			return;
		}

		_fullCutscene = up;
		GD.Print($"[game-ui] full cutscene {(up ? "UP — letterbox, in-game UI hidden" : "down")}");

		if (_letterbox is null)
		{
			_letterbox = new CanvasLayer { Name = "Letterbox", Layer = 4 };
			foreach ((float top, float bottom) in new[] { (0f, LetterboxFraction), (1f - LetterboxFraction, 1f) })
			{
				_letterbox.AddChild(new ColorRect
				{
					Color = Colors.Black,
					AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = top, AnchorBottom = bottom,
					MouseFilter = Control.MouseFilterEnum.Ignore,
				});
			}

			AddChild(_letterbox);
		}

		_letterbox.Visible = up;
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.Suppressed = up;
		}

		foreach (Node3D? layer in new Node3D?[] { _hud, _worldIcons, _worldSprites, _reticule })
		{
			if (layer is not null && GodotObject.IsInstanceValid(layer))
			{
				layer.Visible = !up && (layer != _hud || _hudAddress != 0);
			}
		}
	}

	/// <summary>The aiming object each player's reticule setter last stated (`@Reticule`,
	/// hooks/reticule_hooks.cpp); 0 before any. Its `+0x90` is the aim point.</summary>
	private readonly uint[] _reticuleObject = new uint[2];
	private readonly uint[] _lastReticuleObject = new uint[2];

	private uint _cachedReticuleHud;
	private uint _cachedReticuleHudForHeight;
	private uint _lastReticuleMouseSeq;
	private float _cachedReticuleGuestHeight = 720f;
	private readonly bool[] _cachedReticuleActive = new bool[2];
	private readonly Vector3[] _cachedReticuleAnchor = new Vector3[2];
	private readonly uint?[] _cachedReticuleMouseSeq = new uint?[2];
	private readonly float[] _cachedReticuleProgress = new float[2];
	private readonly Color[] _cachedReticuleColour = new Color[2];
	private readonly uint[] _cachedReticuleMode = new uint[2];
	private readonly int[] _cachedReticuleCounter = new int[2];

	/// <summary>
	/// Feeds the reticule each frame: the record at `*(*(GUI+32)+72)` (+64 for player two) for
	/// active, progress, colour, mode and the intro counter, and the stated object's `+0x90` for
	/// the aim point. Runs after <see cref="DriveWorldSprites"/>, which captured the camera.
	/// </summary>
	private void DriveReticule(bool guestFrame = true)
	{
		if (_reticule is null || !GodotObject.IsInstanceValid(_reticule))
		{
			return;
		}

		bool split = Engine_Views.Split;
		uint currentMouseSeq = Refii.Game.GameWorld.MouseLookSequence;
		bool needPoll = guestFrame ||
			currentMouseSeq != _lastReticuleMouseSeq ||
			_reticuleObject[0] != _lastReticuleObject[0] ||
			(split && _reticuleObject[1] != _lastReticuleObject[1]);

		if (!needPoll && !_cachedReticuleActive[0] && (!split || !_cachedReticuleActive[1]))
		{
			return;
		}

		if (needPoll)
		{
			System.Span<uint> root = stackalloc uint[9];
			_cachedReticuleHud = HostBridge.IsInGame &&
				HostBridge.TryPeekWord(HostBridge.GuestAddress(0x83496B08u), out uint gui) &&
				HostBridge.Peek(gui, root) == 9 &&
				HostBridge.GuestAddressToTu1(root[0]) == 0x82001610u &&
				HostBridge.TryPeekWord(root[8] + 72u, out uint hudAddr)
				? hudAddr
				: 0u;
			if (_cachedReticuleHud != 0u &&
				(_cachedReticuleHud != _cachedReticuleHudForHeight || _cachedReticuleGuestHeight <= 0f))
			{
				// The guest's backbuffer, `qword_834A5FE8` — the pixels its petal sizes are stated in.
				System.Span<uint> backbuffer = stackalloc uint[2];
				int bbCount = HostBridge.Peek(HostBridge.GuestAddress(0x834A5FE8u), backbuffer);
				_cachedReticuleGuestHeight = bbCount == 2 && backbuffer[1] != 0u ? backbuffer[1] : 720f;
				_cachedReticuleHudForHeight = _cachedReticuleHud;
			}
			_lastReticuleMouseSeq = currentMouseSeq;
		}

		uint hud = _cachedReticuleHud;
		if (hud == 0u || _spriteWindow.X <= 0f || _spriteWindow.Y <= 0f)
		{
			_cachedReticuleActive[0] = false;
			_cachedReticuleActive[1] = false;
			_lastReticuleObject[0] = 0u;
			_lastReticuleObject[1] = 0u;
			_cachedReticuleMouseSeq[0] = null;
			_cachedReticuleMouseSeq[1] = null;
			_reticule.Feed(0, null);
			_reticule.Feed(1, null);
			_reticuleTwo?.Feed(0, null);
			_reticuleTwo?.Feed(1, null);
			return;
		}

		float guestHeight = _cachedReticuleGuestHeight;
		// Each layer in its own seat: player one's (the whole window's) and, while split, player
		// two's. The petals clamp inside their own half, and an aim outside it is clamped rather
		// than dropped — so the projection is unclipped.
		Engine_UiSeats.Frame seat = _spriteViews[0].Frame;
		_reticule.CanvasHalfExtent = seat.Extent * 0.5f;
		_reticule.UnitsPerGuestPixel = seat.UnitsPerGuestPixel(guestHeight);
		_reticule.Place ??= guest => PlaceIn(_spriteViews[0], guest, 0u, clip: false)?.Canvas;
		if (split && _reticuleTwo is not null && GodotObject.IsInstanceValid(_reticuleTwo))
		{
			Engine_UiSeats.Frame theirs = _spriteViews[1].Frame;
			_reticuleTwo.CanvasHalfExtent = theirs.Extent * 0.5f;
			_reticuleTwo.UnitsPerGuestPixel = theirs.UnitsPerGuestPixel(guestHeight);
			_reticuleTwo.Place ??= guest => PlaceIn(_spriteViews[1], guest, 0u, clip: false)?.Canvas;
		}

		if (!split && _cachedReticuleActive[1])
		{
			_cachedReticuleActive[1] = false;
			_lastReticuleObject[1] = 0u;
			_cachedReticuleMouseSeq[1] = null;
			_reticule.Feed(1, null);
			_reticuleTwo?.Feed(1, null);
		}

		static float F(uint word) => System.BitConverter.Int32BitsToSingle(unchecked((int)word));

		System.Span<uint> record = stackalloc uint[14];
		System.Span<uint> anchor = stackalloc uint[3];
		uint cameraTick = Refii.Core.Engine_TickClock.GuestGameTick;
		bool stepComplete = Refii.Core.Engine_GuestFrame.Changed;
		int playerCount = split ? 2 : 1;
		for (int player = 0; player < playerCount; ++player)
		{
			uint aimObj = _reticuleObject[player];
			if (needPoll)
			{
				bool bracketMouseSeq = aimObj != 0u &&
					!Refii.Game.GameWorld.IsHostAiming(aimObj) &&
					aimObj == Refii.Game.GameWorld.MouseLookAim;

				int recCount = HostBridge.Peek(hud + (64u * (uint)player), record);
				uint? sequenceBefore = bracketMouseSeq ? HostBridge.GetMouseLook()?.Sequence : null;
				int ancCount = aimObj != 0u
					? HostBridge.Peek(aimObj + 0x90u, anchor)
					: 0;
				uint? sequenceAfter = bracketMouseSeq ? HostBridge.GetMouseLook()?.Sequence : null;
				if (recCount != 14 || (record[0] >> 24) == 0u || ancCount != 3)
				{
					_cachedReticuleActive[player] = false;
					_lastReticuleObject[player] = 0u;
					_cachedReticuleMouseSeq[player] = null;
					_reticule.Feed(player, null);
					_reticuleTwo?.Feed(player, null);
					continue;
				}

				_cachedReticuleActive[player] = true;
				_cachedReticuleProgress[player] = F(record[3]);
				_cachedReticuleColour[player] = new Color(F(record[4]), F(record[5]), F(record[6]), F(record[7]));
				_cachedReticuleMode[player] = record[8];
				_cachedReticuleCounter[player] = unchecked((int)record[13]);
				_cachedReticuleAnchor[player] = new Vector3(F(anchor[0]), F(anchor[1]), F(anchor[2]));
				_cachedReticuleMouseSeq[player] =
					sequenceBefore is { } seq && seq == sequenceAfter ? seq : null;
				_lastReticuleObject[player] = aimObj;
			}
			else if (!_cachedReticuleActive[player])
			{
				continue;
			}

			var aim = new Engine_HudReticule.Aim(
				_cachedReticuleAnchor[player],
				_cachedReticuleProgress[player],
				_cachedReticuleColour[player],
				_cachedReticuleMode[player],
				_cachedReticuleCounter[player],
				Screen: null,
				Object: aimObj,
				Tick: cameraTick,
				StepComplete: stepComplete,
				MouseSequence: _cachedReticuleMouseSeq[player]);
			_reticule.Feed(player, aim);
			_reticuleTwo?.Feed(player, aim);
		}
	}

	/// <summary>
	/// Feeds Time Control's dial from the guest's own draw of it (`@SlowTime.Draw`, the dial record
	/// as `sub_8260BA60` was given it; slow_time_dial_stated.cpp) until its close states it down
	/// (`@SlowTime.End`). A dial is its hero's: the record is `comp118 + 484` (`sub_822B5150`).
	/// </summary>
	private void DriveSlowTimeDial()
	{
		// Local co-op: each dial is its own player's (the local hero follows the menu owner,
		// which is no answer for whose dial this is). Online: the local hero's.
		uint one = 0u, two = 0u;
		if (HostBridge.IsInGame)
		{
			Fable2Bridge.CoopSessionWords? coop = CoopSession.LocalCoop ? Fable2Bridge.CoopSessionRaw() : null;
			one = coop is { } words ? words.PlayerOne : HostBridge.HeroEntityAddress();
			two = coop is { } both && Engine_Views.Split ? both.PlayerTwo : 0u;
		}
		else
		{
			_statedSlowTimeDials.Clear();
		}

		DriveSlowTimeDial(_slowTimeDial, one, 0);
		DriveSlowTimeDial(_slowTimeDialTwo, two, 1);
	}

	/// <summary>The dials stated drawn and not yet closed, by record address: alpha bits, then
	/// per segment handle, top, left, bottom, right, texture, angle bits, velocity bits.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, uint[]> _statedSlowTimeDials = new();

	private void DriveSlowTimeDial(Engine_HudSlowTimeDial? view, uint hero, int seat)
	{
		if (view is null || !GodotObject.IsInstanceValid(view))
		{
			return;
		}

		uint spells = hero != 0u && _statedSlowTimeDials.Count != 0 ? HostBridge.EntityComponentAddress(hero, 118u) : 0u;
		if (spells == 0u || _spriteWindow.X <= 0f || _spriteWindow.Y <= 0f ||
			!_statedSlowTimeDials.TryGetValue(spells + 484u, out uint[]? dial))
		{
			view.Feed(null);
			return;
		}

		static float F(uint word) => System.BitConverter.Int32BitsToSingle(unchecked((int)word));

		// `sub_8260B708` builds both rects as squares about the backbuffer's centre (0x8260b7e4..
		// 0x8260b818: `h/2 -+ half`, `w/2 -+ half`), so a rect's top + bottom is the guest's height
		// and its own centre the screen's.
		float guestHeight = 720f;
		Engine_HudSlowTimeDial.Segment? Segment(uint[] d, int i)
		{
			int at = 1 + (8 * i);
			if (d[at] == 0xFFFFFFFFu)
			{
				return null;
			}

			float top = unchecked((int)d[at + 1]);
			float left = unchecked((int)d[at + 2]);
			float bottom = unchecked((int)d[at + 3]);
			float right = unchecked((int)d[at + 4]);
			if (top + bottom > 0f)
			{
				guestHeight = top + bottom;
			}

			return new Engine_HudSlowTimeDial.Segment(
				Vector2.Zero,
				(right - left) * 0.5f,
				d[at + 5],
				F(d[at + 6]),
				F(d[at + 7]));
		}

		Engine_HudSlowTimeDial.Segment? first = Segment(dial, 0);
		Engine_HudSlowTimeDial.Segment? second = Segment(dial, 1);
		view.UnitsPerGuestPixel = _spriteViews[seat].Frame.UnitsPerGuestPixel(guestHeight);
		view.Feed(new Engine_HudSlowTimeDial.Dial(F(dial[0]), first, second));
	}

	private bool _worldSpritesCleared;
	private System.Collections.Generic.List<HostBridge.WorldSprite>? _lastRawWorldSprites;

	/// <summary>This frame's projected canvas point per pooled icon slot.</summary>
	private System.Collections.Generic.Dictionary<int, Vector2>? _pooledCanvas;

	/// <summary>
	/// Feeds the world-space icons the guest states (RefiiHostGetWorldSprites: each icon's Init,
	/// Updates and destructor, world_icon_hooks.cpp), projected through the world
	/// camera onto the HUD canvas. The anchor is guest space; entities go through the same
	/// handedness flip `Engine_MdlPlacement.FromGuest` applies to their transforms.
	/// </summary>
	private void DriveWorldSprites(bool guestFrame = true)
	{
		if (_worldSprites is null || !GodotObject.IsInstanceValid(_worldSprites))
		{
			return;
		}

		// CLEARED, not merely left alone, when the game is down: the last game's icons
		// ("9 Gold") stayed painted over the title screen after Quit to Title.
		if (!HostBridge.IsInGame)
		{
			if (!_worldSpritesCleared)
			{
				_worldSpritesCleared = true;
				_worldSpritesBuiltOnce = false;
				_lastRawWorldSprites = null;
				// The mods' HUD state goes with the game: markers, meters and fades are a session's.
				Engine_HudModMarkers.Clear();
				Engine_HudModMeters.Clear();
				Engine_UiModFade.Clear();
				Engine_UiHudOverrides.Clear();
				Refii.Mods.ModInput.ClearClaims();
				Refii.Lighting.Engine_ModLights.Clear();
				_worldSprites.Feed(
					new System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>());
				if (_worldSpritesTwoFed && _worldSpritesTwo is not null &&
					GodotObject.IsInstanceValid(_worldSpritesTwo))
				{
					_worldSpritesTwo.Feed(System.Array.Empty<Engine_HudWorldSprites.Sprite>());
					_worldSpritesTwoFed = false;
				}
			}

			return;
		}

		_worldSpritesCleared = false;

		if (GetWindow().GetCamera3D() is not { } camera)
		{
			return;
		}

		Vector2 window = GetWindow().GetVisibleRect().Size;
		if (window.X <= 0f || window.Y <= 0f)
		{
			return;
		}

		// The projection's inputs live in fields and the projector is one cached delegate, so
		// the per-frame feed allocates no closure (GC pauses stall the guest's sim thread).
		bool split = SeatSpriteViews(camera, window);
		_spriteProject ??= ProjectWorldSprite;

		// Reused, not reallocated — and cleared here rather than on entry, because the early
		// returns above deliberately leave the last frame's pooled placements standing. Neither
		// `Feed` nor `TrackProjected` keeps a reference past its call, and `_pooledCanvas` is
		// read later in this same frame by the icon drive.
		System.Collections.Generic.List<Engine_HudWorldSprites.Sprite> sprites =
			_worldSpriteList ??= new System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>();
		System.Collections.Generic.Dictionary<int, Vector2> pooled =
			_pooledCanvas ??= new System.Collections.Generic.Dictionary<int, Vector2>();
		pooled.Clear();
		System.Collections.Generic.List<HostBridge.WorldSprite> rawSprites =
			guestFrame || _lastRawWorldSprites is null
				? (_lastRawWorldSprites = HostBridge.GetWorldSprites())
				: _lastRawWorldSprites;
		bool rebuildSprites = guestFrame || !_worldSpritesBuiltOnce;
		System.Collections.Generic.List<Engine_HudWorldSprites.Sprite> spritesTwo =
			_worldSpriteListTwo ??= new System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>();
		if (rebuildSprites)
		{
			_worldSpritesBuiltOnce = true;
			sprites.Clear();
			spritesTwo.Clear();
		}

		System.Collections.Generic.Dictionary<int, Vector2> pooledTwo =
			_pooledCanvasTwo ??= new System.Collections.Generic.Dictionary<int, Vector2>();
		pooledTwo.Clear();
		if (guestFrame)
		{
			_holdPromptTwo = CoopSession.LocalCoop ? HostBridge.GetPlayerTwoHoldPrompt() : null;
			_promptsTwo.Clear();
			_promptsOneHidden.Clear();
			_titlesOne.Clear();
			_titlesTwo.Clear();
			if (split)
			{
				HostBridge.GetPromptTitles(_promptTitles);
			}
			else
			{
				_promptTitles.Clear();
			}
			if (split)
			{
				int prompts = Fable2Bridge.GetPlayerTwoPrompts(_promptsTwoBuffer);
				for (int i = 0; i < prompts; ++i)
				{
					_promptsTwo.Add(_promptsTwoBuffer[i]);
				}

				int hidden = Fable2Bridge.GetPlayerOneHiddenPrompts(_promptsTwoBuffer);
				for (int i = 0; i < hidden; ++i)
				{
					_promptsOneHidden.Add(_promptsTwoBuffer[i]);
				}
			}
		}

		foreach (HostBridge.WorldSprite sprite in rawSprites)
		{
			if (sprite.Pooled)
			{
				// A prompt on a pooled HUD element: the element draws it, Godot places it. The
				// off-screen hero and henchman markers (types 52/53, the guest's own test in
				// `sub_82824D60`) are pinned inside the edge. On split screen the hero's marker
				// is for player two, so it is pinned inside player two's half through player
				// two's camera; the icons layer stands in player one's seat, so the point is
				// carried into that seat's canvas. On split screen a use prompt is drawn in each
				// half whose player sees its thing (Fable2Bridge.GetPlayerTwoPrompts /
				// GetPlayerOneHiddenPrompts): player one's by their layer, player two's by theirs.
				bool prompt = sprite.Type is not (52u or 53u);
				Vector2? placed = split && sprite.Type == 52u
					? ProjectOffScreenMarker(_spriteViews[1], sprite.Anchor, sprite.Entity) is { } theirs
						? (Vector2?)_spriteViews[0].Frame.FromCanvas(_spriteViews[1].Frame.ToCanvas(theirs))
						: null
					: sprite.Type is 52u or 53u
						? ProjectOffScreenMarker(_spriteViews[0], sprite.Anchor, sprite.Entity)
						: split && _promptsOneHidden.Contains(sprite.Entity)
							? null
							: ProjectWorldSprite(sprite.Anchor, sprite.Entity);
				if (sprite.Slot >= 0 && placed is { } canvas)
				{
					pooled[sprite.Slot] = canvas;
				}

				if (split && prompt && sprite.Slot >= 0 && _promptsTwo.Contains(sprite.Entity) &&
					ProjectWorldSpriteIn(_spriteViews[1], sprite.Anchor, sprite.Entity) is { } theirsAt)
				{
					pooledTwo[sprite.Slot] = theirsAt;
				}

				// EACH HALF ITS OWN PLAYER'S TEXT: the guest's prompt for a thing is the nearer
				// player's; the other player's own text (who owns it, who may open it) comes from
				// the guest's update run for them (HostBridge.GetPromptTitles).
				if (guestFrame && split && prompt && sprite.Slot >= 0 &&
					_promptTitles.TryGetValue(sprite.Entity, out (int Player, string Title) other))
				{
					(other.Player == 2 ? _titlesTwo : _titlesOne)[sprite.Slot] = other.Title;
				}

				continue;
			}

			// A world icon of the sprite layer: composed below, with the mods' markers, by the one
			// composition (Engine_HudModMarkers.Compose).
		}

		// THE WORLD ICONS — the game's own and the mods' markers — through ONE composition
		// (Engine_HudModMarkers.Compose) into each view's list: the game's exactly as the guest
		// states them (kind, texture hash, fade, anchor, tweened scale, text), shown by the one
		// per-player rule (each half its own player's hearts and splashes; one shared screen both
		// players'; an online host its own, the joiner is sent player two's); the markers after
		// them, sized by that view's camera distance through the game's own curve. Fed on the same
		// frames, through the same layers and projectors.
		if (rebuildSprites)
		{
			Engine_HudModMarkers.LoadIcon ??= LoadMarkerIcon;
			bool hostOnline = CoopSession.PeerStanding && !CoopSession.LocalCoop && !CoopSession.IsClient;
			double clock = Godot.Time.GetTicksMsec() / 1000.0;
			Engine_HudModMarkers.Compose(sprites, rawSprites, 1, split, CoopSession.LocalCoop, hostOnline,
				_markerDistanceOne ??= guest => MarkerDistanceSquared(_spriteViews[0], guest), clock);
			if (split)
			{
				Engine_HudModMarkers.Compose(spritesTwo, rawSprites, 2, split, CoopSession.LocalCoop, hostOnline,
					_markerDistanceTwo ??= guest => MarkerDistanceSquared(_spriteViews[1], guest), clock);
			}
		}

		// PLAYER TWO'S OWN HOLD PROMPT (the guest's one hold slot is player one's): drawn by their
		// layer's hold icon, in their half or on the shared screen.
		if (_holdPromptTwo is { } hold &&
			(split ? ProjectWorldSpriteIn(_spriteViews[1], hold.Anchor) : ProjectWorldSprite(hold.Anchor)) is { } holdAt)
		{
			pooledTwo[16] = holdAt;
		}

		if (_worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo))
		{
			_worldIconsTwo.StatedFill = _holdPromptTwo?.Title;
			_worldIconsTwo.StatedTitles = split ? _titlesTwo : null;
		}

		if (_worldIcons is not null && GodotObject.IsInstanceValid(_worldIcons))
		{
			_worldIcons.StatedTitles = split ? _titlesOne : null;
		}

		_worldSprites.Place ??= PlaceWorldSprite;
		if (rebuildSprites)
		{
			_worldSprites.Feed(sprites);
		}

		// Player two's copy is fed only while it is drawn, and emptied once when the split ends.
		if (_worldSpritesTwo is not null && GodotObject.IsInstanceValid(_worldSpritesTwo))
		{
			_worldSpritesTwo.Place ??= PlaceWorldSpriteTwo;
			if (split && rebuildSprites)
			{
				_worldSpritesTwo.Feed(spritesTwo);
				_worldSpritesTwoFed = true;
			}
			else if (!split && _worldSpritesTwoFed)
			{
				_worldSpritesTwo.Feed(System.Array.Empty<Engine_HudWorldSprites.Sprite>());
				_worldSpritesTwoFed = false;
			}
		}
	}

	/// <summary>Whether player two's sprite layer holds this split's sprites.</summary>
	private bool _worldSpritesTwoFed;
	private bool _worldSpritesBuiltOnce;

	private System.Func<Vector3, float?>? _markerDistanceOne;
	private System.Func<Vector3, float?>? _markerDistanceTwo;

	/// <summary>A view camera's squared distance to a guest point, for a marker's size; null with no camera.</summary>
	private static float? MarkerDistanceSquared(SpriteView view, Vector3 guest) =>
		view.Camera is { } camera && GodotObject.IsInstanceValid(camera)
			? camera.GlobalPosition.DistanceSquaredTo(GameWorld.GuestToGodot(guest))
			: null;

	/// <summary>
	/// A marker's icon by what the call named: a PNG of an enabled mod (<c>art/x.png</c>, through
	/// the mount or the package itself), else a game texture path through the presentation's loader.
	/// </summary>
	private Texture2D? LoadMarkerIcon(string name) =>
		Engine_HudModMarkers.LoadIconWith(name, path => _presentation?.LoadTexture(path));

	/// <summary>The other player's prompt text per thing, and each half's titles by pool slot.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, (int Player, string Title)> _promptTitles = new();
	private readonly System.Collections.Generic.Dictionary<int, string> _titlesOne = new();
	private readonly System.Collections.Generic.Dictionary<int, string> _titlesTwo = new();

	/// <summary>Player two's own hold prompt, read once a guest frame (local co-op).</summary>
	private (Vector3 Anchor, string Title)? _holdPromptTwo;

	/// <summary>Player two's half's sprites on split screen, reused like <c>_worldSpriteList</c>.</summary>
	private System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>? _worldSpriteListTwo;

	/// <summary>The things whose use prompt answers player two, read once a guest frame.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _promptsTwo = new();

	/// <summary>The things whose use prompt only player two sees, read once a guest frame.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _promptsOneHidden = new();
	private readonly uint[] _promptsTwoBuffer = new uint[64];

	/// <summary>
	/// One camera the world sprites are projected through, and the seat they land in: the
	/// window's camera on the whole window, or one player's view camera and half on split screen.
	/// </summary>
	private sealed class SpriteView
	{
		public Camera3D? Camera;

		/// <summary>The camera's own viewport, in pixels — what <c>UnprojectPosition</c> answers in.</summary>
		public Vector2 Viewport;

		public Engine_UiSeats.Frame Frame;

		/// <summary>
		/// Whether the scene-depth line of sight applies: a view whose depth is captured (each
		/// view's own, Engine_FxDepthCaptureEffect.View).
		/// </summary>
		public bool TestSight = true;

		/// <summary>Cached world-to-viewport projector updated once per frame.</summary>
		public readonly Refii.Core.Engine_CameraProjector Projector = new();

		/// <summary>A pixel on the camera's viewport, in the seat's own pixels.</summary>
		public Vector2 SeatPixel(Vector2 pixel) =>
			Viewport == Frame.Window || Viewport.X <= 0f || Viewport.Y <= 0f
				? pixel
				: pixel * (Frame.Window / Viewport);

		/// <summary>Whether a viewport pixel lies inside the view.</summary>
		public bool Shows(Vector2 pixel) =>
			pixel.X >= 0f && pixel.Y >= 0f && pixel.X <= Viewport.X && pixel.Y <= Viewport.Y;
	}

	/// <summary>[0] player one's (or the whole window's), [1] player two's while split.</summary>
	private readonly SpriteView[] _spriteViews = { new(), new() };

	/// <summary>Whether the sprite layers carry seat transforms a shared screen must undo.</summary>
	private bool _spriteLayersSeated;

	/// <summary>
	/// Points both sprite views at this frame's cameras, and seats the layers drawn through them:
	/// on the whole window everything is exactly as it always was; split, player one's layers
	/// (sprites, pooled icons, their reticule, the slow-time dial) take player one's half and
	/// player two's (their sprites and reticule) player two's. Returns whether the screen is split.
	/// </summary>
	private bool SeatSpriteViews(Camera3D windowCamera, Vector2 window)
	{
		Camera3D? one = Engine_Views.FirstViewport is { } first && GodotObject.IsInstanceValid(first)
			? first.GetCamera3D()
			: null;
		bool split = Engine_Views.Split && one is not null &&
			Engine_Views.SecondViewport is { } second && GodotObject.IsInstanceValid(second);

		SpriteView mine = _spriteViews[0];
		mine.Camera = split ? one : windowCamera;
		mine.Viewport = split ? (Vector2)Engine_Views.FirstViewport!.Size : window;
		mine.Frame = Engine_UiSeats.Of(window, split ? Engine_UiSeats.Seat.PlayerOne : Engine_UiSeats.Seat.Whole);
		mine.TestSight = true;

		SpriteView theirs = _spriteViews[1];
		theirs.Camera = split ? Engine_Views.Second : null;
		theirs.Viewport = split ? (Vector2)Engine_Views.SecondViewport!.Size : Vector2.Zero;
		theirs.Frame = Engine_UiSeats.Of(window, Engine_UiSeats.Seat.PlayerTwo);
		theirs.TestSight = split;

		_spriteWindow = mine.Frame.Window;

		if (split || _spriteLayersSeated)
		{
			_spriteLayersSeated = split;
			Seat(_worldSprites, mine.Frame, 52f);
			Seat(_worldIcons, mine.Frame, 50f);
			Seat(_worldIconsTwo, split ? theirs.Frame : mine.Frame, 50f);
			Seat(_reticule, mine.Frame, 54f);
			Seat(_slowTimeDial, mine.Frame, 50f);
			Seat(_worldSpritesTwo, theirs.Frame, 52f);
			Seat(_reticuleTwo, theirs.Frame, 54f);
			Seat(_slowTimeDialTwo, theirs.Frame, 50f);
			if (_reticule is not null && GodotObject.IsInstanceValid(_reticule))
			{
				_reticule.OnlyPlayer = split ? 0 : -1;
			}
		}

		bool theirsShown = split && !_fullCutscene;
		ShowLayer(_worldSpritesTwo, theirsShown);
		ShowLayer(_worldIconsTwo, (split || _holdPromptTwo is not null) && !_fullCutscene);
		ShowLayer(_reticuleTwo, theirsShown);
		ShowLayer(_slowTimeDialTwo, theirsShown);
		return split;

		static void Seat(Node3D? layer, Engine_UiSeats.Frame frame, float z)
		{
			if (layer is not null && GodotObject.IsInstanceValid(layer))
			{
				layer.Transform = frame.Transform(z);
			}
		}

		static void ShowLayer(Node3D? layer, bool visible)
		{
			if (layer is not null && GodotObject.IsInstanceValid(layer))
			{
				if (layer.Visible != visible)
				{
					layer.Visible = visible;
				}

				ProcessModeEnum targetMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
				if (layer.ProcessMode != targetMode)
				{
					layer.ProcessMode = targetMode;
				}
			}
		}
	}

	/// <summary>
	/// One world sprite's place on the HUD canvas and its line of sight, read off the camera AS
	/// IT STANDS WHEN ASKED — the sprite layer asks from its own late process, after GameWorld
	/// has moved the camera. The anchor rides its entity's drawn body (`DrawnLead`).
	/// </summary>
	private Engine_HudWorldSprites.Placed? PlaceWorldSprite(Vector3 guest, uint entity) =>
		PlaceIn(_spriteViews[0], guest, entity, clip: true);

	/// <summary><see cref="PlaceWorldSprite"/> through player two's camera, into their half.</summary>
	private Engine_HudWorldSprites.Placed? PlaceWorldSpriteTwo(Vector3 guest, uint entity) =>
		PlaceIn(_spriteViews[1], guest, entity, clip: true);

	/// <summary>
	/// A sprite's place in <paramref name="view"/>'s seat. A half has a neighbour, so with
	/// <paramref name="clip"/> an anchor outside the half's view is not drawn rather than drawn
	/// over the other player's picture; the whole window keeps no such test, as before.
	/// </summary>
	private Engine_HudWorldSprites.Placed? PlaceIn(SpriteView view, Vector3 guest, uint entity, bool clip)
	{
		if (view.Camera is not { } camera ||
			!view.Projector.Update(camera, view.Viewport, Engine.GetProcessFrames()))
		{
			return null;
		}

		Vector3 world = entity != 0u && GameEntities.Current is { } entities
			? entities.WorldAnchorFor(entity, guest)
			: GameWorld.GuestToGodot(guest);

		if (!view.Projector.TryProject(world, out Vector2 pixel, out float depth))
		{
			return null;
		}

		if (clip && !view.Frame.IsWhole && !view.Shows(pixel))
		{
			return null;
		}

		return new Engine_HudWorldSprites.Placed(
			view.Frame.FromPixel(view.SeatPixel(pixel)),
			view.TestSight
				? new Vector3(pixel.X / view.Viewport.X, pixel.Y / view.Viewport.Y, depth)
				: new Vector3(-1f, 0f, 0f));
	}

	/// <summary>
	/// A guest world anchor in canvas units, or null when it is behind the camera — the
	/// projector <see cref="DriveWorldSprites"/> hands to the sprite feed. On a half, an anchor
	/// outside player one's view is parked far off the canvas: the pooled icon it places would
	/// otherwise fall back to the guest's own projection, which is for the whole screen.
	/// </summary>
	private Vector2? ProjectWorldSprite(Vector3 guest) => ProjectWorldSpriteIn(_spriteViews[0], guest, 0u);

	private Vector2? ProjectWorldSprite(Vector3 guest, uint entity) => ProjectWorldSpriteIn(_spriteViews[0], guest, entity);

	/// <summary><see cref="ProjectWorldSprite"/> through one view's camera, into that view's seat.</summary>
	private static Vector2? ProjectWorldSpriteIn(SpriteView view, Vector3 guest, uint entity = 0u)
	{
		if (view.Camera is not { } camera ||
			!view.Projector.Update(camera, view.Viewport, Engine.GetProcessFrames()))
		{
			return null;
		}

		// The WORLD's conversion, not the model handedness: the anchor is a guest world
		// point with its up-lift on guest Z, and `(x, y, -z)` put that lift into depth.
		// When an entity is bound, ride its host-interpolated body and head bone (WorldAnchorFor).
		Vector3 world = entity != 0u && GameEntities.Current is { } entities
			? entities.WorldAnchorFor(entity, guest)
			: GameWorld.GuestToGodot(guest);
		if (!view.Projector.TryProject(world, out Vector2 pixel, out _))
		{
			return null;
		}

		if (!view.Frame.IsWhole && !view.Shows(pixel))
		{
			return new Vector2(100000f, 100000f);
		}

		return view.Frame.FromPixel(view.SeatPixel(pixel));
	}

	/// <summary>
	/// The off-screen partner markers (`HeroOffScreenWorldIcon` / `HenchmanOffScreenWorldIcon`,
	/// types 52/53) are ALWAYS clamped into the screen — `sub_82824D60` sends both through
	/// `sub_82824BD0`, which pins the point inside the viewport less 10% (plus 10 px) at the
	/// left, 10% plus 40 px at the top, and 9% (plus 10 px) right / 9% bottom, in its 720-line
	/// pixels. Behind the camera the guest pushes the point outward before clamping; its exact
	/// vector math is not reproduced — the reflected point is extended to the edge instead. On
	/// a half, "the screen" is that half.
	/// </summary>
	private Vector2? ProjectOffScreenMarker(SpriteView view, Vector3 guest, uint entity = 0u)
	{
		if (view.Camera is not { } camera || !GodotObject.IsInstanceValid(camera) ||
			view.Viewport.X <= 0f || view.Viewport.Y <= 0f)
		{
			return null;
		}

		Vector3 world = entity != 0u && GameEntities.Current is { } entities
			? entities.WorldAnchorFor(entity, guest)
			: GameWorld.GuestToGodot(guest);
		Transform3D eye = camera.GlobalTransform;
		Vector3 local = eye.AffineInverse() * world;
		bool behind = local.Z > -0.01f;
		if (behind)
		{
			local.Z = -Mathf.Max(Mathf.Abs(local.Z), 0.01f);
			world = eye * local;
		}

		Vector2 pixel = camera.UnprojectPosition(world);
		Vector2 canvas = view.Frame.FromPixel(view.SeatPixel(pixel));
		if (behind && canvas.LengthSquared() > 0.0001f)
		{
			canvas = canvas.Normalized() * 100000f;
		}

		Vector2 extent = view.Frame.Extent;
		float unit = extent.Y / 720f;
		float left = (-extent.X * 0.5f) + (extent.X * 0.1f) + (10f * unit);
		float right = (extent.X * 0.5f) - (extent.X * 0.09f) - (10f * unit);
		float top = (extent.Y * 0.5f) - (extent.Y * 0.1f) - (40f * unit);
		float bottom = (-extent.Y * 0.5f) + (extent.Y * 0.09f);
		return new Vector2(Mathf.Clamp(canvas.X, left, right), Mathf.Clamp(canvas.Y, bottom, top));
	}

	/// <summary>Player one's seat window — the whole window, or their half while split — for
	/// the layers that size themselves off it. See <see cref="SeatSpriteViews"/>.</summary>
	private Vector2 _spriteWindow;

	private System.Func<Vector3, Vector2?>? _spriteProject;

	/// <summary>The world sprites handed to the feed, reused frame to frame.</summary>
	private System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>? _worldSpriteList;

	/// <summary>The HUD's element pipeline — one resolve, shared with every owned surface.</summary>
	private Engine_UiElementResolve? _hudResolve;


	/// <summary>The HUD stood on its file and the guest's statements (Engine_UiHudStand).</summary>
	private Engine_UiHudStand? _hudStand;

	private int[]? _hudStatedSlides;
	private bool[]? _hudStatedPlays;
	private bool[]? _hudShown;
	private long _hudStandTicks;
	private ulong _hudTexturesGeneration = ulong.MaxValue;

	/// <summary>The HUD values' registry generation the textures last took (`@Gui.Image` among them).</summary>
	private ulong _hudRegistryTexturesGeneration = ulong.MaxValue;

	private int _hudStatedFolded = -1;

	private Engine_UiBindings? _pulsersKnownFor;

	/// <summary>
	/// THE SLIDES THE HOST STATES BY NAME, as the stand's stated input: what the registry writers
	/// play on every element bound to a name — toggle true → 2, false → 1 (`sub_823FE848`), a
	/// fader's index (`sub_827FA600`, plus one on the second channel), a slide set's verbatim
	/// (`sub_828421F8`). <paramref name="plays"/> marks the entries the writer played: the toggle
	/// and the fader go through `sub_82426CA8` (entry, then `SetPaused(0)`); the second fader and
	/// the slide set through `sub_82426BA8`, which leaves an authored-paused slide at t = 0 unless
	/// the guest's own write played it.
	/// </summary>
	private void FoldStatedHudSlides(int[] slides, bool[] plays)
	{
		if (_hud is null || _hud.Bindings is not { } bindings)
		{
			return;
		}

		// Every pulser the file binds stands stopped at 0 until the guest starts it.
		if (!ReferenceEquals(bindings, _pulsersKnownFor))
		{
			_pulsersKnownFor = bindings;
			foreach (Engine_UiBindings.Binding binding in bindings.All)
			{
				if (binding.Kind == Engine_UiBindings.Kind.Pulser)
				{
					Engine_UiHudPulsers.Know(binding.NameHash);
				}
			}
		}

		int folded = 0;
		void Fold(int ordinal, int slide, bool played)
		{
			if (ordinal < 0 || ordinal >= slides.Length)
			{
				return;
			}

			slides[ordinal] = slide;
			plays[ordinal] = played;
			++folded;
		}

		// THE GUEST'S OWN REGISTRY WRITES FIRST (`@Gui.Slide`, hooks/gui_registry_hooks.cpp): the
		// toggle (registry 9, `sub_823FE848`: 2 on, 1 off), the fader and second fader (2, 3) and
		// the slide set (10), each slide as its writer was handed it, on every element bound to
		// the name — the spell charges' fader (`PlayerSpellCharger`) and charge levels
		// (`PlayerSpellcharge1..5`), the health and experience faders. What the host states by
		// name below (the TopBox, the timing meter, the natives) is laid over them.
		foreach ((int registry, uint hash, int slide) in _hud.Values.RegistrySlides)
		{
			if (Engine_UiHudRegistryWrites.SlideKind(registry) is not { } kind)
			{
				continue;
			}

			foreach (int ordinal in bindings.Elements(kind, hash))
			{
				Fold(ordinal, slide, _hud.Values.RegistryPlayed(registry, hash));
			}
		}

		foreach ((uint hash, bool visible) in _hud.Values.Toggles)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideToggle, hash))
			{
				Fold(ordinal, visible ? 2 : 1, true);
			}
		}

		foreach ((uint hash, int slide) in _hud.Values.Fades)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader, hash))
			{
				Fold(ordinal, slide, true);
			}

			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader2, hash))
			{
				Fold(ordinal, slide + 1, _hud.Values.RegistryPlayed(3, hash));
			}
		}

		// REGISTRY 10 IS VERBATIM — no shift. `sub_828421F8` hands the published integer
		// straight to the slide player, and registry 9 reaches that same call through
		// `sub_82426CA8` with 1 or 2, so the toggle's +1 is the toggle's and not the channel's.
		// Nothing clamps: an index the owner has no slide for simply finds nothing.
		foreach ((uint hash, int slide) in _hud.Values.SlideSets)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideSet, hash))
			{
				Fold(ordinal, slide, _hud.Values.RegistryPlayed(10, hash));
			}
		}

		if (folded != _hudStatedFolded)
		{
			_hudStatedFolded = folded;
			GD.Print($"[hud] {folded} host-stated toggle/fader slide(s) stand the HUD's rest state");
		}
	}

	private readonly System.Collections.Generic.List<(int Ordinal, float Percent)> _hudMeterTimes = new();

	/// <summary>
	/// Each bound meter's component and its stated percentage — the meter registry's scrub,
	/// `goToTime(trunc(pct × 10))` (`sub_823FDFF8`), played by the stand. Layout meters are not
	/// played (<see cref="Engine_UiHudStand.PlaysMeter"/>).
	/// </summary>
	private System.Collections.Generic.IReadOnlyList<(int Ordinal, float Percent)> HudMeterTimes()
	{
		_hudMeterTimes.Clear();
		if (_hud?.Bindings is not { } bindings)
		{
			return _hudMeterTimes;
		}

		foreach (Engine_UiBindings.Binding binding in bindings.All)
		{
			if (Engine_UiHudStand.PlaysMeter(binding) &&
				_hud.Values.Meter(new Engine_HudValues.Handle(binding.Name, binding.NameHash)) is { } percent)
			{
				_hudMeterTimes.Add((binding.ElementOrdinal, percent));
			}
		}

		return _hudMeterTimes;
	}

	/// <summary>
	/// Releases the stated opacity of every element an owner's chosen slide commands or
	/// animates, and takes the slide's active bits — so the slide's end pose is what draws.
	/// </summary>
	private static int ReleaseHudOwnerSlide(
		Engine_UiScreen doc, int owner, int wanted, bool[] active, float?[] stated)
	{
		if (doc.Document is not { } file || wanted <= 0)
		{
			return 0;
		}

		return Engine_UiElementResolve.SlideOf(file, owner, wanted) is { } slide
			? ReleaseHudSlideEntries(file, slide, active, stated)
			: 0;
	}

	/// <summary>The one release of a slide's entries, shared with a copy's own slide (Engine_UiElementResolve.ReleaseSlideEntries).</summary>
	private static int ReleaseHudSlideEntries(
		Engine_BgfDocument file, BgfSlide slide, bool[] active, float?[] stated) =>
		Engine_UiElementResolve.ReleaseSlideEntries(file, slide, active, stated);

	/// <summary>The hud document, kept for the opacity chain the piece gate walks.</summary>
	private Engine_UiScreen? _hudScreen;

	/// <summary>The guest's hud presentation address, taken as Follow declines to mirror it.</summary>
	private uint _hudAddress;

	/// <summary>
	/// A behaviour's presentation event, timed by Godot's playback of the file, posted to the
	/// callback the guest registered for it: `PresentationEvent:&lt;file&gt;:&lt;parent&gt;:&lt;EVENT&gt;`
	/// (hooks/gui_menu_hooks.cpp, which swallows the guest's own timeline delivery of the same event).
	/// </summary>
	private static void PostPresentationEvent(string presentation, Engine_UiBehaviourEvents.Fired fired)
	{
		string file = System.IO.Path.GetFileNameWithoutExtension(presentation.Replace('\\', '/')).ToLowerInvariant();
		string action = $"PresentationEvent:{file}:{fired.ParentName}:{fired.Event}";
		GD.Print($"[behaviour] {file}: {fired.Event} on '{fired.ParentName}' (#{fired.Ordinal}) — posting '{action}'");
		HostBridge.PostFrontEndAction(action);
	}

	/// <summary>
	/// THE HUD'S BEHAVIOURS (`hud.bgf`'s FinishedTransitioningToGame, live on the root's
	/// `PauseMenuToGame` slide for 0..482): the HUD stands on the guest's sample, so its clocks are
	/// a windows-only playback over the stated slides, and a behaviour is live while the resolve
	/// draws it and its window is open. A new HUD presentation starts both afresh.
	/// </summary>
	private Engine_UiBehaviourEvents? _hudBehaviours;
	private Engine_UiSlidePlayback? _hudWindows;
	private Engine_UiScreen? _hudBehavioursScreen;
	private uint _hudBehavioursAddress;
	private static readonly System.Diagnostics.Stopwatch HudBehaviourClock = System.Diagnostics.Stopwatch.StartNew();

	private void ObserveHudBehaviours(Engine_UiScreen doc, int[] slides)
	{
		if (!ReferenceEquals(doc, _hudBehavioursScreen))
		{
			_hudBehavioursScreen = doc;
			_hudBehaviours = Engine_UiBehaviourEvents.For(doc);
			_hudWindows = _hudBehaviours is { } found
				? new Engine_UiSlidePlayback(doc, found.Behaviours.Select(b => b.Ordinal).ToArray())
				: null;
			_hudBehavioursAddress = _hudAddress;
		}

		if (_hudBehaviours is not { } behaviours || _hudWindows is not { } windows || _hudResolve is not { } resolve)
		{
			return;
		}

		if (_hudBehavioursAddress != _hudAddress)
		{
			_hudBehavioursAddress = _hudAddress;
			behaviours.Reset();
			windows.Reset();
		}

		windows.Step(slides, null, HudBehaviourClock.ElapsedMilliseconds);
		bool[] drawn = resolve.Drawn;
		bool[] closed = windows.Closed;
		_observeDrawn = drawn;
		_observeClosed = closed;
		System.Collections.Generic.List<Engine_UiBehaviourEvents.Fired>? fired = behaviours.Observe(_observeLive ??= ObserveLive);
		if (fired is null)
		{
			return;
		}

		foreach (Engine_UiBehaviourEvents.Fired one in fired)
		{
			PostPresentationEvent("hud", one);
		}
	}

	// The HUD behaviour pass's liveness test, as one cached delegate over fields: a lambda
	// capturing the frame's arrays made a closure and a delegate every pass.
	private bool[] _observeDrawn = System.Array.Empty<bool>();
	private bool[] _observeClosed = System.Array.Empty<bool>();
	private System.Func<int, bool>? _observeLive;
	private bool[] _statedPlays = System.Array.Empty<bool>();
	private System.Func<int, bool>? _statedPlayTest;

	private bool ObserveLive(int ordinal) =>
		ordinal >= 0 && ordinal < _observeDrawn.Length && _observeDrawn[ordinal] &&
		!(ordinal < _observeClosed.Length && _observeClosed[ordinal]);

	private bool StatedPlay(int ordinal) =>
		ordinal >= 0 && ordinal < _statedPlays.Length && _statedPlays[ordinal];

	private int _worldIconTick;

	/// <summary>
	/// A scratch buffer at least <paramref name="length"/> long, grown in place.
	///
	/// The feed's arrays are sized by the hud document's element count, which does not change
	/// while a document is up; reallocating them each pass only makes garbage.
	/// </summary>
	private static T[] Buffer<T>(ref T[]? held, int length)
	{
		if (held is null || held.Length < length)
		{
			held = new T[length];
		}

		return held;
	}

	private bool[]? _hudActive;
	private int[]? _hudSlides;
	private float?[]? _hudStated;

	private long _hudResolveTicks;

	private long _hudFeedTicks;

	private int _hudFeedFrames;

	/// <summary>
	/// Feeds the world icons: positions every frame (the guest restates them every frame),
	/// the lit set, titles and art at the registry sampling cadence.
	///
	/// AND THE REBUILT HUD'S GATE, WHICH IS PER FRAME AND WAS NOT. Whether a HUD piece is dark is
	/// a per-frame fact — the guest lights a spell charge, parks a quick slot, fades the whole
	/// layer for a cutscene — and its VALUES are already sampled every frame in
	/// `Engine_HudView._Process`. Only the gate rode the world icons' registry cadence, because
	/// it was written into this method to share one element read, so the HUD drew fresh numbers
	/// through visibility up to thirty frames old: a bar that pops in half a second after the
	/// menu closes, and a charge that lights and dies between two samples never drawn at all.
	///
	/// THE SPLIT IS BY HOW OFTEN THE ANSWER CHANGES, not by what is cheap. Active bits, slides
	/// and opacity move every frame and are read every frame; which TEXTURE a slot carries
	/// changes when the player equips something, and stays on the icons' cadence with the lit
	/// set and the titles.
	/// </summary>
	private void DriveWorldIcons(bool guestFrame = true)
	{
		DriveWorldSprites(guestFrame);
		DriveReticule(guestFrame);
		if (guestFrame)
		{
			DriveSlowTimeDial();
			DriveFullCutscene();
			DriveCreditsRoll();
		}

		if (_worldIcons is null || _hudAddress == 0 ||
			!GodotObject.IsInstanceValid(_worldIcons))
		{
			return;
		}

		// THE WHEEL IS A ONE-FRAME STATEMENT, so the ordinary cadence cannot serve it.
		// `sub_826AB1F8` writes all eight sectors' icons and titles in the single frame
		// `sub_826A9A60` opens the ring, sets the populated flag at `wheel+30` after the
		// populate returns, and never restates them — there is no per-frame update on that
		// class. Sampling on the 30-frame cadence therefore catches whatever fraction of the
		// ring existed at the last multiple of 30, which at the ~19fps one run measured is
		// most of a second of half-dressed wheel: a widget the player is driving directly has to
		// sample at the rate they drive it.
		//
		// The increment stays on the left so it runs every frame regardless — this is an
		// EXTRA sampling window, not a replacement clock. It costs the texture-hash walk per
		// frame for as long as the ring is held, and only then.
		bool cadence = ++_worldIconTick % WorldIconSampleFrames == 0 ||
			Fable2Input.QuickWheelHeld;

		// THE GATE RUNS EVERY FRAME AGAIN, because the read it was rationing is no longer a
		// read. It went 30-frame -> per-frame -> every third frame, and each move was the right
		// call for the cost at the time: per-frame cost 8.4ms, all of it marshalling 3,431
		// elements' worth of strings the gate never looks at, so a third of the frames bought a
		// third of the cost at a latency the old cadence could not touch anyway.
		//
		// That comment ended "the honest fix is a lean bridge export that returns only the gate
		// channels; until that ABI bump, this is the balance". The ABI bump happened, and it was
		// better than a lean export: the guest's own writers state the HUD's elements (Engine_UiHudStand),
		// so a read costs a lookup, and rationing it buys nothing and costs up to two frames of
		// latency on the HUD appearing and disappearing.
		// Nothing rations it any more, so there is no gate variable to carry.

		// WHAT THE PER-FRAME GATE COSTS, reported rather than assumed — and kept now that the
		// reads are cached rather than retired with them, because "the cache made it free" is
		// exactly the kind of claim this project has been caught asserting. The number is what
		// says the seed is holding; a gate that climbs back towards its old cost means something
		// is marking the HUD stale every frame. Averaged over the cadence, so the line is one
		// number a second rather than noise.
		bool hudValid = _hud is not null && GodotObject.IsInstanceValid(_hud);
		bool needHudFeed = guestFrame || cadence || Fable2Input.QuickWheelHeld ||
			Fable2Input.SpellsMenuHeld ||
			_hudOverrideGeneration != Engine_UiHudOverrides.Generation || (hudValid && _hud!.ElementActive is null);
		long began = System.Diagnostics.Stopwatch.GetTimestamp();
		if (needHudFeed && _hudScreen is { Nodes.Length: > 0 } doc)
		{
			// THE HUD STANDS ON ITS FILE AND THE GUEST'S STATEMENTS (Engine_UiHudStand): the
			// presentation is headless, so there is no element tree to read. The host's by-name
			// slides — the registry toggles, faders and slide sets, a mod's `hud_slide` — are the
			// stated slides the rest state stands under; everything the guest tells the
			// presentation's elements (slides, plays, `active`, attributes, texts, textures) is
			// played over that by Godot's own playback of the file.
			if (_hudStand is null || !ReferenceEquals(_hudStand.Screen, doc))
			{
				_hudStand = new Engine_UiHudStand(doc);
				_hudResolve = null;
			}

			int count = doc.Nodes.Length;
			int[] stated = Buffer(ref _hudStatedSlides, count);
			bool[] statedPlays = Buffer(ref _hudStatedPlays, count);
			System.Array.Clear(stated, 0, count);
			System.Array.Clear(statedPlays, 0, count);
			FoldStatedHudSlides(stated, statedPlays);
			// A mod's stated slides (hud_slide) stand over the guest's, for the local player's HUD.
			ApplyHudSlideOverrides(stated, HudLocalPlayer);

			_statedPlays = statedPlays;
			StateJobMultiplier(_hudStand);
			long standBegan = System.Diagnostics.Stopwatch.GetTimestamp();
			_hudStand.Stand(_hudAddress, stated, _hudResolve?.Drawn,
				HudBehaviourClock.ElapsedMilliseconds,
				_statedPlayTest ??= StatedPlay,
				HudMeterTimes());
			_hudStandTicks += System.Diagnostics.Stopwatch.GetTimestamp() - standBegan;

			_hudResolve ??= new Engine_UiElementResolve(doc);
			int[] slides = Buffer(ref _hudSlides, count);
			System.Array.Copy(_hudStand.Slides, slides, count);
			bool[] active = Buffer(ref _hudActive, count);
			System.Array.Copy(_hudStand.Active, active, count);

			// CLEARED: the stand's store carries every told opacity; this channel is only a mod
			// hold's.
			float?[] statedOpacity = Buffer(ref _hudStated, count);
			System.Array.Clear(statedOpacity, 0, count);

			// The pieces a mod holds visible (hud_hold): kept on their shown slides.
			ApplyHudHold(ref _hudHold, ref _hudHoldKey, doc, slides, active, statedOpacity, HudLocalPlayer);

			long resolveBegan = System.Diagnostics.Stopwatch.GetTimestamp();
			_hudResolve.Animate(_hudStand.Animated, _hudStand.Closed);
			_hudResolve.Apply(slides, active, statedOpacity, _hudStand.SwitchedOn);
			_hudResolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - resolveBegan;

			if (cadence)
			{
				uint[]? iconTextures = HudTextures(_hudStand);
				_worldIcons.Sample(_hudResolve.Drawn, _hudStand.Texts, iconTextures);
				if ((Engine_Views.Split || _holdPromptTwo is not null) &&
					_worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo))
				{
					_worldIconsTwo.Sample(_hudResolve.Drawn, _hudStand.Texts, iconTextures);
				}
			}

			if (hudValid)
			{
				ObserveHudBehaviours(doc, slides);
				_hud!.Resolve = _hudResolve;
				_hud.LiveSlides = slides;
				// The resolve's inputs beside it: a mod copy that states a slide of its own
				// resolves itself from these (Engine_HudView.Built.SlideOverrides).
				_hud.ActiveInputs = active;
				_hud.StatedOpacity = statedOpacity;

				// THE ART: the textures told to the HUD's elements, under a mod's icon overrides.
				if (cadence || _hud.LiveTextures is null || _hudTexturesGeneration != _hudStand.Generation ||
					_hudOverrideGeneration != Engine_UiHudOverrides.Generation ||
					_hudRegistryTexturesGeneration != _hud.Values.RegistryGeneration || ModWheelIconsStale ||
					_wheelItemsTexturesGeneration != _wheelItems.Generation)
				{
					_hudRegistryTexturesGeneration = _hud.Values.RegistryGeneration;
					_wheelItemsTexturesGeneration = _wheelItems.Generation;
					_hud.LiveTextures = ModWheelIcons(doc, HudTextures(_hudStand));
				}

				// AND THE TRANSFORMS: the stand's tracks and told writes — a meter's bar scale is its
				// component's track at the meter's time, as the guest's own scrub leaves it.
				_hud.LiveTransform = _hudStand.Transform;
				if (_hudHold is { OwnerCount: > 0 } held)
				{
					_hud.LiveTransform = held.Transforms(_hud.LiveTransform);
				}

				// AND THE ELEMENT TEXT, with the expression wheel's titles as its SetItem states them
				// (Engine_UiWheelItems), under the mods' wheel entries.
				_hud.LiveTexts = ModWheelTexts(doc, _wheelItems.Texts(doc.Document, _hudStand.Texts));

				bool[] shown = Buffer(ref _hudShown, count);
				for (int i = 0; i < count; ++i)
				{
					// A piece is dark when the guest says so, when its SLIDE says so, or when the
					// accumulated opacity has faded it out — three channels, one answer.
					shown[i] = _hudResolve.Drawn[i] && _hudResolve.Opacity[i] > 0.01f;
				}

				_hud.ElementActive = shown;
				DrivePlayerTwoExperience();
				DrivePlayerTwoHud();
				ShareHudFeed();
			}

			_hudFeedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
			++_hudFeedFrames;
			if (cadence && _hudFeedFrames >= 1800)
			{
				double ms = _hudFeedTicks * 1000.0
					/ System.Diagnostics.Stopwatch.Frequency / _hudFeedFrames;
				double per = 1000.0 / System.Diagnostics.Stopwatch.Frequency / _hudFeedFrames;
				GD.Print($"[hud] feed split/frame: stand {_hudStandTicks * per:0.00}ms, " +
					$"resolve {_hudResolveTicks * per:0.00}ms");
				_hudStandTicks = 0;
				_hudResolveTicks = 0;
				GD.Print($"[hud] feed {ms:0.00}ms/frame over {_hudFeedFrames} frame(s), {count} element(s)");
				_hudFeedTicks = 0;
				_hudFeedFrames = 0;
			}
		}

		if (hudValid && _hudScreen is not null)
		{
			// THE EXPRESSION WHEEL, through the guest's own open/close/select — the
			// GameAction chain that would run them from the pad does not fire here.
			// Hold to open, release to close (the shipped default; the toggle option
			// `GUIQuickAccessMenuOnToggle` is not read yet). The pump gates every call
			// on the guest's own tests, so a press it cannot honour is refused there.
			// What the touch controls follow: the wheel's open flag, the item slots (GameUi.Touch.cs).
			FollowTouchHud();
			if (Fable2Input.QuickWheelPressed)
			{
				HostBridge.PostFrontEndAction("QuickWheel:open");
				_wheelSector = -1;
				_wheelInGroup = false;
			}
			else if (Fable2Input.QuickWheelReleased)
			{
				HostBridge.PostFrontEndAction("QuickWheel:close");
			}
			else if (Fable2Input.QuickWheelHeld)
			{
				// A accepts (`sub_826AA230`: enter a group / start the item), its release
				// ends a held expression (`sub_826AA5D8`), B backs out (`sub_826AA510`).
				bool modWheel = Refii.Mods.ModWheel.Open;
				if (modWheel)
				{
					FollowModWheelButtons();
				}
				else if (Fable2Input.MenuAcceptPressed)
				{
					HostBridge.PostFrontEndAction("QuickWheel:select");
					NoteWheelSelect(_wheelSector);
				}
				else if (Fable2Input.MenuAcceptReleased)
				{
					HostBridge.PostFrontEndAction("QuickWheel:release");
				}
				else if (Fable2Input.MenuBackPressed)
				{
					HostBridge.PostFrontEndAction("QuickWheel:back");
					_wheelInGroup = false;
					ForgetWheelPointing();
				}

				// THE CURSOR, in the guest's sector numbering (WheelSector). The guest's own
				// raw-stick route feeds the same routine when it is alive; this posts only on a
				// sector change, so the two agree.
				// THE POINTER AS THE STICK: its offset from the wheel's centre, inside a
				// dead ring of a third of the wheel, is the same direction the stick
				// would hold. Left press selects, its release ends a held expression.
				Vector2 stick = GameInput.LeftStick;
				bool clicked = false;
				// THE WHEEL IS FOUND BY ITS NODE'S NAME, which is the HUD piece's id with its dots
				// made underscores (Engine_HudArt): `group_ExpressionWheel_wheelmenu`. It was
				// looked up as "ExpressionWheel", found nothing, and the pointer never pointed or
				// selected. Its CENTRE is the node's own position: the drawn bounds take in the
				// wheel's Close/Select prompts beside it and sit off to one side of the wedges.
				if (Engine_UiPointer.Canvas is { } wheelAt &&
					Engine_UiTheme.FindByName(_hud!, ExpressionWheelNode) is Node3D wheelNode &&
					Engine_UiPointer.CanvasBounds(wheelNode) is { } wheelBox)
				{
					var centre = new Vector2(wheelNode.GlobalPosition.X, wheelNode.GlobalPosition.Y);
					Vector2 offset = wheelAt - centre;
					float radius = Mathf.Min(wheelBox.Size.X, wheelBox.Size.Y) * 0.5f;
					if (radius > 0f && offset.Length() > radius / 3f)
					{
						stick = offset.Normalized();
					}

					// The click selects BELOW, after this frame's pointing: a pointer that arrives
					// with its click (a tap on a touchscreen, a quick mouse) must point the cursor
					// at the wedge first, or the select takes whatever was highlighted before.
					if (Engine_UiPointer.TakeLeft())
					{
						_wheelMouseDown = true;
						clicked = true;
					}
					else if (_wheelMouseDown && !Input.IsMouseButtonPressed(MouseButton.Left))
					{
						_wheelMouseDown = false;
						HostBridge.PostFrontEndAction("QuickWheel:release");
					}
				}

				int sector = WheelSector(stick);
				if (sector >= 0 && sector != _wheelSector)
				{
					_wheelSector = sector;
					HostBridge.PostFrontEndAction($"QuickWheel:point:{sector}");
				}

				if (clicked && !modWheel)
				{
					HostBridge.PostFrontEndAction("QuickWheel:select");
					NoteWheelSelect(_wheelSector);
				}
			}

			// LOCAL PLAYER TWO'S WHEEL: the same guest routines, opened for player two
			// (`QuickWheel:2:` — the wheel stores its owner, lists the owner's expressions and,
			// accepted with the owner's flag, performs on player two), off player two's pad.
			if (CoopSession.LocalCoop && Fable2Input.PlayerTwoPad >= 1)
			{
				if (Fable2Input.QuickWheelTwoPressed)
				{
					HostBridge.PostFrontEndAction("QuickWheel:2:open");
					_wheelSectorTwo = -1;
				}
				else if (Fable2Input.QuickWheelTwoReleased)
				{
					HostBridge.PostFrontEndAction("QuickWheel:2:close");
				}
				else if (Fable2Input.QuickWheelTwoHeld)
				{
					if (Refii.Mods.ModWheel.IsOpen(2))
					{
						FollowModWheelButtonsTwo();
					}
					else if (Fable2Input.WheelTwoAcceptPressed)
					{
						HostBridge.PostFrontEndAction("QuickWheel:2:select");
					}
					else if (Fable2Input.WheelTwoAcceptReleased)
					{
						HostBridge.PostFrontEndAction("QuickWheel:2:release");
					}
					else if (Fable2Input.WheelTwoBackPressed)
					{
						HostBridge.PostFrontEndAction("QuickWheel:2:back");
					}

					int sector = WheelSector(Fable2Input.LeftStickTwo);
					if (sector >= 0 && sector != _wheelSectorTwo)
					{
						_wheelSectorTwo = sector;
						HostBridge.PostFrontEndAction($"QuickWheel:2:point:{sector}");
					}
				}
			}

			// THE SPELLS MENU. The comment here used to say "no GameAction reaches the
			// consumer that would run 42 and 43, so the guest never sees the press" —
			// THAT IS FALSE. `sub_8236F788` has one code caller, the guest's own
			// GameAction dispatcher, and it is what sets the open flag in a live run
			// while the host lever logs only refusals. The guest sees the trigger.
			//
			// These posts are kept as a debug lever, and the pump now refuses to CLOSE a
			// blade it did not open — closing the guest's own clears the flag at
			// 0x8236f9b8 before the id-5 GuiBlock pop at 0x8236fa00 can run, and an
			// orphaned GuiBlock costs movement and camera for the rest of the session.
			if (Fable2Input.SpellsMenuPressed)
			{
				HostBridge.PostFrontEndAction("SpellsMenu:open");
			}
			else if (Fable2Input.SpellsMenuReleased)
			{
				HostBridge.PostFrontEndAction("SpellsMenu:close");
			}
			else if (Fable2Input.SpellsMenuHeld)
			{
				// THE HELD DIRECTION, AS THE GUEST'S OWN IDS, EVERY FRAME. The widget
				// moves on GameAction 28 UP / 29 DOWN / 30 LEFT / 31 RIGHT and nothing
				// else: its stick resolution is compiled off (`widget+165` is left 0 by
				// `sub_826B4358` at 0x826b4410, and `sub_828818E8` drops a 26 while it is
				// clear), which is why the old `stick` post never moved the box. The
				// cadence is the widget's own latch — first press at once, then 0.3 s,
				// then 0.1 s — so no clock here; `release` resets it when the pad lets go.
				//
				// 28 is UP = `++row` = the higher power level, drawn higher on screen; the
				// host's menu direction is a LIST direction (down = +1), so up is < 0.
				string? direction = Fable2Input.MenuDirection < 0 ? "up"
					: Fable2Input.MenuDirection > 0 ? "down"
					: Fable2Input.MenuHorizontal < 0 ? "left"
					: Fable2Input.MenuHorizontal > 0 ? "right"
					: null;
				if (direction is not null)
				{
					HostBridge.PostFrontEndAction("SpellBlade:" + direction);
					_spellBladeHeld = true;
				}
				else if (_spellBladeHeld)
				{
					HostBridge.PostFrontEndAction("SpellBlade:release");
					_spellBladeHeld = false;
				}

				// A copies this power level's will down into every later level that also
				// offers it (`sub_826B4EF8`). B is the guest's own GameAction 43 and is
				// never ours — see DispatchSpellsMenu on the orphaned GuiBlock.
				if (Fable2Input.MenuAcceptPressed)
				{
					HostBridge.PostFrontEndAction("SpellBlade:copydown");
				}

				// A mod's secondary pick (Engine_UiSpellAssignSecondary): Y or X on the
				// highlighted will, written to the mod's setting.
				Engine_UiSpellAssign.SecondaryAction partnerAction =
					Engine_UiSpellAssign.SecondaryPressed(Fable2Input.MenuFlipPressed, Fable2Input.MenuEquipPressed);
				if (partnerAction != Engine_UiSpellAssign.SecondaryAction.None)
				{
					_spells.WheelSecondary(partnerAction);
				}

				_spells.WheelPointer();
			}
		}

		bool splitIcons = Engine_Views.Split && _worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo);
		bool iconsTwo = (Engine_Views.Split || _holdPromptTwo is not null) && _worldIconsTwo is not null &&
			GodotObject.IsInstanceValid(_worldIconsTwo);
		if (_worldIcons.Lit > 0)
		{
			_worldIcons.Drive(GameInput.AHeld);
			if (_pooledCanvas is { } pooled)
			{
				_worldIcons.TrackProjected(pooled, splitIcons);
			}
		}

		if (iconsTwo && _worldIconsTwo!.Lit > 0)
		{
			(float fraction, bool pressed) = Fable2Bridge.GetPlayerTwoHold();
			_worldIconsTwo.Drive(pressed, fraction * 100f);
			_worldIconsTwo.TrackProjected(_pooledCanvasTwo ??
				(System.Collections.Generic.IReadOnlyDictionary<int, Vector2>)new System.Collections.Generic.Dictionary<int, Vector2>(), true);
		}
	}

	private Engine_AchievementToast? _toast;
	private Node3D? _toastMount;

	/// <summary>
	/// Stands the achievement toast up, topmost on the UI canvas.
	///
	/// THE MOUNT CONVERTS PIXELS TO CANVAS UNITS. The toast lays itself out in display PIXELS
	/// (it is handed the window as its `Canvas`) so its text is laid out at the size it is
	/// drawn, and the canvas camera draws 720 UNITS across the window height — so the wrapper's
	/// per-frame scale is the visible-extent-per-pixel ratio. The toast's own numbers are HUD
	/// canvas units, carried into those pixels by its `HudScale`.
	///
	/// Z 9000: above every presentation slab (depth × 1000) and still inside the canvas
	/// camera's near plane at 10000. The toast hides itself when nothing is queued.
	/// </summary>
	private void BuildAchievementToast()
	{
		if (_presentation is null || (_toast is not null && GodotObject.IsInstanceValid(_toast)))
		{
			return;
		}

		_toast = new Engine_AchievementToast
		{
			Name = "AchievementToast",
			Theme = _presentation.Theme,
		};

		_toastMount = new Node3D { Name = "ToastMount", Position = new Vector3(0f, 0f, 9000f) };
		_toastMount.AddChild(_toast);
		MountHudWidget(Engine_HudWidgets.AchievementToast, false, _toastMount);
	}

	/// <summary>
	/// The achievement system's frame. The guest's own answer to its logbook `Achievements` row
	/// is `XamShowAchievementsUI` (`sub_826D9620` case 150), the console's guide blade, which on
	/// this platform opens nothing.
	///
	/// The request is LOGGED, NOT ACTED ON. The achievements have their own screen now
	/// (<see cref="Engine_UiHostScreen"/>, 1013) on its own pause tab, and the logbook row that
	/// raised this is dropped from the list by its own template record — so a request reaching
	/// here at all means something OTHER than that row asked, which is worth seeing.
	/// </summary>
	private static void DriveAchievements()
	{
		Refii.Achievements.Engine_Achievements.Drive();
		int requests = Refii.Achievements.Engine_Achievements.TakeUiRequests();
		if (requests > 0)
		{
			GD.Print($"[achievements] the guest asked for the achievements UI ({requests}x) — " +
				"the list is the pause menu's Achievements tab");
		}
	}

	/// <summary>The owned spell-assignment blade and quick wheel — see
	/// <see cref="Engine_UiSpellAssignSet"/>.</summary>
	private readonly Engine_UiSpellAssignSet _spells = new();

	/// <summary>Whether a blade direction was posted last frame, so one `release` follows it.</summary>
	private bool _spellBladeHeld;

	/// <summary>The options page's second input level — see <see cref="Engine_UiScreenLevel"/>.</summary>
	private readonly Engine_UiScreenLevel _screenLevel = new();

	/// <summary>One list model per drawn presentation that has a pool.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, Engine_UiScreenMenu>
		_screenLists = new();

	/// <summary>The last "left mirrored" reason printed, so it says so once rather than per frame.</summary>
	private long? _screenMenuDeclined;

	private Engine_UiScreenMenu? _screenMenu;

	/// <summary>
	/// The latest real-estate building snapshot sampled for screen 28. Sampled once per list
	/// pass so all <c>estate.*</c> row conditions read a consistent snapshot with zero extra
	/// P/Invokes, and cleared when leaving screen 28.
	/// </summary>
	private Fable2Bridge.RealEstate _estateSnapshot;

	/// <summary>The detail page's last inputs (screen, row, page, data generation, fold) and the book they filled — a refill runs on a change or a guest frame.</summary>
	private (uint, string, int, uint, bool) _detailKey;

	private Engine_UiOwnedBook? _detailBook;

	private HostBridge.OptionSliders _detailSliders;

	private HostBridge.HeroVitals _detailVitals;

	private HostBridge.HeroDerived _detailHero;

	/// <summary>The bridge's UI data generation the open list last re-read at (<see cref="HostBridge.UiDataGeneration"/>).</summary>
	private uint _uiDataSeen;

	/// <summary>The last row this cursor marked viewed, so a resting cursor posts it once.</summary>
	private uint _viewedPosted;

	/// <summary>The screen the book page was last reset for (the stated screen id).</summary>
	private uint _pageScreen = uint.MaxValue;

	/// <summary>The menu model's rows as last reported, so the report is change-keyed on content.</summary>
	private string _menuModelHeld = string.Empty;

	/// <summary>
	/// THE ROW THE GUEST'S HANDLERS READ. Every list screen's C++ asks `sub_826C1B50` for the
	/// highlighted row's event type, id and strings; the hook answers from this statement for the
	/// current screen (gui_menu_hooks.cpp), so a handler acts on the row the player is on. Stated
	/// on change; a host-only row, a mod's row, or no Godot list, clears it and the reader answers
	/// the script's no-row ("No MenuItem", type 0, id 0). It STANDS while the list is hidden over
	/// its own screen — a confirm box, the abilities blade — because the screens' Update keeps
	/// refilling their cached rows from the reader (`sub_826D1698` at 0x826d1718 into +396) and
	/// their box answers act on that cache. A MOD'S ROW (<see cref="Engine_UiExpandableMenu.Item.FromMod"/>) is never
	/// stated whatever its JSON says: its `(EventType, id)` names no record of the guest's, and
	/// handlers drop failed lookups — the abilities edge `sub_826C3C78` ignores `sub_82896900`
	/// (0x826c3d58), the furniture upgrade `sub_8299C710` ignores `sub_825CC2F0` (0x8299caf4)
	/// and dereferences the unfilled record in `sub_827647F8` (0x82764814).
	/// </summary>
	private void StateHighlightedRow()
	{
		Engine_UiExpandableMenu.Item? row = _screenMenu is { } list &&
			_screenMenuView is { OpenedDepth: 0 }
				? list.Script.Highlighted
				: null;

		// THE ABILITIES SCREEN IS NEVER ANSWERED WITH A ROW IT HAS NO RECORD FOR. Its refresh
		// `sub_826C3C78` builds an ability record from any highlight that is not a category (301)
		// or the Spell Selector (23) and indexes by that record's unwritten Type when the id is no
		// ability (0x826C3CFC / 0x826C3D1C). A mod's or a host row is stated as the guest category
		// it hangs under; the hook answers 301 when nothing is stated at all.
		if (row is { GuestHighlightable: false } && _screenMenu is { ScreenId: Engine_UiScreenInput.AbilitiesScreenId })
		{
			Engine_UiExpandableMenu.Item? category = row.Parent;
			while (category is not null &&
				!(category.GuestHighlightable && category.EventType == (int)Engine_UiMenuConstants.EventType.Submenu))
			{
				category = category.Parent;
			}

			row = category is { Parent: not null } ? category : null;
		}

		uint screen = row is { GuestHighlightable: true } ? _screenMenu!.ScreenId : 0u;
		int id = row is null ? 0 : (row.IdHigh << 16) + row.IdLow;
		string label = row?.StatedLabel ?? row?.TextTag ?? string.Empty;
		string key = screen == 0u ? string.Empty : $"{screen}|{row!.Name}|{row.EventType}|{id}|{label}";
		if (key == _statedRowKey)
		{
			return;
		}

		_statedRowKey = key;
		Engine_UiSource.Current.StateHighlightedRow(
			screen, row?.EventType ?? 0, id, screen == 0u ? string.Empty : row!.Name, screen == 0u ? string.Empty : label);
	}

	private string _statedRowKey = string.Empty;

	/// <summary>The in-game fold last stated: the screen and the depth.</summary>
	private (uint Screen, uint Depth) _statedFold;

	/// <summary>The front end's fold last stated.</summary>
	private uint _statedFrontEndFold;

	/// <summary>
	/// THE FOLD THE GUEST'S ROOT TEST READS. `sub_826C1D98` (`IsHighlightedItemAtRoot`) is answered
	/// from this depth for the current screen (gui_menu_hooks.cpp): the back arm `sub_826D5AF0`
	/// hands the screen to the pause root at depth 0 and otherwise closes the fold, and the A arm
	/// `sub_826D58F0` plays its select cue below the root. Stated on change; a list that goes away
	/// states depth 0, the script's own answer with no row.
	/// </summary>
	private void StateScreenListFold()
	{
		(uint Screen, uint Depth) fold =
			_screenMenu is { } list && _screenMenuView is { Visible: true } view
				? (list.ScreenId, (uint)System.Math.Max(0, view.Depth))
				: (0u, 0u);
		if (fold == _statedFold)
		{
			return;
		}

		if (_statedFold.Screen != fold.Screen && _statedFold.Depth != 0)
		{
			Fable2UiSource.Active.StateListFoldDepth(_statedFold.Screen, 0);
		}

		_statedFold = fold;
		Fable2UiSource.Active.StateListFoldDepth(fold.Screen, fold.Depth);
	}

	/// <summary>
	/// The front end's main menu's fold, for `sub_826DED60` — the root test of
	/// `lhGui_FrontendRoot_OnScreenAction`'s B (at root it leaves for the title, else `OnPressB`).
	/// </summary>
	private void StateFrontEndFold(bool shown)
	{
		uint depth = shown && _menuView is { } view ? (uint)System.Math.Max(0, view.Depth) : 0u;
		if (depth == _statedFrontEndFold)
		{
			return;
		}

		_statedFrontEndFold = depth;
		Fable2UiSource.Active.StateListFoldDepth(HostBridge.FrontEndMainMenuList, depth);
	}

	private Engine_FrontEndMenuView? _screenMenuView;

	/// <summary>The guest's screen fade, drawn over the UI. See <see cref="Engine_UiScreenFade"/>.</summary>
	private Engine_UiScreenFade? _fade;

	private uint _pumpQueued = uint.MaxValue;

	private double _pumpPoll;

	private ulong _pumpTicksAtReport;

	/// <summary>
	/// The tick pump's own three counters, whenever the queue moves.
	///
	/// THE EXPORT HAS EXISTED SINCE ABI 35 AND NOTHING EVER READ IT, which cost this project
	/// several sessions: a press the pump never drained and a press the guest received and
	/// ignored are indistinguishable from Godot's side, and both were guessed at more than once.
	/// The three numbers fail differently and that is the point — ticks not climbing is a hook
	/// that stopped; ticks climbing while `drained` does not is a drain that is refusing;
	/// a queue that only grows is neither of those, it is work being posted into a void.
	///
	/// Keyed on the QUEUE moving, so it is one line per post and per drain rather than per
	/// frame, and it carries the tick delta since the last line so a stalled hook is visible
	/// without needing a second report to compare against.
	/// </summary>
	private void ReportPumpHealth(double delta)
	{
		// NOT EVERY FRAME. Reading the counters takes the pump's OWN mutex — the same one
		// `PostGuestTickAction` and the drain take — so polling it at the display rate puts a
		// hundred-plus needless lock acquisitions a second on the guest tick thread's path. The
		// queue only moves when a button is pressed or a drain runs, so ten times a second
		// misses nothing and costs nothing.
		_pumpPoll += delta;
		if (_pumpPoll < 2.0)
		{
			return;
		}

		_pumpPoll = 0.0;

		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		(ulong ticks, ulong drained, uint queued) = HostBridge.GuestTickPump();
		if (queued == _pumpQueued)
		{
			return;
		}

		ulong since = ticks - _pumpTicksAtReport;
		_pumpQueued = queued;
		_pumpTicksAtReport = ticks;

		GD.Print($"[guest-pump] queued {queued}, drained {drained}, ticks {ticks} " +
			$"(+{since} since the last change)");
	}
	private uint _screenMenuAddress;
	private uint _screenMenuScreen = uint.MaxValue;
	/// <summary>The expression-wheel sector last posted, so the cursor lever fires on change.</summary>
	private int _wheelSector = -1;

	/// <summary>
	/// Which way the stick was last seen on an item-book screen, so a page turn fires on the
	/// EDGE. Reset with the screen, not with the press.
	/// </summary>
	private int _screenPageHeld;

	/// <summary>`Foreground.ExpandableMenu`'s ordinal and authored x in the adopted screen.</summary>
	private int _screenMenuFollowOrdinal = -1;

	private float _screenMenuFollowAuthoredX;

	/// <summary>`ExpandableMenu.Buttons`'s ordinal — the focus dim's element.</summary>
	private int _screenMenuButtonsOrdinal = -1;

	private Vector3 _screenMenuAnchor = new(
		Engine_FrontEndMenuView.CanvasAnchor.X, Engine_FrontEndMenuView.CanvasAnchor.Y, 0f);

	/// <summary>
	/// One frame of the owned pause menu. True when it consumed the input.
	///
	/// STANDS IN ONLY ON THE PAUSE ROOT — screen id 0 with no modal pending, the same window
	/// the bridge accepts a tab select in and the same gate the input mask uses. A tab screen,
	/// a query box, or the resume all leave that window, and the view yields the frame the
	/// guest says so.
	/// </summary>
	private bool DrivePauseMenu(double delta)
	{
		// A pattern-book retire (a mod remount) frees the view, and the guest rebuilds
		// `pausemainmenu` only on its next state change: a tree still standing is adopted again
		// here, or Start would pause with no list and this returning false would read as nothing
		// wrong. Once per retire — the adoption stands the view back up.
		if (_pauseView is null && _presentation is not null && _pauseAddress != 0 &&
			_drawn.TryGetValue(_pauseAddress, out Engine_UiDrawnPresentation? standingTree) &&
			standingTree.Name == PausePresentation && GodotObject.IsInstanceValid(standingTree.Node))
		{
			GD.Print("[game-ui] pause list not standing: rebuilt after retire");
			AdoptPauseMenu(standingTree.Node, _pauseAddress);
		}

		if (_pauseView is null || _pauseMenu is null)
		{
			return false;
		}

		Engine_UiModel.PauseState? pause = HostBridge.GetPauseState();
		bool standing = _pauseTreeUp && IsRevealed && pause is { RootActive: true };

		// The LIST stays on screen under a query box — the original dims it behind the paper,
		// it does not vanish — so visibility follows the SCREEN and only input follows the
		// modal. ScreenId stays 0 under a box (the opener restores it).
		bool onRoot = _pauseTreeUp && IsRevealed && pause is { ScreenId: 0 };
		_pauseView.Visible = onRoot;

		// A mod's menu stands over the pause ROOT only: a tab push, a resume or a quit drops it.
		if (!onRoot)
		{
			_pauseView.CloseMenus();
		}

		DrivePauseBanner(pause);

		// THE OWNED BOOK DRAWS THE ROOT, on the file and what the guest tells the headless
		// presentation; the tokens and the quest markers are projected here.
		if (_pauseBook is not null && IsInstanceValid(_pauseBook))
		{
			_pauseBook.Visible = onRoot;
			if (_drawn.TryGetValue(_pauseAddress, out Engine_UiDrawnPresentation? pauseDrawn))
			{
				if (pauseDrawn.Node is { } mirrored && IsInstanceValid(mirrored))
				{
					mirrored.Visible = false;  // never the guest's tree — see Engine_UiBookSet
				}

				if (onRoot)
				{
					// THE PAGE IS THE GUEST'S STATEMENTS — the readouts, the quest lines, the map
					// image and its turn, every show and hide. The legend's rows are SetMapKey's
					// (`@PauseMap.Key`, Engine_UiPauseMapKey). The hero's and dog's tokens and the
					// projection of the guest's quest markers are Engine_UiPauseMinimap's.
					Engine_UiPauseMinimap.Statements minimap = Engine_UiPauseMinimap.State(
						_pauseBook.AuthoredPosition(Engine_UiPauseMinimap.MapImagePath),
						HostBridge.GetMapMarkers());
					_pauseBook.HostValues = _pauseMapKey.Texts(_pauseAddress);
					_pauseBook.HostTextures = _pauseMapKey.Textures(_pauseAddress);
					_pauseBook.HostPositions = minimap.Positions;
					_pauseBook.HostRotations = minimap.Rotations;
					_pauseBook.HostActive = minimap.Active;

					// The guest's slide statements reach the book by this address — the root's
					// `Static` among them (`sub_826A23F8`, `sub_82426CA8(root, 3)`). The book
					// stands on its file and those statements.
					_pauseBook.PresentationAddress = _pauseAddress;
					_pauseBook.SetRest();
					_pauseBook.SetMarkers(minimap.Markers, Engine_UiPauseMinimap.MiniMapPath,
						Engine_UiPauseMinimap.MarkerDepth);
				}
			}
		}

		// THE MULTIPLAYER BOOK STANDS OVER THE PAUSE LIST and takes the frame whole, as a host
		// screen does (Engine_UiHostScreen, below). Driven here, before the list, so its B closes
		// the screen rather than collapsing a fold behind it.
		//
		// It has no guest state to survive on, so it is dropped the moment the pause root is
		// not what the guest says is up: a tab push, a query box or the resume all close it,
		// the same window the list itself yields in.
		// IN GAME ONLY. After a game this drive keeps running on the FRONT END (the pause view
		// exists), and "the pause root is not standing" was closing the multiplayer book the
		// frame the title's own Multiplayer row opened it — leaving the front-end list hidden
		// and the screen blank. On the front end the book belongs to the front-end drive.
		if (OpenListBook is { } standingBook && HostBridge.IsInGame)
		{
			if (!standing)
			{
				standingBook.Close();
			}
			else
			{
				_pauseView.Visible = false;
				Fable2Input.PauseMenuSwallowsBack = true;

				float slab =
					_drawn.TryGetValue(_pauseAddress, out Engine_UiDrawnPresentation? behind) &&
					IsInstanceValid(behind.Node)
						? behind.Node.Position.Z
						: 0f;

				// Y REFRESHES the open page — MenuFlipPressed is Y, already plumbed.
				PointerPage(standingBook.Book);
				if (standingBook.Drive(
						Fable2Input.MenuDirection,
						Fable2Input.MenuAcceptPressed || PointerList(standingBook.View),
						Fable2Input.MenuBackPressed,
						Fable2Input.MenuFlipPressed, delta, slab))
				{
					ReportMenuView(standingBook.View);
				}

				return true;
			}
		}

		// The nav mask keys on the guest's own pause state inside GameInput; only the B
		// swallow is ours to declare, and it is re-derived every frame so it can never
		// outlive the expansion that justified it.
		Fable2Input.PauseMenuSwallowsBack = standing &&
			(_pauseView.Menu?.CanCollapse == true || _pauseView.OpenedDepth > 0);

		if (!standing)
		{
			// Said once per change, with the three parts: which of them fell is the whole
			// question when a press on this list goes nowhere.
			//
			// Key first, string only on a change: the three parts pack into three bits, and
			// ordinary play takes this branch every frame.
			int why = (_pauseTreeUp ? 1 : 0) | (IsRevealed ? 2 : 0) |
				(pause is { RootActive: true } ? 4 : 0);
			if (_pauseStandingSaid != why)
			{
				_pauseStandingSaid = why;
				GD.Print($"[game-ui] pause list not standing — tree {_pauseTreeUp} " +
					$"revealed {IsRevealed} root {(pause is { RootActive: true })}");
			}

			return false;
		}

		_pauseStandingSaid = -1;

		// THE GUEST'S OWN AVAILABILITY, followed while the list is up. Its LB/RB nav skips an
		// unavailable tab per frame (`sub_826B83D0` — the childhood restriction on the hero),
		// and its push lever never re-checks, so the ghosting is ours to keep current.
		uint tabs = Fable2Bridge.PauseTabsAvailable();

		// THE HENCHMAN MASK DOES NOT APPLY TO A FULL HERO. On a joiner the guest's mask is the
		// henchman root's — it ghosts Abilities, Weapons and Clothing — and this list is the
		// co-op variant that owns those rows outright, so every tab is offered.
		if (CoopSession.IsClient)
		{
			tabs = 0xFFFFFFFFu;
		}

		// Fable 3's Kick: the online host's, while a partner stands (game+0x11C == 1 in the guest).
		bool kick = CoopSession.Side == CoopSession.Role.Host && CoopSession.PeerStanding &&
			!CoopSession.LocalCoop;
		if (_pauseMenu.SetAvailable(Engine_PauseMenu.KickRow, kick))
		{
			_pauseView.Refresh();
		}

		if (tabs != _pauseTabsMask)
		{
			_pauseTabsMask = tabs;
			if (Engine_PauseMenu.ApplyTabAvailability(_pauseMenu, tabs))
			{
				_pauseView.Refresh();
				GD.Print($"[game-ui] pause tabs available: 0x{tabs:X} " +
					$"({_pauseMenu.Visible.Count} entries offered)");
			}
		}

		// The slide sound rides the view's own move (Engine_FrontEndMenuView.Moved).
		_pauseView.Navigate(Fable2Input.MenuDirection, delta);
		bool pauseClicked = PointerList(_pauseView);

		// Change-keyed, so this is one line when the list settles and one more per press —
		// not a line per frame. It is the only report that walks the ROWS.
		ReportMenuView(_pauseView);
		string pauseRoot = Engine_PauseMenu.RootTemplate;
		Engine_UiModMenus.Focus(_pauseView, pauseRoot, Engine_UiCommandList.Pause);
		if (standing && DriveRowActionsOn(_pauseView.OpenedMenuName ?? pauseRoot, _pauseView.Menu?.Highlighted,
				_pauseView, 0u, Engine_UiCommandList.Pause))
		{
			return true;
		}

		if (Fable2Input.MenuBackPressed)
		{
			// Collapse, then close a mod's menu. With neither B is left through to the guest,
			// whose own B on the pause root resumes the game — that path is entirely the guest's.
			// A level closed below the root is the shared cancel (`SE_GUI_MENU_BOX_CANCEL`,
			// 0x826D5BBC).
			if (_pauseView.Back())
			{
				Cue(frontEnd: false, "cancel");
			}
			else
			{
				Engine_UiModMenus.Back(_pauseView);
			}
		}

		if (!Fable2Input.MenuAcceptPressed && !pauseClicked)
		{
			return true;
		}

		Engine_UiExpandableMenu.Item? pauseChosen = _pauseView.Menu?.Highlighted;
		string? action = _pauseView.Activate();
		if (Engine_UiModMenus.Activated(_pauseView, pauseRoot, pauseChosen, action, Engine_UiCommandList.Pause))
		{
			return true;
		}

		// EVERY PRESS SAYS WHAT IT DID. A run pressed A on 'Multiplayer' on both instances and
		// the log held one line for the whole session — there was no way to tell a press the
		// gate above swallowed from one Activate answered null on.
		GD.Print($"[game-ui] pause A on '{_pauseMenu.Highlighted?.Name ?? "(none)"}' → " +
			(action is null ? "null (group toggle / opens / no highlight)" : $"'{action}'"));
		if (action is null)
		{
			// Expanded or collapsed a group — complete here.
			return true;
		}

		// ANY OTHER CHOICE CANCELS A PENDING QUIT. The guest returns a refused box to this list,
		// so the next row picked IS the refusal — otherwise the latch would outlive it and turn
		// a later "quit to title" into an exit. A quit row re-arms it as it runs.
		if (_quitToDesktopPending)
		{
			_quitToDesktopPending = false;
			GD.Print("[game-ui] a pending quit to the desktop was cancelled — the pause list " +
				"took another choice, so the guest's box was answered 'no'");
		}

		// THE ONE DISPATCHER: the pause levers (GameUiCommands), a host screen (`Screen:open:`), a
		// host action or a mod's verb — whatever the row names. A host screen opened here stands
		// over this list; nothing is posted to the guest, whose pause root stays where it is.
		string pauseRow = _pauseMenu.Highlighted?.Name ?? string.Empty;
		Engine_UiCommandResult ran = Engine_UiCommands.Run(action, new Engine_UiCommandContext(
			Engine_UiCommandList.Pause, pauseRoot, pauseRow, Engine_UiModMenus.MenuPlayer,
			Engine_UiTemplates.MenuOwner(pauseRoot, pauseRow)) { View = _pauseView });
		if (ran.Outcome == Engine_UiCommandOutcome.NotOurs)
		{
			GD.Print($"[game-ui] pause entry '{action}' names no verb; nothing runs it");
		}

		return true;
	}

	/// <summary>
	/// The anchor each preview screen hangs its object on — the guest's own dotted paths.
	/// </summary>
	private static string PreviewAnchor(uint screenId) =>
		Engine_UiTemplates.Screen(screenId) is { PreviewAnchor.Length: > 0 } template
			? template.PreviewAnchor
			: "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model";

	/// <summary>
	/// Draws the 3D object a menu panel shows — the item in the ring that has been drawing
	/// empty.
	///
	/// THE GUEST NAMES THE ITEM AND WE DRAW IT: its preview set states the record
	/// (Engine_UiPreviewItems), whose model and artist's offsets are shipped data, and the anchor
	/// is a real element of the drawn screen, so parenting the mesh there puts it in the panel's own space with
	/// the panel's own camera — no world placement to reproduce and no render target.
	///
	/// The SPIN rides in for free: the guest writes it as `rotation.x/y/z` on that anchor
	/// element every frame, so the live-transform read has already applied it to the node we
	/// parent to.
	/// </summary>
	/// <summary>The key over the last preview-gate line; null before the first one.</summary>
	private long? _previewGate;

	/// <summary>The clothing book (`equipmentscreen.bgf`), the one item book that dresses a mannequin.</summary>
	private const uint EquipmentScreenId = 16;

	/// <summary>
	/// AN OUTFIT IS SHOWN ON THE MANNEQUIN. Screen 16 spawns a `CreatureHeroMannequin*` at open,
	/// cloned from the hero in the hero's clothes, and the original's ring never shows anything
	/// else; an outfit has no model of its own (most state none, the DLC ones a single piece).
	/// The highlighted outfit is tried on it through the pump (`Inventory:tryon`, the mechanism
	/// the guest's own dye mode uses on highlight), and put back on leaving the outfit rows.
	/// Single garments keep the item-alone view. Null when the row is not an outfit.
	/// </summary>
	private Fable2Bridge.UiPreviewObject? OutfitTryOn(Engine_UiExpandableMenu.Item? row)
	{
		const string outfit = Engine_UiInventorySource.InventoryAction + "outfit:";
		uint record = row?.Command is { } command &&
			command.StartsWith(outfit, System.StringComparison.Ordinal) &&
			uint.TryParse(command[outfit.Length..], out uint parsed)
				? parsed
				: 0u;

		// Keyed on the BODY too: the screen spawns a fresh mannequin, in the hero's clothes, every
		// time it opens, and the first outfit highlighted after a reopen must still be tried on.
		uint body = Engine_UiPreviewFigures.Entity(Engine_UiPreviewFigures.Clothing);
		if (body != 0u && (record != _triedOn || body != _triedOnBody))
		{
			HostBridge.PostFrontEndAction($"{Engine_UiInventorySource.InventoryAction}tryon:{record}");
		}

		_triedOn = record;
		_triedOnBody = body;
		if (record == 0u || body == 0u)
		{
			return null;
		}

		uint mannequin = body;

		// Turned a quarter to face the viewer, as the co-op body is: the composed body's
		// forward is +X. The scale is the panel's fit of a standing figure.
		return new Fable2Bridge.UiPreviewObject(
			EquipmentScreenId, 0u, mannequin, Vector3.Zero,
			new Vector3(0f, Mathf.Pi / 2f, 0f), MannequinScale, 0);
	}

	/// <summary>The outfit record last tried on the mannequin; 0 = the hero's own clothes.</summary>
	private uint _triedOn;

	/// <summary>The mannequin that try-on was posted for.</summary>
	private uint _triedOnBody;

	/// <summary>The co-op page's figure scale (`(2.1 / body) * 350` canvas units, fitted).</summary>
	private const float MannequinScale = 121f;

	private void DrivePreviewObject()
	{
		// THIS ONE LIVES INSIDE THE BUILT TREE ON PURPOSE — it hangs off the anchor element so
		// the guest's per-frame `rotation.x/y/z` on that anchor spins it for free. The cost is
		// that a rebuild frees it under us, so the reference must be re-validated rather than
		// trusted; the owned panel threw once a frame for exactly this reason before it was
		// moved to the host. Dropping it here simply rebuilds it on the next frame.
		if (_previewObject is not null && !GodotObject.IsInstanceValid(_previewObject))
		{
			_previewObject = null;
		}

		// THE ITEM THE GUEST'S PREVIEW WAS SET TO FIRST, THEN THE ROW'S OWN RECORD.
		//
		// The guest's page fills set the preview for the row its highlight reader answers —
		// Godot's — and the receive-items card for the item it received; the set states the
		// record per screen until that screen's hide (Engine_UiPreviewItems). A screen whose
		// preview the guest has not stated resolves the row the player is on.
		if (!HostBridge.IsInGame)
		{
			Engine_UiPreviewItems.Reset();
		}

		uint statedScreen = IsRevealed && HostBridge.GetPauseState() is { } pause ? pause.ScreenId : 0u;
		uint statedRecord = Engine_UiPreviewItems.Record(statedScreen);
		Fable2Bridge.UiPreviewObject? spinning = statedRecord != 0u
			? Engine_UiItemPage.PreviewOfRecord(statedScreen, statedRecord)
			: null;
		if (spinning is null && IsRevealed && _screenMenu is { } menu)
		{
			// THE SCREEN THAT IS UP, not the list's own id: the item books' rows ride in under
			// `capture:Root`, whose list answers screen 0, so a gate on the list's id never saw
			// a book. The pause state names the screen; the list only supplies the cursor.
			uint up = HostBridge.GetPauseState() is { ScreenId: not 0 } state
				? state.ScreenId
				: menu.ScreenId;
			// Key first, string only on a change; runs every frame a screen list stands. The
			// provider is looked up once and reused below. See Engine_HudValues.DescribeKey.
			string? provider = Engine_UiTemplates.Screen(up)?.Provider;
			string? row = menu.Script.Highlighted?.Name;
			long gate = 17L;
			gate = (gate * 1000003L) ^ up;
			gate = (gate * 1000003L) ^ menu.ScreenId;
			gate = (gate * 1000003L) ^ (provider?.GetHashCode() ?? 0);
			gate = (gate * 1000003L) ^ (row?.GetHashCode() ?? 0);
			if (_previewGate != gate)
			{
				_previewGate = gate;
				GD.Print($"[game-ui] preview gate: screen {up} (list {menu.ScreenId}) " +
					$"provider '{provider}' row '{row}'");
			}

			if (up == EquipmentScreenId)
			{
				spinning = OutfitTryOn(menu.Script.Highlighted);
			}

			if (spinning is null && provider == Fable2PageProviders.ItemBook)
			{
				spinning = Engine_UiItemPage.Preview(up, menu.Script.Highlighted);
			}
		}

		if (spinning is not { } preview)
		{
			if (_previewObject is not null)
			{
				_previewObject.Visible = false;
			}

			// THE SILENT CASE, said out loud once. The bridge answers nothing both when no
			// screen with a preview is up (the normal state) and when it failed to reach the
			// handler, and until this line the two were the same empty log.
			if (IsRevealed && HostBridge.IsInGame &&
				HostBridge.GetPauseState() is { ScreenId: not 0 } up && _previewNone != up.ScreenId)
			{
				_previewNone = up.ScreenId;
				GD.Print($"[game-ui] screen {up.ScreenId} is up and the bridge reports no " +
					"preview object — either the screen has none or its handler was not reached");
			}

			return;
		}

		_previewNone = null;

		// The anchor lives in whichever mirrored screen is drawn; without it there is nowhere
		// to put the object, so the panel stays empty rather than the model floating.
		//
		// RESOLVED THE ENGINE'S WAY, not as a Godot node path. `getElement('Foreground.Book…')`
		// searches for the first segment and walks the rest as children, and a built
		// presentation wraps the artist's tree in the component and layer nodes the file
		// declares — `Component_0/Layer/Foreground/…`. A rigid relative path from the
		// presentation root therefore matched NOTHING, which is why this returned silently on
		// every screen that has a preview and the ring drew empty.
		// THE GUEST'S OWN PRESENTATION FOR THIS SCREEN FIRST. Every in-game book screen has an
		// anchor at the same authored path, so a search over the drawn set finds the previous
		// screen's — and hangs the model in a panel the player is not looking at.
		string path = PreviewAnchor(preview.ScreenId);
		Node3D? anchor = null;
		uint owner = PresentationFor(preview.ScreenId);

		// THE SCREEN'S OWN BOOK FIRST, when it has one. A book that is standing has taken its
		// mirrored tree off screen, and hanging the model in a hidden tree hides the model with
		// it — the panel would be empty for no visible reason. The book's clones carry the same
		// authored paths, so the anchor is the same name in a different tree.
		if (_books.Book(preview.ScreenId) is { } book &&
			IsInstanceValid(book) && book.Visible &&
			FindAnchor(book, path) is { } bookAnchor)
		{
			anchor = bookAnchor;
		}

		if (anchor is null && owner != 0 && _drawn.TryGetValue(owner, out Engine_UiDrawnPresentation? owned) &&
			Engine_UiTheme.FindByPath(owned.Node, path) is { } ownedAnchor)
		{
			anchor = ownedAnchor;
		}

		foreach (var (address, drawn) in _drawn)
		{
			if (anchor is not null)
			{
				break;
			}

			if (FindAnchor(drawn.Node, path) is { } found)
			{
				anchor = found;
				break;
			}
		}

		// AN OWNED SCREEN HAS NO DRAWN PRESENTATION, which is why screen 8's preview reported
		// its anchor "in none of the drawn presentation(s)" every time a gift arrived. The
		// received-item card is built from the PATTERN BOOK rather than mirrored, so
		// `Foreground.SpinningCube.Cube` lives inside the owned view and the search over
		// `_drawn` can never reach it. Every screen we take over moves its anchors out of that
		// collection, so the owned views have to be searched too.
		if (anchor is null && _surfaces.ReceiveItems.View is { } card &&
			GodotObject.IsInstanceValid(card))
		{
			anchor = FindAnchor(card, path);
		}

		if (anchor is null)
		{
			if (_previewObject is not null)
			{
				_previewObject.Visible = false;
			}

			// Named once per change: the guest states a model and we have nowhere to hang it,
			// which is a different failure from the guest naming none — and the two are
			// indistinguishable on screen.
			// The pair keyed rather than spelled: a screen with a preview and no anchor takes
			// this branch every frame it stands.
			long missing = ((long)preview.ScreenId << 32) | preview.ModelHash;
			if (_previewAnchorMissing != missing)
			{
				_previewAnchorMissing = missing;
				GD.Print($"[game-ui] preview object on screen {preview.ScreenId} names " +
					$"0x{preview.ModelHash:X8} but '{path}' is in none of the " +
					$"{_drawn.Count} drawn presentation(s) nor the owned views — " +
					"panel left empty");
			}

			return;
		}

		_previewAnchorMissing = null;

		_previewObject ??= new Engine_UiPreviewObject { Name = "PreviewObject" };
		if (_previewObject.GetParent() != anchor)
		{
			_previewObject.GetParent()?.RemoveChild(_previewObject);
			anchor.AddChild(_previewObject);
		}

		// THE ARTIST'S PANEL, so the object cannot paint over the screen it sits in. The ring
		// is a real quad in the anchor's own ancestry — `ModelHider` on the book screens,
		// `SpinningCube` on the card — and its canvas extent is what the model is drawn to fit.
		_previewObject.PanelSize = PanelExtent(anchor);

		// THE EQUIPMENT SCREEN'S OFFSET IS A MANNEQUIN OFFSET. On screen 16 the guest shows the
		// hero WEARING the item (`TypeSpecific`'s Hero slides, `sub_826D6C90`), and a garment's
		// `GUIScreenTranslationOffset` is where it sits on that standing body — the soldier
		// helmet states (0, -9.6, -559.7), 560 canvas units below the anchor. The remaster
		// shows the item alone in the ring (the provider states the Item slide), so the
		// offset is dropped there and the item hangs at the anchor.
		Vector3 translation = preview.ScreenId == EquipmentScreenId
			? Vector3.Zero
			: preview.Translation;

		uint was = _previewObject.ModelHash;
		bool live = preview.ScreenId == EquipmentScreenId && preview.ModelHash == 0u && preview.Entity != 0u;
		// Hung by its origin wherever the guest's own placement stands (`sub_828C3C78` reads no
		// bounds); centred where the placement is ours — the live body, and screen 16's
		// item-alone view, whose stated offset was dropped above.
		var placement = new Engine_UiPreviewObject.Placement(
			translation, preview.Rotation, preview.Scale, preview.RotationAxis,
			Hung: !live && preview.ScreenId != EquipmentScreenId);
		bool changed = live ? _previewLiveShown != preview.Entity : was != preview.ModelHash;
		bool shown;
		if (live)
		{
			// The body comes from the entity feed's node for that address — GameEntities lives
			// under GameWorld, a sibling of this UI under GameWindow. Its mesh is read, never
			// re-parented: the feed still owns and drives the node it built.
			MeshInstance3D? feedNode = GetParent()?.GetNodeOrNull<GameWorld>("GameWorld")?.Entities?
				.NodeFor(preview.Entity);
			Mesh? body = feedNode?.Mesh;
			Skeleton3D? rig = feedNode is not null && !feedNode.Skeleton.IsEmpty
				? feedNode.GetNodeOrNull<Skeleton3D>(feedNode.Skeleton)
				: null;
			shown = _previewObject.ShowLive(body, preview.Entity, placement, rig, feedNode?.Skin);
			_previewLiveShown = shown ? preview.Entity : 0u;
		}
		else
		{
			_previewLiveShown = 0u;
			// A dyed garment shows dyed: the guest's own dye builder states the item's layers.
			shown = _previewObject.Show(
				preview.ModelHash, placement,
				preview.Item != 0u ? Fable2Bridge.GetItemDyeLayers(preview.Item, out _) : null);
		}

		if (changed)
		{
			GD.Print($"[game-ui] preview object on screen {preview.ScreenId}: " +
				(live ? $"live entity 0x{preview.Entity:X8}" : $"0x{preview.ModelHash:X8}") +
				$" — {_previewObject.LastReport}" +
				$"{(shown ? $"; {_previewObject.Measurement}" : " (panel left empty)")}");
		}

		// The turn is handed to the preview as the spinner's `rotation.y`; how that lands —
		// the axis swizzle and the order against the record's offset — is the preview's
		// (`Engine_UiPreviewObject.Stated`, from `sub_828C3C78`).
		//
		// THE PLAYER TURNS IT. The guest's spinner (`sub_828C37C8`) free-runs 0.01 rad per
		// GUI tick and, on the stick (event 27, `sub_828C3910`), moves its target by
		// -0.08·x³ and pauses the free spin for 3 s, easing at 4·dt. Here the free spin is
		// dropped — the stick alone turns the model, with the guest's own cubic response and
		// easing — and a new model starts from its record's pose. Read only while the model is
		// on screen, so the page-scroll stick on the other page is untouched.
		if (changed)
		{
			// From the record's own pose: `GUIScreenRotationOffset` is the artist's facing, and
			// the guest's spinner starts from zero over it.
			_previewYaw = PreviewStartYaw;
			_previewYawTarget = PreviewStartYaw;
		}

		if (shown && _previewObject.IsVisibleInTree())
		{
			float step = (float)GetProcessDeltaTime();
			float x = GameInput.RightStickX;
			_previewYawTarget += x * x * x * PreviewStickDegreesPerSecond * step;

			// THE MOUSE TURNS IT TOO: a press anywhere in its ring grabs it and the drag turns
			// it, a degree per canvas unit, for as long as the button is held.
			if (Engine_UiPointer.Canvas is { } turnAt)
			{
				if (_previewDragX is { } fromX)
				{
					if (Input.IsMouseButtonPressed(MouseButton.Left))
					{
						_previewYawTarget += turnAt.X - fromX;
						_previewDragX = turnAt.X;
					}
					else
					{
						_previewDragX = null;
					}
				}
				else if (Engine_UiPointer.LeftPressed &&
					(PreviewRingHas(turnAt) ||
						(_previewObject.CanvasBounds() is { } model &&
							model.Grow(20f).HasPoint(turnAt))))
				{
					Engine_UiPointer.TakeLeft();
					_previewDragX = turnAt.X;
				}
			}
			_previewYaw += (_previewYawTarget - _previewYaw) * Mathf.Min(1f, PreviewEaseRate * step);
			_previewObject.Spin(_previewYaw);

			if (x != 0f && Time.GetTicksMsec() - _previewYawSaid > 1000)
			{
				_previewYawSaid = Time.GetTicksMsec();
				GD.Print($"[game-ui] preview spin: stick {x:F2} target {_previewYawTarget:F0}° " +
					$"yaw {_previewYaw:F0}° step {step * 1000f:F0}ms");
			}
		}
	}

	private ulong _previewYawSaid;

	/// <summary>Where the drag turning the preview was last frame, in canvas X; null when none.</summary>
	private float? _previewDragX;

	private const float PreviewStartYaw = 0f;

	private float _previewYaw;

	private float _previewYawTarget;

	/// <summary>0.08 rad per 30 Hz GUI tick at full deflection, in degrees per second.</summary>
	private const float PreviewStickDegreesPerSecond = 0.08f * 30f * 180f / Mathf.Pi;

	/// <summary>`flt_8209B46C` = 4.0, the spinner's easing rate per second.</summary>
	private const float PreviewEaseRate = 4f;

	/// <summary>
	/// The canvas extent of the authored frame an anchor hangs inside, or 0 when it has none.
	///
	/// Walks UP from the anchor and takes the largest quad found beside it on the way — the
	/// ring, the window, the card back. A quad's canvas size is its accumulated basis scale
	/// times the builder's quad base size, which is the same arithmetic the shader's own
	/// detail mapping does. Stops at the first ancestor that has one, so a screen-wide
	/// backdrop further up can never become the "panel".
	/// </summary>
	private static float PanelExtent(Node3D anchor)
	{
		for (Node? at = anchor.GetParent(); at is Node3D; at = at.GetParent())
		{
			float largest = 0f;
			Measure(at, ref largest);
			if (largest > 0f)
			{
				return largest;
			}
		}

		return 0f;

		static void Measure(Node node, ref float largest)
		{
			if (node is MeshInstance3D mesh && Engine_UiSceneBuilder.DescribeQuad(mesh).Painted)
			{
				Vector3 scale = mesh.GlobalTransform.Basis.Scale;
				float size = Mathf.Max(scale.X, scale.Y) * Engine_UiSceneBuilder.QuadBaseSize;
				if (size > largest)
				{
					largest = size;
				}
			}

			foreach (Node child in node.GetChildren())
			{
				Measure(child, ref largest);
			}
		}
	}

	private Engine_UiPreviewObject? _previewObject;

	/// <summary>The screen and model whose anchor was last reported missing, to print once.</summary>
	private long? _previewAnchorMissing;

	/// <summary>The screen last reported as answering no preview object, to print once.</summary>
	private uint? _previewNone;

	/// <summary>
	/// Applies the guest's OWN spawn conditions to our list.
	///
	/// The one thing a host-authored menu genuinely cannot invent. "Load Game" exists only when
	/// signed in with at least one save and "Language" only above one installed language — the
	/// guest decides both, and an entry we offer that it will not honour is worse than a
	/// missing one, because it looks like the game is broken rather than like the option is
	/// unavailable.
	/// </summary>
	private void ApplyMenuAvailability()
	{
		if (_menu is null || !HostBridge.FrontEndGuiIsUp)
		{
			return;
		}

		// Load Game's guard is `sub_82443308`, a live enumeration: its count comes through the
		// tick pump (GetSaveState). The Language entry is spawned above one installed language,
		// the count of the guest's own language table.
		System.Collections.Generic.List<Engine_UiModel.Language> languages = HostBridge.GetLanguages();
		Fable2FrontEndRoot.ApplyAvailability(_menu, languages.Count, HostBridge.GetSaveState(),
			languages: languages);

		// HERE, not only at view creation: the pump's first subtitles sample lands on its own
		// schedule, and a marker applied once from an unsampled bridge stays blank for the
		// whole session. This path already runs on every front-end state change for exactly
		// this class of late answer.
		ApplySubtitlesEmphasis();
		_menuView?.Refresh();
	}

	/// <summary>
	/// Drives the Godot-owned menu from the Godot-owned input.
	///
	/// Per frame rather than event-driven because the auto-repeat curve is a TIMING model —
	/// the model wants the held direction and a delta and owns the rest of it. Reading discrete
	/// key events here instead would move the repeat timing into this class, which is the one
	/// piece of the feel that was read out of shipped Lua rather than chosen.
	/// </summary>
	/// <summary>
	/// How long an armed quit waits for the guest to reach the front end before disarming.
	/// </summary>
	private const double QuitToDesktopWindowSeconds = 120.0;

	/// <summary>
	/// A pause row asked to leave for the desktop and the guest is still saving and tearing the
	/// world down. See the latch at the top of <see cref="ui_processBody"/>.
	/// </summary>
	private bool _quitToDesktopPending;

	/// <summary>Seconds left on that latch.</summary>
	private double _quitToDesktopWindow;

	/// <summary>Set when the page path above already filled a book this frame.</summary>
	private bool _providerFilledThisFrame;

	/// <summary>The screen whose book the list path last filled, 0 when none.</summary>
	private uint _providerFilledScreen;

	/// <summary>
	/// EVERY OWNED SCREEN WITH A PROVIDER GETS ITS CHANNELS, not only one whose provider fills a
	/// PAGE.
	///
	/// The page path is gated on `ScreenPageOpen`, so a screen that declares a provider and has
	/// no page element never reached it. The co-op screen is exactly that: it named
	/// `Fable2PageProviders.Coop` and was never once asked for a value, which is why its
	/// profile block stayed dark and its X prompt kept the shipped caption while the sliders
	/// beside them looked right — those were the GUEST still writing them, not us.
	///
	/// Stands down when the page path already filled a book this frame, so nothing here can
	/// clobber the item, options or logbook pages.
	/// </summary>
	private void DriveOwnedScreenProvider()
	{
		bool filled = _providerFilledThisFrame;
		_providerFilledThisFrame = false;
		if (filled)
		{
			return;
		}

		uint upId = OwnedScreenId();
		if (upId != _providerFilledScreen)
		{
			_providerFilledScreen = 0u;
		}

		if (upId == 0u || !Engine_UiDetailPages.Owns(upId) || _books.Book(upId) is not { } book)
		{
			return;
		}

		// A screen the list path fills keeps its last stated channels while that path is
		// starved (a modal or the spell blade over it): refilling here from `default` samples
		// blanks the page behind the box.
		if (upId == _providerFilledScreen)
		{
			return;
		}

		// The list's row only when the list is THIS screen's: `_screenMenu` outlives its screen
		// until the next list stands, and a page filled from another screen's row is wrong.
		Engine_UiExpandableMenu.Item? row = _screenMenu is { } list && list.ScreenId == upId
			? list.Script.Highlighted
			: null;
		book.HostValues = Engine_UiDetailPages.Values(
			upId, row,
			default, default, new(), default, default, default,
			book.Book?.Strings, optionsPageOpen: false);
		book.HostActive = Engine_UiDetailPages.Active(upId, row);
		book.HostOpacity = Engine_UiDetailPages.Opacities(upId, row);
		book.HostTints = Engine_UiDetailPages.Tints(upId, row);
		book.HostPositions = Engine_UiDetailPages.Positions(upId, default, row);
	}

	/// <summary>
	/// The co-op screen's X, read on EVERY display frame: a press lasts one frame, and read only
	/// on the frames a new guest frame landed it was lost about half the time at 60 Hz.
	/// </summary>
	private void DriveCoopHeroX()
	{
		uint upId = OwnedScreenId();

		// X ON THE CO-OP SCREEN PUTS PLAYER TWO IN THEIR OWN HERO'S BODY — player two's X, as the
		// prompt stands on their page: the joiner's own pad online, player two's pad on the couch.
		// `MenuEquipPressed` is pad 0's X.
		//
		// The body's gender is the kit's where one describes player two, else the appearance
		// pickbox's (`CoopHeroBody:auto`). `sub_82389FB8` is the guest's own
		// `Player::ChangeEntityType`, proven live — the body swaps and the session survives.
		// The outfit, weapons, inventory and stats follow from the kit on the channel drive.
		bool playerTwoX = CoopSession.LocalCoop
			? Fable2Input.XTwoPressed
			: CoopDrivesRightPage && Fable2Input.MenuEquipPressed;
		if (upId == CoopScreenId && playerTwoX)
		{
			RequestCoopHeroBody(!Fable2PageProviders.CoopOwnHero);
		}
	}

	/// <summary>
	/// The screen whose page provider runs. The in-game pause state names it while the in-game
	/// GUI is up; a joiner arriving from the TITLE has the co-op screen under the front-end GUI
	/// instead, where the pause state is null — there the follower's presentation name says it.
	/// Without this, that joiner's page got no names, no X prompt and no mannequin.
	/// </summary>
	private uint OwnedScreenId() =>
		HostBridge.GetPauseState() is { ScreenId: not 0 and not 0xFFFFFFFFu } up
			? up.ScreenId
			: _showing == "coopscreen" ? CoopScreenId : 0u;

	/// <summary>
	/// Player two's side of the toggle: a DESIRED body,
	/// applied only when it differs from the one player two is in, so a repeated request is
	/// not a flip. ONLY AFTER A SAVE IS CHOSEN: the hero comes from the save the player picked
	/// with Y on this screen; until one is chosen there is no hero to switch to, and the prompt
	/// says so.
	/// </summary>
	private void RequestCoopHeroBody(bool ownHero)
	{
		if (ownHero == Fable2PageProviders.CoopOwnHero)
		{
			return;
		}

		if (ownHero && !Fable2PageProviders.CoopHeroAvailable)
		{
			GD.Print("[game-ui] co-op X — no save chosen yet; the hero comes from the chosen save");
			return;
		}

		SetCoopHeroBody(ownHero);
	}

	/// <summary>
	/// Puts player two in their own hero's body, or back in the pickbox henchman, on THIS
	/// instance. The restatement to the peer rides <see cref="DriveCoopHeroChannel"/>, which
	/// also dresses, arms and stats the new body from the kit once the guest has built it.
	///
	/// `pickbox` goes back to the exact henchman the pickbox is showing, out of `sub_8233C508`'s
	/// own table — a fixed name would quietly change their henchman on the way back.
	/// `sub_82389FB8` is the guest's own `Player::ChangeEntityType` and the swap is proven live.
	/// </summary>
	private void SetCoopHeroBody(bool ownHero)
	{
		Fable2PageProviders.CoopOwnHero = ownHero;
		_coopHeroApplied = ownHero;

		// A new body comes up armed with the henchman's defaults, in either direction: the
		// joiner's live sample waits for the kit's own weapons to be drawn again.
		_coopLiveSampling = false;

		// A new body is a new apply: player two's record goes to the pump again for it.
		_coopStatePostedTwo = 0u;

		string body = CoopHeroBodyAction(ownHero);
		_coopBodyPosted = body;
		_coopHeroPickedAt = Time.GetTicksMsec();

		// Told to the peer on this pass, not the next half-second restatement.
		_coopHeroRestate = 0.0;
		GD.Print($"[game-ui] co-op — player two to their {(ownHero ? "own hero" : "henchman")} ({body})");
		HostBridge.PostFrontEndAction(body);
	}

	/// <summary>
	/// The body action for a state. THE KIT'S GENDER WHERE A KIT DESCRIBES PLAYER TWO — Local on
	/// the joiner, Remote on the host — because the two pickboxes did not agree in one run
	/// ("Good Female" on the host, "Good Male" on the joiner). `auto` otherwise: the guest sets
	/// that pickbox from the chosen save's real Gender and Morality (`sub_8243AEF8`), so it is
	/// the authority until a kit arrives.
	/// </summary>
	private static string CoopHeroBodyAction(bool ownHero)
	{
		CoopHeroKit.Kit two = SessionRole == 2u ? CoopHeroKit.Local : CoopHeroKit.Remote;
		return !ownHero ? "CoopHeroBody:pickbox"
			: two.Known ? (two.Female ? "CoopHeroBody:female" : "CoopHeroBody:male")
			: "CoopHeroBody:auto";
	}

	/// <summary>
	/// What one page says about its player's hero while it crosses the wire — the same words on
	/// both machines. Each hero record travels from the machine that plays it (player one's from
	/// the host, player two's from the joiner) as acknowledged fragments (<see cref="CoopHeroState"/>),
	/// so the sending side reports the acknowledgement and the receiving side the fragments
	/// landed. Empty once the record is across, and always on one machine: local co-op reads player
	/// two's save in place.
	/// </summary>
	private static string CoopHeroStatus(bool playerTwo)
	{
		uint role = SessionRole;
		if (role == 0u || CoopSession.LocalCoop)
		{
			return string.Empty;
		}

		bool ours = playerTwo == (role == 2u);
		if (ours)
		{
			return !CoopHeroKit.Local.Known
				? (playerTwo ? "Select a save to bring your hero" : string.Empty)
				: CoopHeroState.Delivered ? "Hero ready"
				: $"Sending hero to {(playerTwo ? "player one" : "player two")}…";
		}

		(int have, int total) = CoopHeroState.Receiving;
		return total > 0 ? $"Receiving hero… {have}/{total}"
			: CoopHeroKit.Remote.Known ? "Hero ready"
			: "Waiting for hero…";
	}

	/// <summary>The session's role as the pump's numbers: 0 solo, 1 host, 2 client.</summary>
	private static uint SessionRole => CoopSession.Side switch
	{
		CoopSession.Role.Host => 1u,
		CoopSession.Role.Client => 2u,
		_ => 0u,
	};

	private CoopSession.Phase _coopPhaseSeen = CoopSession.Phase.Solo;

	private CoopSession.Role _coopSideSeen = CoopSession.Role.Solo;

	/// <summary>Whether local co-op was up last frame, so its end resets player two's hero.</summary>
	private bool _coopLocalHeroSeen;

	/// <summary>Local player two's record, read from their save: the version and its base64.</summary>
	private uint _localTwoVersion;

	private string? _localTwoBase64;

	/// <summary>
	/// Drops every piece of co-op state on both sides of the bridge: the pump's stored state
	/// (`CoopHeroReset`), the own-hero bit, the applied body, the live-sampling latch, the
	/// restate clock, the peer's record, the chosen save, both names, the HeroState versions,
	/// acks, fragment buffers and post latches. Everything restarts from the next poll, so a
	/// later join starts clean.
	/// </summary>
	private void ResetCoopHero(string why)
	{
		HostBridge.PostFrontEndAction("CoopHeroReset");
		_coopPartnerWaiting = false;
		_coopExitUseTaken = 0u;
		_coopExitRequestRestates = 0;
		Fable2PageProviders.CoopOwnHero = false;
		Fable2PageProviders.CoopHeroAvailable = false;
		Fable2PageProviders.CoopPlayerOneName = string.Empty;
		Fable2PageProviders.CoopPlayerTwoName = string.Empty;
		_coopHeroApplied = false;
		_coopLiveSampling = false;
		_coopBodyPosted = string.Empty;
		_localTwoBase64 = null;
		_coopHeroRestate = 0.0;
		_coopStatePostedOne = 0u;
		_coopStatePostedTwo = 0u;
		CoopHeroKit.ResetSession();
		CoopHeroState.Reset();
		ResetCoopMods();
		GD.Print($"[game-ui] co-op — {why}; remote hero, chosen save, own-hero bit, versions, acks and the peer's mods dropped");
	}

	/// <summary>
	/// Refii's own co-op channel, drained and restated once a frame.
	///
	/// THE MESSAGES ARE STATE, NOT EVENTS. The joiner restates "player two's body is X" and the
	/// host its held pairs on a timer for as long as the session lasts, so a dropped datagram
	/// costs one restatement rather than leaving the two instances rendering different
	/// creatures. The hero record itself is the one message too large for that: it crosses as
	/// a versioned, fragmented, acknowledged HeroState (<see cref="CoopHeroState"/>), resent
	/// only until the peer's pump reports the version applied.
	/// </summary>
	private void DriveCoopHeroChannel(double delta)
	{
		// THE SESSION MODEL FIRST — one read of the authoritative manager per frame; every
		// role decision below comes off it. On the frame the peer goes, the session drops to
		// solo or the side changes, everything learned about the peer goes too, so nothing
		// restates a torn-down body and a later join is clean.
		CoopSession.Poll();
		string? reset =
			_coopPhaseSeen == CoopSession.Phase.PeerJoined && CoopSession.State != CoopSession.Phase.PeerJoined
				? "the peer is gone"
			: _coopPhaseSeen != CoopSession.Phase.Solo && CoopSession.State == CoopSession.Phase.Solo
				? "the session ended"
			: _coopSideSeen != CoopSession.Side
				? $"the side changed ({_coopSideSeen} → {CoopSession.Side})"
			: null;

		// The same transitions, told to the player (GameUiCoopNotices) — before the reset drops
		// what they need.
		TellCoopTransitions(_coopPhaseSeen);
		reset ??= _coopLocalHeroSeen && !CoopSession.LocalCoop ? "local co-op ended" : null;
		_coopLocalHeroSeen = CoopSession.LocalCoop;
		if (reset is not null)
		{
			ResetCoopHero(reset);
		}

		_coopPhaseSeen = CoopSession.State;
		_coopSideSeen = CoopSession.Side;

		// SAMPLED EVEN OUTSIDE A SESSION, and deliberately first. Joining tears the joiner's own
		// world down, so their hero has to have been captured BEFORE the co-op screen ever asks
		// for it. See CoopHeroKit.
		_coopKitSample -= delta;
		if (_coopKitSample <= 0.0)
		{
			_coopKitSample = CoopKitSampleSeconds;

			// THE JOINER STOPS SAMPLING ONCE IN A SESSION. After the join, the "hero" slot the
			// capture reads is the HOST's hero — the copy the sync created, armed and naked —
			// not the joiner's own. Sampling on would overwrite the outfit captured before the
			// join with an empty one and ship that to the host as player two's clothes. The
			// host keeps sampling: its hero is player one, and that is what the joiner needs.
			// Record id → def name, off the host's own globals.gdb view — the same lookup the
			// inventory rows use. `Engine_` is reached from here, never from the host layer.
			// WHERE THE GAME SAVES, not where it saves by default: the emulator's live
			// `user_data_root` (the save profile it runs from), and the active profile only while
			// the guest has not said. The default alone read a joiner's
			// hero from a stale copy in the app folder while the game saved to the configured
			// root, so a joiner who had changed outfit arrived at the host in the old one.
			CoopHeroKit.UserDataRoot = HostBridge.GetSaveRoot() is { Length: > 0 } live
				? live
				: CoopHeroKit.UserDataRoot ?? Engine_AppRoot.SaveDirectory;

			// The shared database directly, not the inventory source's copy of it: that copy is
			// set on a screen-menu path the host may not have walked yet when the kit is built,
			// and both weapon ids resolved to '' on the host for exactly that reason.
			CoopHeroKit.RecordName ??= id =>
				(Engine_UiInventorySource.Globals ?? Refii.Gdb.Engine_GameGdb.Database) is { } globals &&
				globals.TryLookup(id, out GdbCursor cursor)
					? cursor.View.RecordName(cursor.Record)
					: string.Empty;

			// THE JOINER'S HERO IS THE ONE THEY WERE PLAYING. A joiner who loads a save and joins
			// from the pause menu has a live capture of that hero (`Sample` ran while the role was
			// still 0 or host); that capture is the kit, and nothing replaces it. The save picked
			// on the co-op screen is read only when there is NO capture — a join from the title,
			// where no hero was ever loaded. Reading the pick whenever its name changed shipped a
			// different hero with every cursor move on the save list (run 2026-10-07 22:07: Hero000
			// then Hero002, the host built the second), and the pick's weapon is the SAVE FILE's,
			// not the one the live hero holds.
			//
			// Once the body is player two's, the live sample is appearance only and starts at
			// once: Worn and Inventory keep their previous values while the fresh body reads
			// empty, and a weapon slot is taken only when it holds a record, so an early sample
			// cannot unarm or undress the kit.
			if (SessionRole != 2u)
			{
				CoopHeroKit.Sample();
			}
			else if (HostBridge.IsInGame && Fable2PageProviders.CoopOwnHero &&
				CoopHeroKit.Local.Known && HostBridge.HeroEntityAddress() != 0u)
			{
				if (!_coopLiveSampling)
				{
					_coopLiveSampling = true;
					GD.Print("[game-ui] co-op — the joiner's body is their own hero; sampling it " +
						"live from here (appearance only)");
				}

				CoopHeroKit.Sample(appearanceOnly: true);
			}

			// LOCAL CO-OP: player two's kit is the save they picked on the co-op screen, read the
			// joiner's way; a new pick is a new version of their record. Fable 3's couch rule: with a
			// save they play as their own hero (the body goes to it once the kit is read), without
			// one as the henchman.
			if (CoopSession.LocalCoop && CoopHeroKit.SampleLocalPlayerTwo())
			{
				_localTwoBase64 = System.Convert.ToBase64String(
					CoopHeroState.Serialize(CoopHeroKit.Remote, ++_localTwoVersion));
				GD.Print($"[game-ui] local co-op — player two's hero read from their save (v{_localTwoVersion})");
				if (!Fable2PageProviders.CoopOwnHero)
				{
					SetCoopHeroBody(true);
				}
			}

			if (SessionRole == 2u && (!CoopHeroKit.Local.Known || CoopHeroKit.ChosenSaveRead))
			{
				// No capture, or the kit came from a pick: the pick names the hero, and a
				// different pick replaces it. The read skips an unchanged name.
				CoopHeroKit.SampleChosenSave();
			}

			// PLAYER TWO'S OWN HERO IS OFFERED ONCE BOTH MACHINES HOLD IT: on the host when the
			// joiner's record has landed, on the joiner when the host has acknowledged it. Offering
			// it earlier swapped the body on one screen while the other still drew the henchman.
			// On the couch player two's record is read from their save in place (Remote, by
			// SampleLocalPlayerTwo) and nothing crosses a wire.
			Fable2PageProviders.CoopHeroAvailable = CoopSession.LocalCoop
				? CoopHeroKit.Remote.Known
				: SessionRole switch
				{
					1u => CoopHeroKit.Remote.Known,
					2u => CoopHeroKit.Local.Known && CoopHeroState.Delivered,
					_ => false,
				};
			Fable2PageProviders.CoopPlayerOneStatus = CoopHeroStatus(playerTwo: false);
			Fable2PageProviders.CoopPlayerTwoStatus = CoopHeroStatus(playerTwo: true);

			// Player one is the host, player two the joiner; each side knows its own name from
			// the node and the other's from RexNet's view of them (PartnerName).
			string own = CoopHeroKit.LocalName;
			string peer = PartnerName();
			bool joiner = SessionRole == 2u;
			Fable2PageProviders.CoopPlayerOneName = joiner ? peer : own;
			Fable2PageProviders.CoopPlayerTwoName = joiner ? own : peer;
		}

		// NOT GATED ON A ROLE. The role bytes come off the co-op manager, and the name kit has
		// to travel while the co-op screen is up, whatever those bytes say at that moment; the
		// channel answers to whichever peer's game traffic reached us. Only the APPLY steps
		// below need a side.
		uint role = SessionRole;

		// THE INBOX IS DRAINED EVERY FRAME AND READ ONLY WHILE A SESSION STANDS: a kit, a chunk
		// or a held pair that arrives after the peer has gone, or before a session exists,
		// belongs to no one and is dropped rather than stored against the next join.
		bool listening = CoopSession.State is CoopSession.Phase.Hosting or CoopSession.Phase.PeerJoined;
		while (HostBridge.CoopReceive() is { } message)
		{
			if (!listening)
			{
				continue;
			}

			// The players' mod values (core/player_state.h): the peer's statement of the player it
			// plays, or its writes to the one this machine plays.
			if (HostBridge.PlayerStateReceive(message))
			{
				continue;
			}

			// A bed's question for this machine's player, or the answer to this machine's (GameUiCoopConsent).
			if (TakeConsentMessage(message))
			{
				continue;
			}

			// The joiner's question about a villager's opinion of them, or the host's answer (GameUiCoopOpinion).
			if (TakeOpinionMessage(message))
			{
				continue;
			}

			// The joiner's real-estate action for its hero two, or the host's ack (GameUiCoopEstate).
			if (TakeEstateMessage(message))
			{
				continue;
			}

			// The host's dynamic weather, for this joiner to show (GameUiCoopWeather).
			if (TakeWeatherMessage(message))
			{
				continue;
			}

			// The host's notice about one of this joiner's buildings (GameUiPropertyTips).
			if (TakePropertyNoticeMessage(message))
			{
				continue;
			}

			// The joiner's Repair All in this host's world (Engine_UiPropertySource).
			if (message == Engine_UiPropertySource.RepairAllMessage)
			{
				if (!CoopSession.IsClient)
				{
					HostBridge.PostFrontEndAction("Property:repairall:2");
				}

				continue;
			}

			// The joiner's repair of one of its properties in this host's world (Engine_UiPropertySource).
			if (message.StartsWith(Engine_UiPropertySource.RepairMessage, System.StringComparison.Ordinal))
			{
				if (!CoopSession.IsClient)
				{
					HostBridge.PostFrontEndAction("Property:repair:2:" + message[Engine_UiPropertySource.RepairMessage.Length..]);
				}

				continue;
			}

			// The peer's mods record; compared below once the peer stands (GameUiCoopMods).
			if (TakeCoopMods(message))
			{
				continue;
			}


			if (message.StartsWith(CoopHeroState.FragmentPrefix, System.StringComparison.Ordinal))
			{
				// The peer's hero, one fragment of a version. Complete and decoded it is
				// `CoopHeroKit.Remote`; the pump post and the ack ride the tick below.
				CoopHeroState.Receive(message);
				continue;
			}

			if (message.StartsWith(CoopHeroState.AckPrefix, System.StringComparison.Ordinal))
			{
				CoopHeroState.Acknowledge(message);
				continue;
			}

			if (message.StartsWith(CoopExitUsePrefix, System.StringComparison.Ordinal))
			{
				// The joiner used an exit: `seq:x:y:z`, the exit's guest position. The host's exit
				// update fires the exit standing there, once per sequence.
				string[] parts = message[CoopExitUsePrefix.Length..].Split(':');
				if (role == 1u && parts.Length == 4 &&
					uint.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
						System.Globalization.CultureInfo.InvariantCulture, out uint sequence) &&
					sequence != _coopExitUseTaken)
				{
					_coopExitUseTaken = sequence;
					HostBridge.PostFrontEndAction($"CoopExitUse:{parts[1]}:{parts[2]}:{parts[3]}");
				}

				continue;
			}

			if (message.StartsWith(CoopExitWaitingPrefix, System.StringComparison.Ordinal))
			{
				// The partner stands at an exit waiting for this machine's player: told once as
				// it begins (Fable 3's TEXT_..._BLOCKED_BY_PLAYER_DISTANCE_REMOTE).
				bool waiting = message[CoopExitWaitingPrefix.Length..] == "1";
				if (waiting && !_coopPartnerWaiting)
				{
					_boxes.InfoBoxEx(ExitRefusedBox, 5f, "Your partner is waiting for you at the exit.");
				}

				_coopPartnerWaiting = waiting;
				continue;
			}

			if (message.StartsWith("CoopHeroHeld:", System.StringComparison.Ordinal))
			{
				// The host's held weapons as net ids; the pump attaches the replicas here.
				if (role == 2u)
				{
					HostBridge.PostFrontEndAction(message);
				}

				continue;
			}

			if (!message.StartsWith(CoopHeroStatePrefix, System.StringComparison.Ordinal))
			{
				continue;
			}

			bool ownHero = message[CoopHeroStatePrefix.Length..] == "1";
			if (role == 1u && ownHero != _coopHeroApplied)
			{
				SetCoopHeroBody(ownHero);
			}
		}

		// THE MODS MUST AGREE. Checked every frame once a peer stands; a mismatch is refused on
		// this machine whichever side it is.
		CheckCoopMods(role);

		// THE PLAYERS' MOD VALUES, every frame: what changed goes at once (the store paces itself,
		// and restates every half second), but only to a standing peer.
		string[] playerState = HostBridge.PlayerStateOutgoing();
		if (CoopSession.PeerStanding)
		{
			foreach (string line in playerState)
			{
				HostBridge.CoopSend(line);
			}
		}

		if (role == 0u)
		{
			// Nothing to apply outside a session; the record goes out while the co-op screen is
			// up, so the peer's page can name this player before the role bytes land.
			_coopHeroRestate -= delta;
			if (_coopHeroRestate > 0.0 || OwnedScreenId() != CoopScreenId)
			{
				return;
			}

			_coopHeroRestate = CoopHeroRestateSeconds;
			SendCoopMods();
			CoopHeroState.Send(CoopHeroKit.Local, CoopHeroRestateSeconds);
			return;
		}

		// THE KIT'S GENDER REACHES THE BODY. A body posted as `auto` before the kit arrived, or
		// under a kit of the other gender, is rebuilt from the kit's: the pump re-types player
		// two and the restatement below dresses the new body.
		if (Fable2PageProviders.CoopOwnHero && CoopHeroBodyAction(true) != _coopBodyPosted)
		{
			GD.Print("[game-ui] co-op — player two's kit states a different body; restating it");
			SetCoopHeroBody(true);
		}

		// SENT ONLY INTO A SESSION. After the loss there is nobody to state anything to, and a kit
		// sent into that gap is one the next peer misreads as its own. The host states from the
		// moment its session is up — the joiner's co-op page names player one before the
		// joined byte lands — the joiner only once it stands.
		bool inSession = CoopSession.PeerStanding ||
			(role == 1u && CoopSession.State == CoopSession.Phase.Hosting);
		if (!inSession)
		{
			return;
		}

		// ONE TICK for the bit, the held pairs, the record and its ack.
		_coopHeroRestate -= delta;
		if (_coopHeroRestate > 0.0)
		{
			return;
		}

		_coopHeroRestate = CoopHeroRestateSeconds;

		// THE MODS RECORD FIRST, both ways: the peer compares it with its own the moment it stands.
		SendCoopMods();

		// Whether this machine's player waits at an exit for the partner (the travel rule).
		HostBridge.CoopSend(CoopExitWaitingPrefix + (Fable2Bridge.ExitWaiting() ? "1" : "0"));

		// THE JOINER'S EXIT USE, restated for a few seconds so a dropped datagram costs nothing; the
		// host fires each sequence once.
		if (role == 2u)
		{
			(uint sequence, float x, float y, float z) = Fable2Bridge.ExitRequest();
			if (sequence != 0u && sequence != _coopExitRequestSeq)
			{
				_coopExitRequestSeq = sequence;
				_coopExitRequestRestates = CoopExitRequestRestates;
			}

			if (_coopExitRequestRestates > 0)
			{
				--_coopExitRequestRestates;
				HostBridge.CoopSend(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"{CoopExitUsePrefix}{sequence}:{x:R}:{y:R}:{z:R}"));
			}
		}

		// THE BIT IS THE JOINER'S TO STATE; THE KIT GOES BOTH WAYS. The joiner's kit dresses
		// player two on the host; the host's kit dresses player one on the joiner, whose copy of
		// the host's hero the sync creates without any clothes.
		if (role == 2u)
		{
			HostBridge.CoopSend(CoopHeroStatePrefix + (Fable2PageProviders.CoopOwnHero ? "1" : "0"));
		}

		// The host names the item entities both players hold, as net ids; the client attaches
		// the replicas (the pump's CoopAttachHeld). Idempotent state, restated — the zero pair
		// included, which says "nothing held" and the pump stores as such.
		if (role == 1u)
		{
			for (uint player = 1u; player <= 2u; ++player)
			{
				if (Fable2Bridge.CoopHeldUids(player) is { } heldIds)
				{
					HostBridge.CoopSend($"CoopHeroHeld:{player}:{heldIds.Melee:X16},{heldIds.Ranged:X16}");
				}
				else if (player == 2u && Fable2PageProviders.CoopOwnHero &&
					(_coopHeldUnanswered += CoopHeroRestateSeconds) >= 5.0)
				{
					// Said while it lasts: a body the bridge cannot read is a joiner who never
					// gets their weapons (run 2026-10-07 22:07: no CoopHeroHeld:2 for a session).
					_coopHeldUnanswered = 0.0;
					(uint one, uint two) = HostBridge.CoopPlayerEntities();
					GD.Print($"[game-ui] co-op — player two's held ids are unanswered by the bridge " +
						$"(player entities one 0x{one:X8}, two 0x{two:X8})");
				}
			}
		}

		// THE RECORD GOES BOTH WAYS. The joiner's dresses player two on the host; the host's
		// dresses player one on the joiner, whose copy of the host's hero the sync creates
		// without any clothes. A changed record is a new version; an unacked one is resent.
		CoopHeroState.Send(CoopHeroKit.Local, CoopHeroRestateSeconds);
		PostHeroStates(role);
		AckHeroState();
	}

	/// <summary>
	/// Hands each player's HeroState to the pump as <c>HeroState:&lt;player&gt;:&lt;base64&gt;</c>, once
	/// per version per body. Player two's is the JOINER's record — Remote on the host, Local on
	/// the joiner, so the joiner's own copy of player two is dressed too — and goes only while
	/// player two is in their own hero's body: the apply dresses whatever body stands, and the
	/// pickbox henchman is not to be dressed. Player one's is the host's record, which only the
	/// joiner's copy needs; the pump refuses to re-give the host's own player one.
	/// </summary>
	private void PostHeroStates(uint role)
	{
		if (Fable2PageProviders.CoopOwnHero)
		{
			// Local co-op: player two's record is the one read from their save on this machine.
			(uint version, string? record, bool known) = CoopSession.LocalCoop
				? (_localTwoVersion, _localTwoBase64, CoopHeroKit.Remote.Known)
				: role == 1u
				? (CoopHeroState.ReceivedVersion, CoopHeroState.ReceivedBase64, CoopHeroKit.Remote.Known)
				: (CoopHeroState.Version, CoopHeroState.LocalBase64, CoopHeroKit.Local.Known);
			if (known && version != 0u && version != _coopStatePostedTwo && record is not null)
			{
				_coopStatePostedTwo = version;
				HostBridge.PostFrontEndAction("HeroState:2:" + record);
				GD.Print($"[game-ui] co-op — HeroState v{version} posted for player two " +
					$"({(role == 1u ? "the joiner's record" : "own record")})");
			}
		}

		if (role == 2u && CoopHeroKit.Remote.Known)
		{
			uint version = CoopHeroState.ReceivedVersion;
			if (version != 0u && version != _coopStatePostedOne && CoopHeroState.ReceivedBase64 is { } record)
			{
				_coopStatePostedOne = version;
				HostBridge.PostFrontEndAction("HeroState:1:" + record);
				GD.Print($"[game-ui] co-op — HeroState v{version} posted for player one (the host's record)");
			}
		}
	}

	/// <summary>
	/// Acknowledges the peer's record the moment it is complete and decoded. Acked again when the
	/// peer resends a version already acknowledged, which says the ack was lost.
	///
	/// ON RECEIPT, NOT ON APPLY. Waiting for the pump to apply the record to a body held the ack
	/// until player two was standing in their own hero — which the screen offers only once the
	/// record is across — so the sender resent a record the host already held, every second, and
	/// "Load Hero" could never appear. A resend could not have helped an apply anyway: a version
	/// already complete is not reassembled again. The record is kept (`ReceivedBase64`) and
	/// applied whenever its body stands (<see cref="PostHeroStates"/>).
	/// </summary>
	private static void AckHeroState()
	{
		uint received = CoopHeroState.ReceivedVersion;
		if (received != 0u && (CoopHeroState.AckedToPeer != received || CoopHeroState.AckDue))
		{
			CoopHeroState.SendAck();
		}
	}

	/// <summary>The joiner's statement of which body player two is in.</summary>
	private const string CoopHeroStatePrefix = "coop.hero=";


	/// <summary>`coop.exit.waiting=0|1`: the sender's player waits at a region exit for the partner.</summary>
	private const string CoopExitWaitingPrefix = "coop.exit.waiting=";

	/// <summary>The partner's last stated <see cref="CoopExitWaitingPrefix"/>.</summary>
	private bool _coopPartnerWaiting;

	/// <summary>`coop.exit.use=seq:x:y:z`: the joiner used the region exit at that guest position.</summary>
	private const string CoopExitUsePrefix = "coop.exit.use=";

	/// <summary>Restatements of one exit use (0.5 s apart).</summary>
	private const int CoopExitRequestRestates = 6;

	private uint _coopExitRequestSeq;
	private int _coopExitRequestRestates;
	private uint _coopExitUseTaken;

	/// <summary>How often the joiner restates it. Often enough that a drop is invisible.</summary>
	private const double CoopHeroRestateSeconds = 0.5;

	/// <summary>Seconds the bridge has answered nothing for player two's held items.</summary>
	private double _coopHeldUnanswered;

	/// <summary>What this instance last asked the guest for, so a restatement is not re-applied.</summary>
	private bool _coopHeroApplied;

	/// <summary>The last `CoopHeroBody:` action posted, so a kit stating another body re-posts it.</summary>
	private string _coopBodyPosted = string.Empty;

	/// <summary>Seconds until the next restatement.</summary>
	private double _coopHeroRestate;

	/// <summary>How often the local hero is captured, in a session or out of one.</summary>
	private const double CoopKitSampleSeconds = 2.0;

	/// <summary>Seconds until the next capture.</summary>
	private double _coopKitSample;

	/// <summary>The HeroState version last posted to the pump for player one (joiner only).</summary>
	private uint _coopStatePostedOne;

	/// <summary>The HeroState version last posted to the pump for player two, for the body posted.</summary>
	private uint _coopStatePostedTwo;


	/// <summary>
	/// An anchor by authored path, tolerant of WHERE the tree starts. `FindByPath` walks exact
	/// names from the first segment, and an owned book that clones its pages as separate chrome
	/// units (`LeftPage`, `RightPage` on the co-op screen) has no `Foreground` node above them —
	/// the full authored path then matches nothing while `RightPage.model_proxy.model` is right
	/// there. Shorter suffixes are tried in turn, so one path serves both tree shapes.
	/// </summary>
	private static Node3D? FindAnchor(Node root, string path)
	{
		string[] segments = path.Split('.');
		for (int from = 0; from < segments.Length; ++from)
		{
			if (Engine_UiTheme.FindByPath(root, string.Join('.', segments[from..])) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>
	/// Last reason the pause list was not standing, so it is said once: the three gate bits
	/// packed, or -1 while it stands.
	/// </summary>
	private int _pauseStandingSaid = -1;

	/// <summary>Forgets the pointed sector after a select, and follows the wheel's level for the touch controls (GameUi.Touch.cs).</summary>
	private void NoteWheelSelect(int sector)
	{
		ForgetWheelPointing();
		CountWheelPick(sector);
	}

	/// <summary>
	/// A select or a back-out changes the wheel's level, and the guest's cursor starts the new level
	/// pointing at nothing (`cursor 2/-1` after entering group 2): the sector posted last is
	/// forgotten, so the next pointing is posted even in the same direction. Otherwise an item
	/// standing where its group stood (Fun's Laugh, north-east like Fun) was never pointed at and
	/// its select picked nothing.
	/// </summary>
	private void ForgetWheelPointing() => _wheelSector = -1;

	/// <summary>The expression wheel's HUD node: its piece id, dots made underscores (Engine_HudArt).</summary>
	private static readonly string ExpressionWheelNode =
		"group_ExpressionWheel_" + Fable2HudLayout.ExpressionWheelName;

	/// <summary>The co-op screen — id 24, `CoopScreen.bgf`.</summary>
	private const uint CoopScreenId = 24u;

	/// <summary>The live entity the preview panel last showed a body for; 0 on the hash path.</summary>
	private uint _previewLiveShown;

	public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("ui_process"))
		{
			// The pointer first, so every screen below reads the same position this frame.
			Engine_UiPointer.Resolve(_uiCamera, _viewport, GetViewport().GetVisibleRect().Size);

			Fable2Input.CoopScreenOwnsInput = OwnedScreenId() == CoopScreenId;
			PointerCoop();
			ui_processBody(delta);
			DriveHudRate();

			// What no screen took becomes the pad's (right click B, wheel D-pad steps).
			Engine_UiPointer.HandLeftoversToPad();
		}
	}

	/// <summary>The pause state as read on the last guest frame.</summary>
	private Engine_UiModel.PauseState? _modal;

	/// <summary>Seconds until the hero's text suffix is read again.</summary>
	private double _heroSuffixClock;

	/// <summary>
	/// The mouse on a list — the controller's carousel, driven by the mouse. Pointer MOVEMENT does
	/// nothing to it. A wheel notch over the list's column is a D-pad step. The scroll bar walks
	/// the highlight to the row it names. A click on a drawn row moves the highlight there and is
	/// the A press, once the carousel has let A through (`WaitForInput`). True on the frame that
	/// press is due — the caller treats it as accept.
	/// </summary>
	private static bool PointerList(Engine_FrontEndMenuView? view)
	{
		if (view is null)
		{
			return false;
		}

		if (Engine_UiPointer.Canvas is not { } at)
		{
			// The pad took over: a click still waiting is dropped, never answered later.
			if (ReferenceEquals(_clickView, view))
			{
				_clickView = null;
				_clickItem = null;
			}

			return false;
		}

		// THE SCROLL BAR: a press on it sends the highlight to the row under the pointer, and the
		// drag keeps sending it for as long as the button is held, wherever the pointer wanders.
		if (ReferenceEquals(_barDrag, view))
		{
			if (Input.IsMouseButtonPressed(MouseButton.Left))
			{
				BarTo(view, view.ScrollBarPositionAt(at), grab: false);
				return false;
			}

			_barDrag = null;
		}

		if (view.ScrollBarAt(at) is { } barPosition)
		{
			if (Engine_UiPointer.TakeLeft())
			{
				_barDrag = view;
				_clickView = null;
				_clickItem = null;
				BarTo(view, barPosition, grab: true);
			}

			WheelToPad();
			return false;
		}

		// A list on the left page never answers the pointer over the right one: that half is
		// the detail page's, to scroll and to turn the preview.
		if (view.GlobalPosition.X < 0f && at.X >= 0f)
		{
			return ClickDue(view);
		}

		Engine_UiExpandableMenu.Item? item = view.EntryAt(at);

		// The wheel over the list's column — on a row or between them — is the pad's D-pad step,
		// handed to it here so nothing else this frame claims a notch that landed on the list.
		// The pad clamps at the ends exactly as it does for the stick.
		if (item is not null || view.ColumnHas(at))
		{
			WheelToPad();
		}

		if (item is not null && Engine_UiPointer.TakeLeft())
		{
			_clickView = view;
			_clickItem = item;
			_clickPointed = false;
			_clickAt = Time.GetTicksMsec();
		}

		return ClickDue(view);
	}

	/// <summary>Hands this frame's wheel notches to the pad as D-pad steps.</summary>
	private static void WheelToPad()
	{
		if (Engine_UiPointer.WheelSteps != 0)
		{
			GameInput.QueuePointerButtons(false, Engine_UiPointer.TakeWheel());
		}
	}

	/// <summary>
	/// The highlight to the row a 0..1 place on the bar names. Our cursor walks there a step a
	/// frame (<see cref="Engine_FrontEndMenuView.SeekTo"/>); the guest's is sent the D-pad steps
	/// that take it there, counted from where the grab found it.
	/// </summary>
	private static void BarTo(Engine_FrontEndMenuView view, float position, bool grab)
	{
		if (view.Menu is null)
		{
			return;
		}

		if (!view.GuestCursor)
		{
			view.ScrollTo(position);
			return;
		}

		if (grab)
		{
			GameInput.ClearPointerSteps();
			_barStart = view.Menu.Model.CurrentHighlightIdx;
			_barSent = 0;
		}

		int send = Engine_UiCarouselBar.StepsToSend(_barStart, view.RowAtBar(position), _barSent);
		if (send != 0)
		{
			GameInput.QueuePointerSteps(send);
			_barSent += send;
		}
	}

	/// <summary>
	/// Whether a click on <paramref name="view"/>'s row is due as A this frame. The click first
	/// moves the highlight onto its row; the press follows once the carousel's press gate is down,
	/// since A mid-cascade is swallowed whole. Dropped when its row is gone, when something else
	/// moved the highlight off it, or after <see cref="ClickPatienceMs"/>.
	/// </summary>
	private static bool ClickDue(Engine_FrontEndMenuView view)
	{
		if (!ReferenceEquals(_clickView, view) || _clickItem is not { } item)
		{
			return false;
		}

		if (view.Menu is null || view.Menu.Model.VisibleIndexOf(item) == 0 ||
			Time.GetTicksMsec() - _clickAt > ClickPatienceMs)
		{
			_clickView = null;
			_clickItem = null;
			return false;
		}

		if (view.PressGated)
		{
			return false;
		}

		if (!ReferenceEquals(view.Menu.Highlighted, item))
		{
			if (_clickPointed)
			{
				_clickView = null;
				_clickItem = null;
				return false;
			}

			view.PointAt(item);
			_clickPointed = true;
			if (view.PressGated || !ReferenceEquals(view.Menu.Highlighted, item))
			{
				return false;
			}
		}

		_clickView = null;
		_clickItem = null;
		return true;
	}

	/// <summary>The list a click is waiting on, and the row it landed on.</summary>
	private static Engine_FrontEndMenuView? _clickView;

	private static Engine_UiExpandableMenu.Item? _clickItem;

	/// <summary>The waiting click has already moved the highlight onto its row.</summary>
	private static bool _clickPointed;

	private static ulong _clickAt;

	/// <summary>How long a click waits on the carousel before it is dropped.</summary>
	private const ulong ClickPatienceMs = 1500;

	/// <summary>Where the bar's grab found the guest's cursor, and the steps sent since.</summary>
	private static int _barStart;

	private static int _barSent;

	/// <summary>The left button went down on the expression wheel and has not come up.</summary>
	private bool _wheelMouseDown;

	/// <summary>The list whose scroll bar the left button is dragging, or null.</summary>
	private static Engine_FrontEndMenuView? _barDrag;

	/// <summary>
	/// The mouse on a book's right-hand page: the wheel over its scroll region scrolls it, and its
	/// authored scroll bar jumps on a press and drags while the button is held.
	/// </summary>
	private static void PointerPage(Engine_UiOwnedBook? book)
	{
		if (book is null || Engine_UiPointer.Canvas is not { } at)
		{
			return;
		}

		if (ReferenceEquals(_pageDrag, book))
		{
			if (Input.IsMouseButtonPressed(MouseButton.Left))
			{
				book.ScrollRegionTo(book.ScrollBarAt(at, anyHeight: true) ?? 0f);
				return;
			}

			_pageDrag = null;
		}

		if (book.ScrollBarAt(at) is { } fraction)
		{
			if (Engine_UiPointer.TakeLeft())
			{
				_pageDrag = book;
				book.ScrollRegionTo(fraction);
			}

			if (Engine_UiPointer.WheelSteps != 0)
			{
				book.AddScrollWheel(Engine_UiPointer.TakeWheel());
			}

			return;
		}

		if (book.ScrollRegionRect is { } page && page.HasPoint(at) && Engine_UiPointer.WheelSteps != 0)
		{
			book.AddScrollWheel(Engine_UiPointer.TakeWheel());
		}
	}

	/// <summary>The book whose scroll bar the left button is dragging, or null.</summary>
	private static Engine_UiOwnedBook? _pageDrag;

	/// <summary>
	/// The right-hand pages keep the pointer to themselves: a click or a wheel notch over them
	/// that nothing on the page took is dropped, never handed on to the list as A or a step.
	/// </summary>
	private void SwallowOverPages(Engine_UiOwnedBook? book)
	{
		if (book is null || Engine_UiPointer.Canvas is not { } at ||
			(!Engine_UiPointer.LeftPressed && Engine_UiPointer.WheelSteps == 0) ||
			_screenMenuView?.EntryAt(at) is not null || _screenMenuView?.ScrollBarAt(at) is not null ||
			(book.NodeFor("Foreground.Book.Pages") ?? book.Find("Pages")) is not { } pages ||
			Engine_UiPointer.CanvasBounds(pages) is not { } bounds || !bounds.HasPoint(at) ||
			Engine_UiSeats.PointToLayout(pages, at).X < 0f)
		{
			return;
		}

		// The preview's ring takes its own press, to grab and turn the model.
		if (!PreviewRingHas(at))
		{
			Engine_UiPointer.TakeLeft();
		}

		Engine_UiPointer.TakeWheel();
	}

	/// <summary>The page arrows each book screen authors: the equipment books' Page1 carries
	/// them in its footer, every other page directly (`equipmentscreen.bgf`,
	/// `abilitiesscreen.bgf`).</summary>
	private static readonly string[] PageNumberGroups =
	{
		"Foreground.Book.Pages.Page1.Contents.Footer.PageNumber",
		"Foreground.Book.Pages.Page1.Contents.PageNumber",
		"Foreground.Book.Pages.Page2.Contents.PageNumber",
	};

	/// <summary>
	/// -1 or +1 when this frame's click landed on a drawn page arrow (taking it), else 0.
	/// </summary>
	private static int PageArrowClicked(Engine_UiOwnedBook book)
	{
		if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } at)
		{
			return 0;
		}

		foreach (string group in PageNumberGroups)
		{
			foreach ((string arrow, int way) in new[] { ("LeftArrow", -1), ("RightArrow", 1) })
			{
				if (book.NodeFor($"{group}.{arrow}") is { } node && node.IsVisibleInTree() &&
					Engine_UiPointer.CanvasBounds(node) is { } bounds && bounds.Grow(12f).HasPoint(at))
				{
					Engine_UiPointer.TakeLeft();
					return way;
				}
			}
		}

		return 0;
	}

	/// <summary>
	/// Whether a canvas point is inside the item preview's ring — the panel the model is fitted
	/// to, centred on the anchor it hangs from.
	/// </summary>
	private bool PreviewRingHas(Vector2 at) =>
		_previewObject is { } preview && preview.IsVisibleInTree() &&
		preview.GetParent() is Node3D anchor && preview.PanelSize > 0f &&
		new Vector2(anchor.GlobalPosition.X, anchor.GlobalPosition.Y).DistanceTo(at) <=
			preview.PanelSize * 0.5f;

	/// <summary>
	/// Whether this frame's click landed on the book's right-hand pages — not on its scroll bar,
	/// which the page scroll takes — and takes it if so.
	/// </summary>
	private bool PageClicked(Engine_UiOwnedBook book)
	{
		if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } at ||
			book.ScrollBarAt(at) is not null || _screenMenuView?.EntryAt(at) is not null ||
			_screenMenuView?.ScrollBarAt(at) is not null ||
			(book.NodeFor("Foreground.Book.Pages") ?? book.Find("Pages")) is not { } pages ||
			Engine_UiPointer.CanvasBounds(pages) is not { } bounds || !bounds.HasPoint(at) ||
			Engine_UiSeats.PointToLayout(pages, at).X < 0f)
		{
			return false;
		}

		Engine_UiPointer.TakeLeft();
		return true;
	}

	public static bool WorldInteractionAllowed { get; private set; }

	/// <summary>
	/// Reads the VANILLA text bank — the one `Engine_ItemNames` asks for a curated record,
	/// after the mounted bank — on a worker, so the first Items list does not read 2.6 MB and
	/// inflate 5.5 MB of pages on the main thread. Only bytes, dictionaries and deflate streams
	/// are touched; the loader prints its own line. Not awaited, and only this bank: its lazy
	/// read is a plain `??=`, so a main-thread lookup landing mid-read sees null and reads the
	/// bank itself, exactly as before. The MOUNTED bank is left to the level build, which
	/// already reads it — its loader marks "tried" before "loaded", and a second reader would
	/// open a window where a lookup answers nothing.
	/// </summary>
	private static void WarmTextBanks()
	{
		_ = System.Threading.Tasks.Task.Run(() =>
		{
			try
			{
				_ = Engine_BnkTextBankLoader.BaseBank;
			}
			catch (System.Exception ex)
			{
				GD.PushWarning($"[babel] warming the text banks failed: {ex.Message}");
			}
		});
	}

	/// <summary>
	/// The language the guest runs in, followed. The title's Language entry switches the guest's
	/// own book and fonts (`sub_82382C68` -> `sub_823835F8`) and writes the choice to the profile;
	/// every string and glyph on screen is ours, so without this the switch changed nothing the
	/// player could see. Read on the same half-second clock as the hero suffix: a switch is a
	/// title-screen event, and the guest takes a second to reload behind its own banner anyway.
	/// </summary>
	private void FollowGuestCulture()
	{
		if (!Refii.Text.Engine_GameCulture.Set(HostBridge.GetCurrentCulture()))
		{
			return;
		}

		string culture = Refii.Text.Engine_GameCulture.Current;
		GD.Print($"[game-ui] the guest's language is now '{culture}' — reading its text and fonts");
		Engine_BnkTextBankLoader.InvalidateCulture();
		_presentation?.ReloadForCulture(culture);
		_menuView?.Refresh();
	}

	private void ui_processBody(double delta)
	{
		// Idle the full-window UI SubViewport while the loading screen cover sits on top of it,
		// and wake it immediately once the cover drops.
		Engine_UiSceneBuilder.SetViewportActive(_viewport, IsRevealed && !GameWorld.CoverHeld);
		ResetRowActionPrompt();
        WorldInteractionAllowed = IsRevealed && !ModalPanelUp && !_fullCutscene && !GameMorphMirror.OwnsInput && !GameDecorate.Active;
		if (DriveGoldenCockMenu(delta)) return;
		// THE QUIT LATCH. The guest is still running its confirmation, save and teardown; the
		// front end coming up is it reporting all three done, and the only point at which
		// ending the process cannot interrupt a save in flight.
		if (_quitToDesktopPending)
		{
			_quitToDesktopWindow -= delta;
			if (HostBridge.FrontEndGuiIsUp)
			{
				_quitToDesktopPending = false;
				GameExit.ToDesktop(GetTree());
				return;
			}

			// A BOUND on the latch, not a claim about the guest: a player can refuse the box,
			// leave the pause list and play on, and an armed latch would then fire on some
			// later return to the title.
			if (_quitToDesktopWindow <= 0.0)
			{
				_quitToDesktopPending = false;
				GD.Print("[game-ui] the pending quit to the desktop expired — the guest never " +
					"reached the front end; staying in the game");
			}
		}

		// The Video page's brightness, as the guest's camera holds it. Every frame, so the
		// slider's own live preview (`sub_829C0AB0`) shows while it is dragged.
		if (Fable2Bridge.GuestBrightness() is { } brightness)
		{
			Refii.Lighting.Engine_PostFxParams.UserGamma = brightness;
		}

		// The hero's text suffix, which every guest string lookup tries first. It changes only
		// with the hero, so twice a second is plenty.
		_heroSuffixClock -= delta;
		if (_heroSuffixClock <= 0.0)
		{
			_heroSuffixClock = 0.5;
			Refii.Text.Engine_BabelDatabase.HeroTextSuffix = HostBridge.HeroTextSuffix();
			FollowGuestCulture();
		}

		// OUR MULTIPLAYER SCREEN COMES DOWN WHEN THE GAME STARTS, AND IT HAS TO BE CHECKED HERE.
		//
		// The guest has no multiplayer/friends presentation — the id->bgf table's only online
		// entry is 24 CoopScreen.bgf — so this screen is ours end to end and the guest's own
		// front-end teardown cannot dismiss it. On a join `sub_82319BE8` writes its run-loop exit
		// byte (0x82319E00) and destroys the guest's own presentations; ours would otherwise keep
		// standing over the running world, which is what left a joining player looking at the
		// friends list with the game already playing behind it.
		//
		// IT MUST RUN BEFORE THE FRONT-END GATES BELOW. The first attempt sat next to the screen's
		// own input drive, which is past `!IsRevealed` — the gate that stops running the moment
		// the game is up, so the close could never fire on the one transition it exists for.
		//
		// `IsInGame` is the guest stating it: byte_83496A5E, set by Game_InitDatabases and cleared
		// by the game mode's shutdown.
		//
		// ON THE TRANSITION ONLY. The pause menu's Multiplayer tab opens this same screen while
		// the game is already up; a level test closed it again the frame after it opened, which
		// is what made the tab look dead in a running co-op game.
		bool inGame = HostBridge.IsInGame;
		if (OpenListBook is { } frontEndBook && inGame && !_multiplayerSawInGame)
		{
			GD.Print("[game-ui] the game came up — closing the front end's list book");
			frontEndBook.Close();
		}

		_multiplayerSawInGame = inGame;

		// THE GUEST LEAVING THE GAME, on the falling edge of its own flag. Everything keyed on
		// the game's presentations is dropped here — see LeaveGame — rather than left to be
		// noticed, because the front end that follows never runs the in-game sampler.
		if (!inGame && _sawInGame)
		{
			LeaveGame();
		}

		// THE GAME COMING UP, on the rising edge: the text banks an item label resolves through
		// are read now, off the main thread, rather than on the frame the first Items screen
		// opens. The vanilla bank in particular is only ever asked for by a curated item name,
		// so it was reached 30 minutes into a session, in the middle of building the gift list.
		if (inGame && !_sawInGame)
		{
			EnsureRebuiltHud();
			WarmTextBanks();
		}

		_sawInGame = inGame;

		// THE OWNED BOOKS, BUILT WHILE THE LOADING SCREEN IS UP. A book is 100-500 cloned
		// nodes, their materials, bound text and nine-slice borders — 100-300 ms on the main
		// thread, which used to land on the frame the player first opened each screen. The
		// loading screen is a window where that frame is not seen, so one book is built per
		// frame of it; whatever is left when it comes down is built on demand as before.
		if (inGame && _loading is { IsShowing: true })
		{
			using (Refii.Core.Engine_FramePhases.Measure("ui.book.prebuild"))
			{
				_books.PrebuildOne();
			}

			// The phone's shadowed shaders, waiting for a frame nobody sees (lite_shadowless).
			if (_loading.IsLevelLoad) Refii.Models.Engine_ShaderSpecialisation.LoadingScreen();
		}

		// THE HOST'S EMBEDDED PREVIEW dies with the page that asked for it. The ask is
		// per-frame (the abilities detail block sets the flag); a frame with no ask — the
		// blade, another screen, gameplay — takes the movie down.
		//
		// The DECODER is torn down by GameVideo's OWN _Process (the `EmbedOrdinal < 0` path):
		// GameVideo is the one owner of `_decoder.Dispose`. Disposing it from here too was a
		// second teardown site on the same object each frame — a double-free that corrupted the
		// host heap (`malloc_consolidate(): unaligned fastbin`). Here we only DETACH — drop the
		// ordinal and clear the latch — and let GameVideo notice and stop on its next tick.
		if (!_embeddedMovieAsked && Video is { } orphaned && orphaned.HasHostMovie)
		{
			orphaned.DetachHostMovie();

			// Only the host's own slots: a guest-hosted movie's slot is re-added every frame
			// by FollowMovie while its sprite stands, so a clear here cannot lose one.
			_movieSlots.Clear();
		}

		_embeddedMovieAsked = false;

		// THE DEBUG MENU'S VISIBILITY, applied every frame rather than on screen changes:
		// the reveal after the boot movies is not a screen change, and the menu must never
		// appear behind a bink or accept a click before the front end is ready. Frontend up
		// AND the movies done.
		// GATED ON A LAUNCHER SETTING. The overlay is a development fixture that draws over the
		// game's own UI, so it is off unless `developer_menu_enabled` says otherwise. Read once —
		// the file is not watched, and a per-frame read would open it every frame.
		bool showDebug = IsRevealed && DeveloperMenuEnabled;
		if (showDebug && _debugMenu is null)
		{
			ShowDebugMenu();
		}
		if (_debugMenu is not null)
		{
			_debugMenu.Visible = showDebug;
		}
		// Visible as well as revealed. IsRevealed is the whole UI layer being on screen — it
		// is false through the boot movies — and Visible is this menu being the screen that is
		// up. Without the second test the highlight would keep moving under the title screen,
		// so a player holding down on the title would arrive at the menu with the selection
		// already somewhere else.
		// Before the menu gate, because this has to keep reporting once the front end is gone
		// — that is exactly the window the in-game UI lives in.
		// THE LOADING SCREEN FIRST, and outside every gate below it. The others return early on
		// `IsRevealed` or on there being no menu, and a loading screen has to work in exactly
		// those windows — it is up while the front end is coming down and before the HUD exists.
		_loading?.Poll();

		// THE FADE, on the same terms and for the same reason — it runs across every one of
		// those windows, and it is a continuous ramp, so it is driven per frame rather than on
		// the sampling cadence.
		_fade?.Drive();

		ReportPumpHealth(delta);

		// WHICH LAYOUT THE DISPLAY WANTS, re-read as the window changes. Reported on the change
		// rather than every frame: a HUD that jumped between layouts and a HUD in the wrong one
		// look the same in a still, and only the transition says which.
		if (_viewport is not null)
		{
			bool wide = Engine_UiWidescreen.IsWidescreen(_viewport.Size);
			if (wide != _isWidescreen)
			{
				_isWidescreen = wide;
				GD.Print($"[game-ui] the display is now {(wide ? "WIDESCREEN" : "4:3")} " +
					$"({_viewport.Size.X}x{_viewport.Size.Y}) — the shipped " +
					$"{(wide ? "wide" : "standard")} layout applies");
			}
		}


		// The front-end list rides out with its book's left half — the template's wide layout.
		if (_menuView is not null && GodotObject.IsInstanceValid(_menuView))
		{
			_menuView.Position = _menuView.Position with
			{
				X = _menuView.Anchor.X - Engine_UiTemplates.WideListShift(1010),
			};
		}

		// The anchors resolve against the WINDOW, and it can change under us at any time.
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.Window = GetViewport().GetVisibleRect().Size;
		}

		SeatPlayerUi(GetViewport().GetVisibleRect().Size);

		// The toast works in display pixels; the mount maps them onto the canvas — see
		// BuildAchievementToast for why the two stay in different units.
		if (_toast is not null && _toastMount is not null &&
			GodotObject.IsInstanceValid(_toastMount))
		{
			Vector2 window = GetViewport().GetVisibleRect().Size;
			if (window.Y > 0f)
			{
				Vector2 extent = Engine_HudLayout.CanvasExtent(window);
				float unitsPerPixel = extent.Y / window.Y;
				_toastMount.Scale = new Vector3(unitsPerPixel, unitsPerPixel, 1f);
				_toast.Canvas = window;
				_toast.Held = !IsRevealed;
			}
		}

		ApplyHudWidgets(GetViewport().GetVisibleRect().Size);

		DriveUiCamera();
		// THE UI CUT, DRAINED FIRST. The `GUI.*` calls the game VM made since the last frame are
		// the guest STATING what it wants on screen, and every other drive below infers the same
		// thing from a result — so anything driven from a call has to see it before the frame
		// that would otherwise have inferred it. Drained even when nothing routes yet: the
		// report is what turns the plan's static census into a measured one.
		EnsureGuiNativeRoutes();
		EnsureHudRegistryRoutes();

		// On the guest's frame: what reads guest state below reads what the guest wrote on its
		// last HF tick, and between two the read is identical. Input pumps, per-frame animation
		// and the surfaces' own drive stay on every frame.
		bool guestFrame = Refii.Core.Engine_GuestFrame.Changed;
		if (guestFrame)
		{
			_guiNatives.Drive();
		}

		// THE WIDGETS RUN THEIR OWN CLOCKS, and their predicates report from them — so both have
		// to happen every frame, in this order. A predicate published before the tick would
		// answer with last frame's fill percent.
		_widgets.Tick(delta);
		StepMiniGameTally();
		foreach (string sound in _widgets.TakeSounds())
		{
			_screens.PlaySound(sound);
		}

		if (guestFrame)
		{
			PublishPredicates();
		}

		// THE MENU MODEL, FED FROM THE C++'s OWN STATEMENTS. Every `g_Menu:` mutation the game
		// makes — spawn, ghost, emphasis, refresh, delete — arrives here with its staged
		// attributes, and applying them in order puts the list model in the host. This is what
		// `GetLuaMenu` retired into: the same information, taken where the C++ states it instead
		// of read back out of the Gameface VM afterwards.
		//
		// DRAINED BEFORE ANYTHING READS A MENU, because a statement made this frame should be
		// visible to this frame's drive rather than the next one.
		if (guestFrame)
		{
			_menuModel.Apply(Fable2UiSource.Active.TakeMenuChunks());
		}
		// Change-keyed on what the model HOLDS, not on the statement count: the pause root
		// restates its rows every tick (four statements a tick, the same rows), and a line per
		// statement was two log lines a tick for the whole time the pause menu stood.
		if (_menuModel.Applied != _menuModelReported)
		{
			_menuModelReported = _menuModel.Applied;
			string held = _menuModel.DescribeMenus();
			if (held != _menuModelHeld)
			{
				_menuModelHeld = held;
				GD.Print($"[menu-model] {_menuModel.Describe()}");
			}
		}

		// WHAT THE CAPTURE ITSELF HAS SEEN, whenever it moves. Without this, "the guest states
		// no menus" and "our hook never fired" produce the identical silence — and a whole run
		// went by with no `[menu-model]` line at all, which is why every screen was inferring its
		// rows from the button pool's geometry instead of reading the guest's own statements.
		(ulong recorded, uint pending, ulong dropped) = guestFrame
			? HostBridge.MenuChunkCounters()
			: (_menuChunksRecorded, 0u, _menuChunksDropped);
		if (recorded != _menuChunksRecorded || dropped != _menuChunksDropped)
		{
			// Said on a drop, the first statement, and each doubling — the count alone moves
			// every tick the pause menu stands.
			bool say = (dropped != _menuChunksDropped &&
					Refii.Core.Engine_RdHealth.ShouldLogThrottled("gameui.menumodel.drop", 5000, System.Environment.TickCount64, out _)) ||
				recorded == 0 ||
				(recorded <= 4096 && (recorded & (recorded - 1)) == 0) ||
				_menuChunksRecorded == 0;
			_menuChunksRecorded = recorded;
			_menuChunksDropped = dropped;
			if (say)
			{
				GD.Print($"[menu-model] the g_Menu capture has recorded {recorded} statement(s), " +
					$"{pending} pending, {dropped} dropped — applied {_menuModel.Applied}");
			}
		}
		TellExitRefused();
		_boxes.Tick(delta);

		// THE HUD'S TOPBOX AND TIMING METER, stated by name for the slide fold above: the
		// guest publishes their strings and meters into registries the host reads, and only
		// their visibility has to be re-derived here.
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			if (guestFrame)
			{
				Engine_UiHudTopBox.Publish(
					Engine_UiHudTopBox.Front, GameInput.AHeld, GameInput.YHeld, _hud.Values,
					_presentation?.Strings, _boxes.FrontOf(RefiiNoticeBoxes));
				Engine_UiHudTargetedQuery.Refresh(_hud.Values);
				DriveJoinerOpinion(_hud.Values, delta);
			}

			// THE PEER'S EXPRESSION STAYS ON THE PEER'S SCREEN. The guest starts the local meter
			// for a remote expression too (net message 136); in a network session only the local
			// player's pip is drawn here — pip A is player one's, pip B player two's
			// (`sub_824143F8`: 1 when the expressing entity is `*(EM+108)`). Split screen and a
			// job's bar fill (mode 3) are one screen's business and draw as they are.
			// Run every display frame so Smoothed() extrapolates the timing meter at full monitor Hz.
			HostBridge.ExpressionMeter meter = HostBridge.GetExpressionMeter();
			bool network = CoopSession.PeerStanding && !CoopSession.LocalCoop;
			bool mine = !network || meter.Mode == 3 || (CoopSession.IsClient ? meter.ArmedB : meter.ArmedA);
			int ownPip = network && meter.Mode != 3 ? (CoopSession.IsClient ? 2 : 1) : 0;
			Engine_UiHudExpressionMeter.Publish(meter, _hud, mine, ownPip);

			// ON SPLIT SCREEN THE METER STANDS IN THE HALF OF WHOEVER ARMED IT: pip A is the
			// hero's, pip B the henchman's, both is both (`sub_823D0E10`). A meter with no pip
			// (a job's bar fill) is player one's screen, as the guest's one HUD is.
			int armed = (meter.ArmedA ? 1 : 0) | (meter.ArmedB ? 2 : 0);
			_hud.ExpressionPlayers = armed == 0 ? 1 : armed;

			// THE EXPRESSION WHEEL STANDS IN ITS OWNER'S HALF, as the guest states the owner.
			_hud.WheelPlayer = ExpressionWheelOwner();
			if (Engine_Views.Split && _hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
			{
				_hudTwo.WheelPlayer = _hud.WheelPlayer;
				_hudTwo.ExpressionPlayers = _hud.ExpressionPlayers;
				Engine_UiHudExpressionMeter.Publish(meter, _hudTwo, mine);
			}
		}

		_surfaces.Message.StatedDisplayBox = _boxes.GuestMessageBox;
		Fable2Input.DisplayMessageBoxUp = _boxes.GuestMessageBox != 0u;
		if (_fade is not null)
		{
			_fade.UnderUi = _boxes.GuestMessageBox != 0u || Engine_UiDisplayBoxes.Open.Count != 0;
		}

		// Change-keyed, so it is one line per box rather than one per frame — and it is the only
		// place the owned box state is visible at all.
		if (_boxes.Generation != _boxesReported)
		{
			_boxesReported = _boxes.Generation;
			GD.Print($"[ui-cut] boxes: {_boxes.Describe()}");
		}

		if (guestFrame)
		{
			ReportCamera();
			TracePresentations();
		}
		DriveWorldIcons(guestFrame);
		DriveCoopHeroChannel(delta);
		if (!GameWorld.CoverHeld)
		{
			DriveCoopNotices(delta);
		}
		DriveCoopConsent(delta);
		DriveCoopEstate(delta);
		DriveCoopWeather(delta);
		if (!GameWorld.CoverHeld)
		{
			DrivePropertyTips(delta);
		}
		// What the guest told its presentations since the last frame, before any book stands.
		Engine_UiSlideStatements.Take();
		if (guestFrame) DriveOwnedScreenProvider();
		DriveCoopHeroX();
		if (guestFrame) _books.Drive(_drawn, IsRevealed);
		if (guestFrame) _spells.Follow(IsRevealed);
		DrivePreviewObject();
		DriveCoopScreen(delta);
		if (guestFrame) DriveCoopFigures();

		if (guestFrame) ScanWhenSettled();
		DriveFrontEndPrebuild();
		DriveTitle(delta);

		// THE SHARED MODAL FLAG BELONGS TO WHICHEVER SURFACE STANDS A VIEW THIS FRAME — false
		// here, re-set by the active one before this frame's value is ever read (GameInput
		// samples once per frame, after the value settles). This is the statement the surface
		// set cannot make for itself: "nobody raised a box at all" happens where none of them
		// runs.
		Fable2Input.ModalBoxOwnsInput = false;
		Fable2Input.HostUiOwnsPad = false;
		Fable2Input.RowActionHolds = 0;

		// THE GUEST'S STICK LATCH IS RELEASED BY DEFAULT. Every drive below can take the frame
		// with a `return`, so "release it when the list stops owning the input" cannot be written
		// at the point of use — a modal raised while the player held a direction would leave the
		// guest scrolling for ever, because its latch has no timeout. Asserting is immediate;
		// only the release waits for a pass in which nobody asked for a direction.

		// THE OWNED SURFACES FIRST, and independent of the pause menu — the guest raises them
		// over ANY screen and one that is pending owns the frame outright. Their order is the
		// set's own; see Engine_UiSurfaceSet.
		if (guestFrame)
		{
			DriveAchievements();
		}

		// The frame's one pause-state read (HostBridge caches it per frame), so the surfaces,
		// the screen list and the pause list describe the same screen.
		_modal = HostBridge.GetPauseState();
		Engine_UiModel.PauseState? modal = _modal;

		// A NEW SCREEN OPENS ON ITS FIRST PAGE, on the frame the guest states it — not on the
		// presentation sample a second later, which left a quick tab change on the old page and
		// swallowed the first turn as "held".
		uint statedScreen = modal?.ScreenId ?? uint.MaxValue;
		if (statedScreen != _pageScreen)
		{
			_pageScreen = statedScreen;
			Fable2PageProviders.BookPage = 0;
			_screenPageHeld = 0;
		}

		// A SCREEN LIST LEAVES WITH ITS SCREEN, on the frame the guest states another one. Its
		// own drive is what hides it, and that drive does not run once the pause list (or any
		// earlier drive) takes the frame — so after B the item rows stood over the pause
		// backdrop until the pause book's next sample.
		if (_screenMenuView is { Visible: true } && GodotObject.IsInstanceValid(_screenMenuView) &&
			statedScreen != _screenMenuScreen)
		{
			_screenMenuView.Visible = false;
		}
		if (_surfaces.Drive(IsRevealed ? _presentation : null, modal, SurfaceInput(delta)))
		{
			return;
		}

		// THE HOST'S OWN SURFACES with nothing of the guest's under them: a host box out of game
		// (the front end's dialog), then a mod's menu opened with no list up. See GameUiHostSurfaces.
		if (DriveFrontEndHostBox(delta) || DriveHostModMenu(delta))
		{
			return;
		}

		// A DISPLAY BOX DRAWN AS A SCREEN — the quest card (5). It is never the current screen,
		// so the revealed-screen path below never reaches it; it answers its own template's
		// vocabulary, and the modal mask holds the pad while it is up.
		if (IsRevealed && Engine_UiSource.Current.DisplayBoxScreen() is var boxScreen &&
			boxScreen != uint.MaxValue && Engine_UiScreenActions.OwnsRevealedInput(boxScreen))
		{
			Fable2Input.ModalBoxOwnsInput = true;
			Engine_UiScreenActions.RunRevealed(
				boxScreen, Fable2Input.MenuAcceptPressed, Fable2Input.MenuBackPressed);
			return;
		}

		// A HOST SCREEN takes the frame whole over whichever list opened it — the achievements
		// screen, a mod's screen (Engine_UiHostScreen, `Screen:open:<id>`). Before the lists, so
		// its B closes the screen rather than collapsing a fold behind it.
		if (DriveModScreen() || DriveHostScreen(delta))
		{
			return;
		}

		// THE PAUSE MENU, before the front-end gate below — in game there is no front-end menu
		// at all, so the gate would starve it of input the way it starved the load fan once.
		if (DrivePauseMenu(delta))
		{
			return;
		}

		// THE IN-GAME LISTS — abilities, items, weapons, clothing, logbook, options, crime,
		// shop, world map, gift, selection, decoration. After the pause ROOT because they are
		// what the root opens INTO: the root is screen 0 and every one of these is not, so at
		// most one of the two ever stands.
		if (DriveScreenMenu(delta, modal))
		{
			return;
		}

		// WHETHER B IS OURS THIS FRAME — see Fable2Input.FrontEndSwallowsBack. Computed before
		// the guard so it is cleared the moment the front end is not the thing on screen.
		// `_newGameCommitPending`: the guest is at the root with the rows down and the New Game
		// post queued; its B would leave for the title under the commit. A list book (Multiplayer,
		// Active Mods) or the host page book answers B itself by closing: the guest sits at its
		// root under it with the rows hidden, so its B (0x826dfb88) would answer "at root" and
		// leave for the title.
		StateFrontEndFold(_menu is not null && _menuView is { Visible: true } && IsRevealed);
		Fable2Input.FrontEndSwallowsBack =
			_menu is not null && _menuView is not null && IsRevealed &&
			(_warningView is { Visible: true } || _cardView is { Visible: true } || _newGameCommitPending ||
			(_replacing && _saveCardView is not { Visible: true }) ||
			OpenListBook is not null || _hostPageBook.IsOpen ||
			(_menuView.Visible && (_menuView.Menu?.CanCollapse == true || _menuView.OpenedDepth > 0)));

		// AND WHAT IT IS SHOWING, for the unattended driver — see Fable2Input.FrontEndDrive. The
		// driver asked the GUEST which slide the menu was on and got a permanent 1, so it kept
		// pressing A at a list it could have read straight off this object. Same place, same
		// cadence, same reason: this class owns the front end, so it states its own condition.
		Fable2Input.FrontEndDrive = new Fable2Input.FrontEndDriveState(
			ListUp: _menu is not null && _menuView is { Visible: true } && IsRevealed &&
				_menu.Visible.Count > 0 &&
				_cardView is not { Visible: true } && _warningView is not { Visible: true },
			CardUp: _cardView is { Visible: true },
			WarningUp: _warningView is { Visible: true },
			Highlighted: _menu?.Highlighted?.Name ?? string.Empty);

		// THE GUEST'S OWN DIALOG, ahead of everything the host owns — see DriveGuestDialog.
		if (DriveGuestDialog())
		{
			return;
		}

		if (_menu is null || _menuView is null || !IsRevealed)
		{
			return;
		}

		// THE WARNING IS MODAL and therefore first of all, ahead of even the card. The guest's
		// box parks its screen in substate 1 and the only way out is the accept button — every
		// other input is dead while it stands. B clears it too, which is ours: the one-button
		// form has nothing else to press, and a box that could only be dismissed by a key we
		// might have mismapped is a deadlock.
		// A box raised by the save-limit flow answers through its own handler (GameUi.SaveLimit.cs).
		if (DriveAnsweredBox())
		{
			return;
		}

		if (_warningView is { Visible: true })
		{
			int warningClick = _warningView.Click();

			// ACCEPT may PROCEED rather than merely dismiss. The two-button form is an offer,
			// not a dead end — "Continue without saving" goes on to the gender card — so the
			// box carries where accepting leads and null means it leads nowhere.
			if (Fable2Input.MenuAcceptPressed || warningClick > 0)
			{
				string? proceed = _warningView.AcceptOpens;
				_warningView.Visible = false;

				if (proceed is not null)
				{
					OpenScreen(proceed);
				}
				else
				{
					_menuView.Visible = true;
				}
			}
			else if ((Fable2Input.MenuBackPressed || warningClick < 0) && _warningView.AllowCancel)
			{
				// Cancel exists only on the two-button form, and the guest's B arm checks that
				// flag before doing anything at all — see AllowCancel. It returns the screen to
				// substate 0 without touching the presentation: back to the list, no card.
				_warningView.Visible = false;
				_menuView.Visible = true;
			}

			return;
		}

		// THE CARD OWNS THE INPUT WHILE IT IS UP, and it is tested BEFORE the menu's own
		// visibility. The card takes the rows off screen, so a gate that required a visible
		// menu first would hand the card no input at all and leave it stuck on the screen it
		// had just put up.
		if (_cardView is { Visible: true })
		{
			DriveCardChoice();
			return;
		}

		// THE LOAD FAN OWNS THE PRESS ONCE THE GUEST HAS OPENED IT, and — like the gender card
		// above — it is tested BEFORE the menu's own visibility rather than after.
		//
		// That ordering is the whole of a bug worth naming, because it is the same one twice. The
		// fan takes the rows off screen, so a gate requiring a visible menu first would hand it no
		// input at all and leave it stuck on the screen it had just put up. The earlier version of
		// this sat below that gate and was reachable only while the rows were still drawn.
		//
		// Choosing "Load Game" puts the guest's screen into substate 5 and it keeps receiving
		// input there, because our input mask covers only the root list. A run showed the
		// consequence exactly: its cursor walked 0..5 and back while every A press re-dispatched
		// "Load Game", because this class still believed the root list was up.
		if (DriveLoadCarousel(delta))
		{
			return;
		}

		// "Replace a save" posted Load Game; the rows stay down until the guest's fan is up.
		if (AwaitingReplaceFan())
		{
			return;
		}

		// THE MULTIPLAYER BOOK OWNS THE INPUT WHILE IT IS UP, and — like the gender card and the
		// load fan above — it is tested BEFORE the menu's own visibility. Opening it takes the
		// rows off screen, so a gate requiring a visible menu first would hand it no input at
		// all and leave it standing with nothing able to close it.
		if (OpenListBook is { } listBook)
		{
			PointerPage(listBook.Book);
			if (listBook.Drive(
					Fable2Input.MenuDirection,
					Fable2Input.MenuAcceptPressed || PointerList(listBook.View),
					Fable2Input.MenuBackPressed,
					Fable2Input.MenuFlipPressed, delta, 0f))
			{
				ReportMenuView(listBook.View);
			}

			// B on its left list closes the screen; the rows come back with it.
			if (!listBook.IsOpen)
			{
				_menuView.Visible = true;
			}

			return;
		}

		if (!_menuView.Visible)
		{
			return;
		}

		// The movement sound rides the view's own move (Engine_FrontEndMenuView.Moved).
		_menuView.Navigate(Fable2Input.MenuDirection, delta);

		bool menuClicked = PointerList(_menuView);

		// THE SOURCED ROWS, RE-READ EVERY FRAME. Friends come and go, a search answers over
		// seconds, and a fold built once shows none of it — the row list was static until the
		// menu was rebuilt, which is why a search only appeared after leaving Games and coming
		// back. `RefreshSources` compares the set and does nothing when it has not moved, so a
		// settled list costs one walk; `PollSearch` drains the native queue, which empties on
		// read and so has to be collected whether or not the fold is open.
		bool sourcesMoved = Engine_UiNetworkSource.PollSearch();
		if (_menu is not null &&
			Engine_UiTemplates.Menu("frontend-root") is { } frontEnd &&
			frontEnd.RefreshSources(_menu))
		{
			sourcesMoved = true;
		}

		if (sourcesMoved)
		{
			_menuView.Refresh();

			// WHAT THE FOLDS ACTUALLY HOLD. A sourced entry with no children does not fold at
			// all — it dispatches as a command row — and that is indistinguishable on screen
			// from a fold that opens onto nothing, so the counts are reported rather than
			// inferred. Only on a change, so a settled menu is silent.
			GD.Print("[game-ui] content rows: " + string.Join(" ", new[]
			{
				"Mods", "Friends", "Requests", "Invites", "Games",
			}.Select(name => $"{name}={_menu?.Find(name)?.Children.Count ?? -1}")));
		}

		// The front end's rows, reported the same way as the pause root's. Both views are the
		// same class on the same slot table, so the front end — which draws correctly — is the
		// control: a difference between the two lines localises a defect in one report.
		ReportMenuView(_menuView);
		Engine_UiModMenus.Focus(_menuView, Fable2FrontEndRoot.RootTemplate, Engine_UiCommandList.FrontEnd);

		if (Fable2Input.MenuBackPressed)
		{
			// A false return is "nothing left to back out of". Inside a mod's menu that closes
			// the menu; on the front end's root it is the guest's business, not ours — B there
			// leaves for the title screen, and the guest is fed the same B press through XInput.
			// A fold closed here is the guest's `OnPressB` arm, which slides the blade out
			// (`SE_GUI_BLADE_SLIDE_OUT`, 0x826DFC80 then 0x826DFBDC).
			if (_menuView.Back())
			{
				Cue(frontEnd: true, "bladeout");
			}
			else
			{
				Engine_UiModMenus.Back(_menuView);
			}
		}

		if (!Fable2Input.MenuAcceptPressed && !menuClicked)
		{
			return;
		}

		Engine_UiExpandableMenu.Item? frontChosen = _menuView.Menu?.Highlighted;
		string? action = _menuView.Activate();
		if (Engine_UiModMenus.Activated(_menuView, Fable2FrontEndRoot.RootTemplate, frontChosen, action,
				Engine_UiCommandList.FrontEnd))
		{
			return;
		}

		// A on any root row slides the blade in (`SE_GUI_BLADE_SLIDE_IN`, 0x826DF428) before the
		// press reaches Lua; A is ours in this substate, so the guest's arm never plays it.
		Cue(frontEnd: true, "bladein");

		// The guest confirms through the pump's next sample; flipping now keeps the marker on
		// the row the player just chose instead of one sample behind their press.
		if (action is "SubtitlesOn" or "SubtitlesOff")
		{
			ApplySubtitlesEmphasis(action == "SubtitlesOn");
		}

		// THE ONE DISPATCHER, AHEAD OF THE REFUSAL PIPE. `QuitToDesktop` and the multiplayer rows
		// (`Net:`) are host-owned verbs (GameUiCommands) — the guest's Marketplace branch is a
		// stubbed XAM surface that draws nothing here — and a mod's row may name any verb. HANDLED,
		// THEN FALL THROUGH: the refusal pipe below is what SHOWS a message a verb raised, so
		// returning here put the box one press late. A command no verb claims is the guest's row
		// name and is posted to it below.
		bool handledHere = false;
		if (action is not null)
		{
			string frontRow = frontChosen?.Name ?? string.Empty;
			handledHere = Engine_UiCommands.Run(action, new Engine_UiCommandContext(
				Engine_UiCommandList.FrontEnd, Fable2FrontEndRoot.RootTemplate, frontRow, Engine_UiModMenus.MenuPlayer,
				Engine_UiTemplates.MenuOwner(Fable2FrontEndRoot.RootTemplate, frontRow)) { View = _menuView })
				.Outcome != Engine_UiCommandOutcome.NotOurs;
		}

		// A REFUSAL, before either of the two outcomes below. The guest's New Game branch runs
		// its storage guards ahead of the gender card, so an entry can decline to open the very
		// screen it names — and the reason is a Babel tag, which is what the guest puts through
		// `dialog.Group.yousure`.
		if (_menu?.TakeWarning() is { } warning)
		{
			// The save-limit refusal is the guest's ONE two-button warning on this path
			// (`r6 = 1` at 0x826E01A4) and it labels its accept button GUI_DEVICE_REMOVED_CONTINUE
			// — a generic "Continue", reused. Every other storage refusal passes `r6 = 0` with
			// GUI_OK. Carried explicitly rather than derived, because it is the guest's flag.
			bool limit = warning == Fable2FrontEndRoot.SaveLimitWarning;
			string button = limit ? "GUI_DEVICE_REMOVED_CONTINUE" : "GUI_OK";

			if (_warningView is null && _presentation?.Theme is { } warningTheme)
			{
				AdoptWarning(warningTheme);
			}

			if (_warningView is not null && limit)
			{
				// Three answers here, not the guest's two: see GameUi.SaveLimit.cs.
				ShowSaveLimitBox(warning);
			}
			else if (_warningView is not null)
			{
				_warningView.Show(_presentation?.Theme, warning, button, twoButton: limit);

				// ONLY A NEW GAME REFUSAL PROCEEDS. Both of its forms go on to the card — each
				// parks `+116 = 0, +120 = 2` on the screen and the accept arm routes that pair
				// to "choosecard" whatever the button count — but this pipe now also carries
				// host-authored messages, and those must dismiss to the list. Setting it
				// unconditionally made A on the friend-code box start a new game.
				_warningView.AcceptOpens =
					warning == Fable2FrontEndRoot.NoProfileWarning ||
					warning == Fable2FrontEndRoot.SaveLimitWarning
						? Fable2FrontEndRoot.HeroCardScreen
						: null;
				_menuView.Visible = false;
			}

			GD.Print($"[game-ui] New Game refused — {warning} " +
				$"(saves {_menu.Saves.Count}/{_menu.Saves.Limit}, " +
				$"signed in {_menu.Saves.SignedIn}) — " +
				$"{(_warningView is null ? "NO BOX to show it in" : _warningView.LastReport)}");
			return;
		}

		// A SCREEN the entry opens, before the null test below — an entry that opens one also
		// returns null, and treating the two the same is what made "New Game" do nothing
		// visible while quietly starting a game.
		if (_menuView.TakeOpenedScreen() is { } screen)
		{
			OpenScreen(screen);
			return;
		}

		if (action is null || handledHere)
		{
			// Expanded a submenu, collapsed one, or a host-only entry the guest knows nothing
			// about. All three are complete here — there is nothing to tell the guest.
			return;
		}

		Post(action, "menu");
	}

	/// <summary>
	/// One frame of the Load Game card fan. True when it consumed the input.
	///
	/// THE GUEST OPENS IT; GODOT DRAWS IT. That split is not a compromise — it is the only
	/// division the enumeration allows. Listing a profile's saves is `sub_82443308`, a live
	/// `XamContentCreateEnumerator`, and building each card's text is `sub_826E14F8`, which
	/// mounts every container and parses its `ChapterSave.bin`. Both are guest CALLS, both run
	/// inside the dispatcher's own "Load Game" branch, and neither is something a host can do
	/// for itself. So "Load Game" still dispatches by name, the guest still does the work, and
	/// what Godot takes over is the part that was never the guest's to begin with once the front
	/// end changed hands: the cards on screen and the cursor moving between them.
	///
	/// SUBSTATE 5 IS THE SIGNAL — `screen+24`, set at 0x826E07E0 once the branch has enumerated
	/// more than one save. A single save never reaches it: that path commits directly at
	/// 0x826E0470, which is why this returning false during a one-save load is correct rather
	/// than a gap.
	///
	/// B IS LEFT TO THE GUEST, as it was. Its own handler maps B in this substate to UNFREEZE,
	/// which puts `screen+24` back to 0 — and it is already being fed the press, since the input
	/// mask covers only the root list. Posting a second back-out would drive one transition
	/// twice; the fan comes down on the next frame when the substate is no longer 5.
	/// </summary>
	private bool DriveLoadCarousel(double delta)
	{
		if (!HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen, out uint menuState, out _,
				out uint guestCursor) ||
			screen != Engine_UiModel.FrontEndScreen.MainMenu || menuState != LoadCarouselState)
		{
			// The guest has left the carousel — by its own B, or because a save was committed.
			// Taking the fan down here rather than on our own B press is what keeps the two in
			// step whichever of them ended it.
			CloseSaveCards();
			return false;
		}

		if (_saveCardView is null && _presentation?.Theme is { } saveTheme)
		{
			AdoptSaveCards(saveTheme);
		}

		if (_saveCardView is null)
		{
			return false;
		}

		// POPULATED ONCE PER OPENING. The guest states the list before the mode: the text builder
		// (`sub_826E14F8`, 0x826E0448) pushes one text per file (Engine_FrontEndStated.SaveText),
		// StampSaveCard runs for saves 0 .. min(count, 6) - 1 (Engine_FrontEndStated.Card) and the
		// dispatch hook states the rest the same way (frontend_stated.cpp StateLaterPortraits), and
		// only then is the mode 5 (0x826E07E0). The statements ride the native-call queue, so the
		// mode can be seen a frame before they are drained.
		if (!_saveCardView.Visible)
		{
			if (_frontEndStated.SavesSince(_saveCardsClosedAt) is not { Count: > 0 } texts ||
				_frontEndStated.CardsSince(_saveCardsClosedAt, texts.Count) is not { } stated)
			{
				if (!_saveCardsUnstatedReported)
				{
					_saveCardsUnstatedReported = true;
					GD.Print("[game-ui] the guest is in its save carousel and has not stated every " +
						"save and card yet");
				}

				return true;
			}

			_saveCardsUnstatedReported = false;
			var saves = new System.Collections.Generic.List<Engine_FrontEndSaveCards.Save>();
			// THE FILE NAMES, as the guest handed each to its header reader: the export copy is
			// named by them. A save's mods are told on the page that opens when it is picked
			// (ModSaveGate), never on the card.
			_saveCardNames.Clear();
			for (int i = 0; i < texts.Count; ++i)
			{
				_saveCardNames.Add(texts[i].File);
				saves.Add(new Engine_FrontEndSaveCards.Save(
					texts[i].Text,
					i < stated.Count ? CardTexture(stated[i].Texture) : null));
			}

			_saveCardsOpenedAt = _frontEndStated.Sequence;
			_saveCardView.PageTemplate = Said(PageText);
			_saveCardView.MarkText = Said(ChosenText);
			OpenSaveCards(saves, stated[0].Slot);
			if (_replacing)
			{
				ShowReplaceHeading();
			}

			GD.Print($"[game-ui] the guest opened the load fan with {texts.Count} save(s) from card " +
				$"{stated[0].Slot}, its cursor at {guestCursor} — {_saveCardView.Describe()}");
		}

		// WHAT THE FAN IS BEING TOLD, reported when it changes — the gender card's diagnostic,
		// for the same reason: a fan that does not move on left/right is either never handed the
		// direction or ignoring it, and only the input side is invisible from a screenshot.
		int sideways = Fable2Input.MenuHorizontal;
		if (sideways != _lastFanDirection)
		{
			_lastFanDirection = sideways;
			GD.Print($"[game-ui] load fan input {sideways:+#;-#;0} — {_saveCardView.Describe()}");
		}

		if (_saveCardView.Navigate(sideways, delta))
		{
			GD.Print($"[game-ui] load fan moved — {_saveCardView.Describe()}");
		}

		// The mouse: pointing at a card lifts it, clicking a card loads it. A click on no card
		// is taken here too, so it cannot fall through as A on whichever card is lifted. A click on
		// a page arrow, or the wheel, turns the page (DrivePages).
		bool fanClicked = false;
		Vector2? pointer = Engine_UiPointer.Canvas;
		bool leftClick = pointer is not null && Engine_UiPointer.TakeLeft();
		DrivePages(pointer, leftClick, out bool pageClick);
		if (pointer is { } fanAt)
		{
			int pointed = _saveCardView.SaveAt(fanAt);
			if (pointed >= 0 && Engine_UiPointer.Moved)
			{
				_saveCardView.Select(pointed);
			}

			if (leftClick && !pageClick && pointed >= 0)
			{
				_saveCardView.Select(pointed);
				fanClicked = true;
			}
		}

		// The replace pick: A (or a click) picks a card; nothing is loaded or exported.
		if (_replacing)
		{
			if ((Fable2Input.MenuAcceptPressed || fanClicked) && _saveCardView.Activate() is var picked && picked >= 0)
			{
				PickForReplace(picked);
			}

			return true;
		}

		// X on a card: "Copy for Xbox / Xenia" — the save's guest files, vanilla-only, beside the
		// content root; the original is untouched (ModSaveGate.ExportForConsole).
		if (Fable2Input.MenuEquipPressed && _saveCardView.Activate() is var exported && exported >= 0 &&
			exported < _saveCardNames.Count)
		{
			Engine_UiHostBoxes.Tell(Refii.Mods.ModSaveGate.ExportForConsole(_saveCardNames[exported]));
			return true;
		}

		if ((Fable2Input.MenuAcceptPressed || fanClicked) &&
			_saveCardView.Activate() is var chosen && chosen >= 0)
		{
			// THE INDEX IS THE GUEST'S JOIN. It identifies a save by the address of its handle
			// slot — `Frontend_SetStartGameTarget` takes `*(screen+84) + 4*i` and
			// `*(screen+148) + 40*i + 12` — so the index is what both sides already agree on,
			// and our fan is populated in that same enumeration order.
			uint queued = HostBridge.LoadSave(chosen);
			GD.Print($"[game-ui] load fan: save {chosen} chosen — posted (queue depth {queued})");
		}

		// Consumed either way. A press that is not A still belongs to the fan, and letting it
		// fall through would move the root list's highlight underneath the cards.
		return true;
	}

	/// <summary>The fan opened from statements up to here; a later opening takes only newer ones.</summary>
	private ulong _saveCardsOpenedAt;

	private ulong _saveCardsClosedAt;

	private bool _saveCardsUnstatedReported;

	/// <summary>
	/// A stated card texture: the save's screenshot or the guest's blank, by resource hash. A hash
	/// the name table does not know or a texture the banks do not carry keeps the artist's
	/// placeholder (null).
	/// </summary>
	private Texture2D? CardTexture(uint hash) =>
		hash != 0 && _presentation?.TextureNames is { } names && names.TryResolve(hash, out string path)
			? _presentation.LoadTexture(path)
			: null;

	/// <summary>
	/// Puts the fan up with the guest's own enumerated list, and takes the rows down.
	///
	/// The rows go for the reason the gender card's do — see <see cref="_cardView"/>: the file
	/// puts every menu element at z 0 and the cards reach z -100, so the list would cut straight
	/// across them, and no shipped slide parks the menu anywhere this could reuse.
	/// </summary>
	private void OpenSaveCards(
		System.Collections.Generic.List<Engine_FrontEndSaveCards.Save> saves, int firstSlot)
	{
		if (_saveCardView is null)
		{
			return;
		}

		_saveCardView.SetSaves(saves, firstSlot);
		_saveCardView.Visible = true;

		if (_menuView is not null)
		{
			_menuView.Visible = false;
		}
	}

	/// <summary>
	/// Takes the fan down and gives the rows back. Idempotent — it runs on every frame the
	/// guest is not in substate 5, which is nearly all of them.
	/// </summary>
	private void CloseSaveCards()
	{
		if (_saveCardView is not { Visible: true })
		{
			return;
		}

		_saveCardView.Visible = false;
		_saveCardsClosedAt = _saveCardsOpenedAt;
		EndReplace();

		// Not over the gender card, and not over the running game. The menu comes back only if
		// it is what the player would be looking at.
		if (_menuView is not null && _cardView is not { Visible: true } && !HasInGameUi)
		{
			_menuView.Visible = true;
		}

		GD.Print("[game-ui] the guest left its save carousel — the load fan is down");
	}

	/// <summary>
	/// `screen+24` while the guest's save carousel is up — set at 0x826E07E0 for two or more
	/// saves. The one-save case never reaches it; that path commits directly at 0x826E0470.
	/// </summary>
	private const uint LoadCarouselState = 5;

	/// <summary>
	/// Sends one named action to the guest.
	///
	/// POSTED, not called. The name goes onto the guest's tick-thread queue and the guest
	/// performs it inside its own tick. Nothing here touches guest state, which is what keeps
	/// this side as safe as it has always been — the bridge still only reads and writes memory
	/// from Godot's thread, and the one place that makes a guest CALL is the guest's own thread.
	///
	/// A name the guest has no action for is dropped by the pump with a log line rather than
	/// silently, so an entry that does nothing says so.
	/// </summary>
	/// <summary>
	/// Marks the current subtitles arm the way `sub_826E2220` does with SetItemEmphasis.
	/// </summary>
	/// <param name="known">The setting, or null to ask the bridge for the guest's answer.</param>
	private void ApplySubtitlesEmphasis(bool? known = null)
	{
		if (_menu is null)
		{
			return;
		}

		bool? on = known ?? HostBridge.GetSubtitles();
		if (on is null)
		{
			return;  // not sampled yet — leave both unmarked rather than guess
		}

		if (_menu.Find("SubtitlesOn") is { } onItem)
		{
			onItem.Emphasised = on.Value;
		}

		if (_menu.Find("SubtitlesOff") is { } offItem)
		{
			offItem.Emphasised = !on.Value;
		}
	}

	private void Post(string action, string from)
	{
		uint queued = HostBridge.PostFrontEndAction(action);
		GD.Print($"[game-ui] {from} activated '{action}' — posted to the guest tick pump " +
			$"(queue depth {queued})");
	}

	/// <summary>
	/// Opens a front-end screen a menu entry named.
	///
	/// An unknown name is reported rather than ignored: an entry that opens nothing looks
	/// exactly like an entry whose press was dropped, and those need different fixes.
	/// </summary>
	/// <summary>
	/// One command from the content fold — a mod row, a friend, a request, an invite or a
	/// session search. The suffix is `verb` or `verb:argument`.
	/// </summary>
	/// <remarks>
	/// EVERY ANSWER GOES ON THE ROW'S PAGE (<see cref="Engine_UiNetworkSource.Say"/>). The
	/// front end's warning box draws UNDER the multiplayer book and blocks it, so nothing on
	/// this path raises one.
	/// </remarks>
	private void DispatchNetwork(string command)
	{
		int colon = command.IndexOf(':');
		string verb = colon < 0 ? command : command[..colon];
		string argument = colon < 0 ? string.Empty : command[(colon + 1)..];

		switch (verb)
		{
			case "code":
				// A COPIES. The code is drawn on the page beside this row, so there is nothing
				// for a box to add — it would only be another press to dismiss.
				if (NetBridge.Self() is { FriendCode.Length: > 0 } self)
				{
					DisplayServer.ClipboardSet(self.FriendCode);
					Engine_UiNetworkSource.MarkCopied();
				}
				else
				{
					Engine_UiNetworkSource.Say("RexNet is not up, so there is no friend code yet.\n\n" +
						"Check the RexNet settings in the launcher.");
				}

				break;

			case "book":
				// THE FRONT END HOSTS THE SAME SCREEN as the pause tab. `_books` is a cache keyed
				// by screen id, not an in-game fixture, so the book stands here too.
				if (_multiplayer.Open())
				{
					// Null-conditional: this dispatch is shared with the pause book's own rows,
					// and there is no front-end list to hide on that path.
					if (_menuView is not null)
					{
						_menuView.Visible = false;
					}
				}
				else
				{
					// No page to answer on yet: the book is what failed to open.
					GD.PushWarning("[game-ui] multiplayer book not ready — art still loading");
				}

				break;

			case "paste":
				// EVERY ANSWER GOES ON THE PAGE. The clipboard is the input — a friend code is
				// ~100 characters that arrives by being pasted — and the confirm is a SECOND
				// press on this same row rather than a box, so nothing here opens a modal.
				if (Engine_UiNetworkSource.PendingAdd is { Length: > 0 } confirmed)
				{
					// The name rides the code; kept so a decline can say who it came from.
					if (NetBridge.PreviewCode(confirmed) is { } target)
					{
						Engine_UiNetworkSource.RememberSent(target.PeerId, target.Name);
					}

					Engine_UiNetworkSource.SetAddState(
						NetBridge.AddFriendByCodeResult(confirmed, string.Empty) switch
						{
							1 => "Friend request sent.\n\nThey appear under Friends once they accept.",
							2 => "That is your own friend code.\n\nIf this came from another install, " +
								"that install is a copy of this one and shares its identity: delete " +
								"its assets/saves/<profile>/rexnet/identity.key and restart it.",
							_ => "That request could not be sent — RexNet may be off.",
						});
					break;
				}

				string pasted = DisplayServer.ClipboardGet().Trim();
				if (NetBridge.PreviewCode(pasted) is { } who)
				{
					// The name is the code owner's own claim, so the peer id is shown with it.
					Engine_UiNetworkSource.SetAddState(
						$"Add {(who.Name.Length == 0 ? "this player" : who.Name)}?\n" +
						$"{who.PeerId}\n\nA sends the request.",
						pasted);
				}
				else
				{
					Engine_UiNetworkSource.SetAddState(pasted.Length == 0
						? "Your clipboard is empty.\n\nCopy a friend code and choose this again."
						: "That is not a friend code.\n\nCopy the whole REXN- code and try again.");
				}

				break;

			case "status":
				Engine_UiJoinRequests.StatusIndex = int.TryParse(argument, out int status) ? status : 0;
				Engine_UiNetworkSource.Say($"Your status is now: {Engine_UiJoinRequests.Statuses[Engine_UiJoinRequests.StatusIndex]}.");
				break;

			case "greet":
				{
					int greeting = int.TryParse(argument, out int id) ? id : 0;
					Answer(greeting >= 1 && greeting <= Engine_UiJoinRequests.Greetings.Length && NetBridge.SendPlayerSignal(greeting),
						"Sent to your friends.", "That could not be sent — RexNet is not up.");
					break;
				}

			case "joinmode":
				{
					var mode = (Engine_UiJoinRequests.JoinMode)System.Math.Clamp(
						int.TryParse(argument, out int wanted) ? wanted : 0, 0, 2);
					Engine_UiJoinRequests.Mode = mode;
					Answer(NetBridge.SetJoinable(mode == Engine_UiJoinRequests.JoinMode.Anyone),
						mode switch
						{
							Engine_UiJoinRequests.JoinMode.Anyone => "Friends can now join your game.",
							Engine_UiJoinRequests.JoinMode.InvitesOnly => "Only someone you invite can join now.",
							_ => "No one can join now; asks to join are declined.",
						},
						"That could not be changed — no game is hosted yet.");
					break;
				}

			case "joinable":
				// The row states the CURRENT setting, so its command carries the one it wants.
				Answer(NetBridge.SetJoinable(argument == "1"),
					argument == "1"
						? "Friends can now join your game."
						: "Your game is closed to joiners.",
					"That could not be changed — no game is hosted yet.");
				break;

			case "search":
				Engine_UiNetworkSource.BeginSearch();
				Engine_UiNetworkSource.Say(NetBridge.Available
					? "Searching.\n\nResults appear in this list as they answer."
					: "RexNet is not up, so there is nothing to search.");
				break;

			case "accept":
				Answer(NetBridge.PeerAction(argument, NetBridge.Act.AcceptRequest),
					"Friend request accepted.", "That friend request could not be accepted.");
				break;

			case "dismiss":
				Answer(NetBridge.PeerAction(argument, NetBridge.Act.DismissRequest),
					"Friend request dismissed.", "That friend request could not be dismissed.");
				break;

			case "join-allow":
				WithdrawJoinQuestion(argument);
				Answer(Engine_UiJoinRequests.Answer(argument, true),
					"Invite sent through Discord.\n\nThe game joins when they accept it.",
					"That ask could not be answered; it may have lapsed.");
				break;

			case "join-deny":
				WithdrawJoinQuestion(argument);
				Answer(Engine_UiJoinRequests.Answer(argument, false),
					"Declined.", "That ask could not be answered; it may have lapsed.");
				break;

			case "accept-invite":
				if (!ConfirmLeavingGame(command, argument))
				{
					break;
				}

				Answer(NetBridge.PeerAction(argument, NetBridge.Act.AcceptInvite),
					"Invitation accepted.\n\nThe game joins when the host is ready.",
					"That invitation could not be accepted.", hold: true);
				break;

			case "join":
				// THE SAME PATH AN INVITE TAKES. The guest joins from an accepted invite, and the
				// material that needs — host peer plus session id — is what presence carries for
				// a friend who is hosting. The node raises the accept for us.
				if (!ConfirmLeavingGame(command, argument))
				{
					break;
				}

				Answer(NetBridge.PeerAction(argument, NetBridge.Act.Join),
					"Joining.\n\nThe game loads when the host answers.",
					"They are not hosting a game you can join right now.", hold: true);
				break;

			case "about":
				// NOTHING TO RAISE. The placeholder row's page already explains the section
				// (Engine_UiNetworkSource.Detail for `none:`), so the press is a no-op.
				break;

			case "mods":
				// THE ACTIVE MODS BOOK: the enabled mods on the left page, the highlighted one's
				// description on the right.
				if (_modsBook.Open())
				{
					if (_menuView is not null)
					{
						_menuView.Visible = false;
					}
				}
				else
				{
					GD.PushWarning("[game-ui] Active Mods book not ready — art still loading");
				}

				break;

			case "mod":
				// READ-ONLY, answered on the page: the load order is the launcher's to write, and
				// editing it from two places would be two sources of truth.
				Engine_UiNetworkSource.Say("Mods are turned on and ordered in the launcher.");
				break;

			default:
				GD.PushWarning($"[game-ui] content command '{command}' has no dispatch path");
				break;
		}

		GD.Print($"[game-ui] content command '{command}' handled");
	}

	private static void Answer(bool ok, string good, string bad, bool hold = false) =>
		Engine_UiNetworkSource.Say(ok ? good : bad, hold: ok && hold);

	/// <summary>The join a first press armed, and when; a second press of the same row joins.</summary>
	private string _joinConfirmArmed = string.Empty;

	private ulong _joinConfirmAt;

	private const ulong JoinConfirmMsec = 10_000;

	/// <summary>
	/// FABLE 3'S JOIN WARNING (`GUI_JOIN_DESTRUCTIVE_ACTION_*`): joining someone leaves the game you
	/// are in, and what you have not saved goes with it. Said on the row's page — never a box over
	/// the book — and the join goes ahead on a second press of the same row within ten seconds. No
	/// warning from the title or the front end, where there is nothing to lose, or once already
	/// joined to someone.
	/// </summary>
	private bool ConfirmLeavingGame(string command, string argument)
	{
		if (!HostBridge.IsInGame || CoopSession.IsClient)
		{
			return true;
		}

		string key = command + ":" + argument;
		ulong now = Time.GetTicksMsec();
		if (_joinConfirmArmed == key && now - _joinConfirmAt <= JoinConfirmMsec)
		{
			_joinConfirmArmed = string.Empty;
			return true;
		}

		_joinConfirmArmed = key;
		_joinConfirmAt = now;
		Engine_UiNetworkSource.Say("Joining another game leaves this one, and anything you have not saved is lost." +
			"\n\nPress again to join.");
		return false;
	}

	/// <summary>
	/// One frame of a mod's menu opened over a screen's list (<see cref="Engine_UiModMenus"/>): the
	/// stick moves the view, A runs the row, B collapses a fold or closes the menu. The guest's
	/// stick latch is released, since none of these rows are in its pool.
	/// </summary>
	private void DriveOpenedModMenu(Engine_FrontEndMenuView view, string rootMenu, double delta)
	{
		view.Navigate(Fable2Input.MenuDirection, delta);
		bool clicked = PointerList(view);
		ReportMenuView(view);
		if (DriveRowActionsOn(view.OpenedMenuName ?? rootMenu, view.Menu?.Highlighted, view,
				_screenMenu?.ScreenId ?? 0u, Engine_UiCommandList.Screen))
		{
			return;
		}

		if (Fable2Input.MenuBackPressed)
		{
			if (!view.Back())
			{
				Engine_UiModMenus.Back(view);
			}

			return;
		}

		if (!Fable2Input.MenuAcceptPressed && !clicked)
		{
			return;
		}

		Engine_UiExpandableMenu.Item? chosen = view.Menu?.Highlighted;
		string? action = view.Activate();
		Engine_UiModMenus.Activated(view, rootMenu, chosen, action, Engine_UiCommandList.Screen);
		view.Refresh();
	}

	private void OpenScreen(string screen)
	{
		if (screen == Fable2FrontEndRoot.HeroCardScreen && _cardView is null &&
			_presentation?.Theme is { } cardTheme)
		{
			AdoptCardChoice(cardTheme);
		}

		if (screen == Fable2FrontEndRoot.HeroCardScreen && _cardView is not null)
		{
			// Reset on the way IN, not on the way out. The guest re-enters through the same
			// `Intro` slide and substate 2 every time, whatever the player did last, so a
			// cancelled choice must not be waiting when the card is opened again.
			_cardView.Reset();
			_cardView.Visible = true;

			// New Game past its guards: the narrator's line as the card comes in
			// (`TEXT_QUEST_QC010_FRONT_END_20`, 0x826E0244).
			Cue(frontEnd: true, "newgame");

			// The rows out of the way while the card is up — see the field's remarks for why
			// that is ours rather than the original's.
			if (_menuView is not null)
			{
				_menuView.Visible = false;
			}

			GD.Print($"[game-ui] the gender card is up — {_cardView.Describe()}");
			return;
		}

		GD.PushWarning($"[game-ui] menu entry asked for screen '{screen}', which nothing here " +
			"draws — the entry will appear to do nothing");
	}

	/// <summary>
	/// One frame of the gender card.
	///
	/// B closes it, which is the guest's own answer too: its B arm sets the `Outro` slide from
	/// any of the three card substates, and `Outro` switches on the group's `BehaviorUnFreeze`
	/// element, whose event drives the screen back to substate 0 — the menu list.
	///
	/// A with NEITHER card up does nothing, and that is read rather than invented: the guest's
	/// A arm has no case for substate 2 and falls through to `default: return` at
	/// `0x826DF3E8`. So the panel opens with a real question and will not answer it for you.
	/// </summary>
	/// <summary>Last direction reported to the card, so the trace fires on change only.</summary>
	private int _lastCardDirection;

	private int _lastFanDirection;

	/// <summary>The load fan's save file names in card order, read when it opened (ABI 218).</summary>
	private readonly System.Collections.Generic.List<string> _saveCardNames = new();

	private void DriveCardChoice()
	{
		if (_cardView is null)
		{
			return;
		}

		// WHAT THE CARD IS BEING TOLD, reported when it changes.
		//
		// The panel draws but has been seen not responding to left and right, and the two
		// explanations need opposite fixes: either the direction never arrives here — the
		// masking, the pad mapping, the axis fold — or it arrives and the pose does not follow.
		// Only the input side is invisible from a screenshot, so it is the side that says so.
		// Keyed on change, because this runs at the display rate.
		int sideways = Fable2Input.MenuHorizontal;
		if (sideways != _lastCardDirection)
		{
			_lastCardDirection = sideways;
			GD.Print($"[game-ui] gender card input {sideways:+#;-#;0} — {_cardView.Describe()}");
		}

		Engine_FrontEndCardChoice.Choice raisedBefore = _cardView.Chosen;
		if (_cardView.Navigate(sideways))
		{
			GD.Print($"[game-ui] gender card moved — {_cardView.Describe()}");
		}

		// The mouse: pointing raises a card, clicking one chooses it; a click elsewhere is
		// taken so it cannot confirm the raised card by accident.
		bool cardClicked = false;
		if (Engine_UiPointer.Canvas is { } cardAt)
		{
			Engine_FrontEndCardChoice.Choice pointed = _cardView.ChoiceAt(cardAt);
			if (Engine_UiPointer.Moved)
			{
				_cardView.Choose(pointed);
			}

			if (Engine_UiPointer.TakeLeft() && pointed != Engine_FrontEndCardChoice.Choice.Neither)
			{
				_cardView.Choose(pointed);
				cardClicked = true;
			}
		}

		// A card raised or swapped turns the page (`SE_GUI_NEW_PAGE`, 0x826DF380 and its three
		// siblings), by stick or by pointer.
		if (_cardView.Chosen != raisedBefore)
		{
			Cue(frontEnd: true, "page");
		}

		if (Fable2Input.MenuBackPressed)
		{
			// The card's B slides the blade out (`SE_GUI_BLADE_SLIDE_OUT`, 0x826DFBDC).
			Cue(frontEnd: true, "bladeout");

			// The guest sits in substate 0 under the card; its B is withheld while the card
			// stands (Fable2Input.FrontEndSwallowsBack), so this press cannot also leave for the title.
			CloseCardChoice();
			GD.Print("[game-ui] the gender card was cancelled — back to the menu");
			return;
		}

		if (!Fable2Input.MenuAcceptPressed && !cardClicked)
		{
			return;
		}

		if (_cardView.Activate() is not { } chosen)
		{
			// Neither card is up. Nothing to confirm, and nothing to say — pressing A on an
			// unanswered question is not a fault.
			return;
		}

		// The choice's line (`..._30_HM` 0x826DF5E8 / `..._30_HF` 0x826DF734), then the commit once
		// the card's own slide has run (`ChooseBoy` 600 ms, `ChooseGirl` 603 ms), as the guest
		// commits after it: the line is not cut by the transition.
		bool boy = _cardView.Chosen == Engine_FrontEndCardChoice.Choice.Boy;
		Cue(frontEnd: true, boy ? "boy" : "girl");
		// The card goes down now; the rows stay down until the post has gone out
		// (`_newGameCommitPending`, part of ModalPanelUp), so nothing is pressable meanwhile.
		_cardView.Visible = false;
		_newGameCommitPending = true;
		GetTree().CreateTimer(boy ? 0.600 : 0.603).Timeout += () =>
		{
			Post(chosen, "gender card");
			_newGameCommitPending = false;
			CloseCardChoice();
		};
	}

	/// <summary>
	/// Plays one of the guest's own UI sounds for a press Godot handled itself (the pump's
	/// `UiCue:`): on the front end's emitter or the in-game GUI's.
	/// </summary>
	private static void Cue(bool frontEnd, string cue) =>
		HostBridge.PostFrontEndAction($"UiCue:{(frontEnd ? "fe" : "game")}:{cue}");

	/// <summary>The one movement sound for any list or value Godot moves (-1 up, 1 down).</summary>
	public static void MoveCue(int direction) =>
		Cue(frontEnd: !HostBridge.IsInGame, direction < 0 ? "slideup" : "slidedown");

	/// <summary>
	/// Takes the card down and gives the rows back.
	///
	/// One place, because a card closed without restoring the menu leaves the front end with
	/// nothing on it and no way back — and there are two ways out of the panel.
	/// </summary>
	private void CloseCardChoice()
	{
		if (_cardView is not null)
		{
			_cardView.Visible = false;
		}

		if (_menuView is not null)
		{
			_menuView.Visible = true;
		}
	}

	private string? _showing;

	/// <summary>
	/// Follows the guest's stated UI: which presentations are live, which screen is up, and the
	/// owned lists standing on them. Reads only what the guest's own calls stated (the told live
	/// list, the stated screen set, the `g_Menu` statements) — no guest memory.
	///
	/// FOLLOWED ON CHANGE, never on a timer. Every input says when it moved, and a follow runs on
	/// the guest frame one of them did — see <see cref="FollowInputs"/>. A follow that leaves the
	/// stated screen without its presentation or its list is repeated on the next guest frame
	/// (<see cref="FollowUnsettled"/>).
	/// </summary>
	private void TracePresentations()
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		FollowKey inputs = FollowInputs();
		bool moved = inputs != _followInputs;
		_followInputs = inputs;
		if (moved)
		{
			_unsettledFrames = 0;
		}
		else if (!FollowUnsettled())
		{
			return;
		}

		System.Collections.Generic.List<Engine_UiModel.Presentation> live =
			HostBridge.GetPresentations();
		_statedHud = StatedHudPresentation(live);
		AddStatedScreens(live);

		// A presentation the registry has dropped takes its menu rows with it. The menu model is
		// keyed by address and the guest reuses addresses, so a screen rebuilt where its last
		// instance stood would otherwise inherit that instance's rows. Only an address this
		// follow has itself seen live is forgotten: rows can land for a presentation no follow has
		// listed yet, and those must stand.
		foreach (Engine_UiModel.Presentation p in live)
		{
			_menuModelSeenLive.Add(p.Address);
		}

		_menuModelSeenLive.RemoveWhere(address =>
		{
			if (live.Exists(p => p.Address == address))
			{
				return false;
			}

			_menuModel.Forget(address);
			return true;
		});

		// Standing the screens comes first; the report below fires only when the live set itself
		// changed, so a stable screen does not fill the log.
		if (!HostBridge.FrontEndGuiIsUp)
		{
			FollowInGameUi(live);
		}
		else
		{
			HasInGameUi = false;
			FollowFrontEndExtras(live);
		}

		// WHICH SCREEN THE GUEST SAYS IT IS ON, reported on change and separately from the set
		// of live presentations — because for the in-game screens those two are not the same
		// claim. The pause menu's presentation is created once at game entry and never freed,
		// so it is in the list below from the first frame of play; only this says whether the
		// player is actually in it.
		Engine_UiModel.InGameScreens? manager = HostBridge.GetInGameScreens();
		Engine_UiModel.InGameScreen? screen = manager?.Current;
		_screenOwners = manager?.Owned;
		if (screen != _inGameScreen)
		{
			_inGameScreen = screen;
			if ((uint?)screen != Engine_UiRealEstatePage.ScreenId)
			{
				_estateSnapshot = default;
			}

			// A NEW SCREEN OPENS ON ITS FIRST PAGE.
			Fable2PageProviders.BookPage = 0;
			_screenPageHeld = 0;

			// THE SLOTS TOO, because "which screen is current" and "which presentation is in
			// which slot" are separate claims and the draw gate needs both to be right. A gate
			// that silently never fires looks exactly like a gate that fires and finds nothing,
			// and only the addresses tell them apart — they can be read straight against the
			// `[guest-ui] N live presentation(s)` list printed below, which gives each
			// presentation's address.
			string slots = manager is { } m && m.Owned.Count > 0
				? string.Join(", ", System.Linq.Enumerable.Select(
					System.Linq.Enumerable.OrderBy(m.Owned, e => e.Value),
					e => $"[{e.Value}]={e.Key:X8}"))
				: "none built yet";

			GD.Print(screen is null
				? "[guest-ui] no screen manager yet — the guest is not in game"
				: $"[guest-ui] the guest's current in-game screen is {screen} " +
				  $"({(int)screen.Value}); manager holds {slots}");
		}

		// WHICH SCREEN THE FOLLOWER IS ACTUALLY DRAWING, restated on every follow. It is the
		// question both <see cref="FollowUnsettled"/> and the pause menu's decline report ask,
		// and neither can answer it from the screen id alone: the guest states a screen the
		// moment it pushes it, several of its own ticks before the presentation exists.
		//
		// THE MANAGER'S SLOT MAP IS THE JOIN, the same stated join <see cref="PresentationFor"/>
		// makes — read here off the manager already in hand rather than through a second bridge
		// call. The STATED screen wins when it is among the drawn, because two of the manager's
		// presentations are briefly drawn together across a transition and only one of them is
		// the answer.
		//
		// ANY of them still drawn answers when the stated screen is not, which is what covers
		// LEAVING a menu: the guest drops to `None` and this stays on the screen whose tree is
		// still up, so the follower hurries until that tree is retired instead of leaving a
		// stale book over the running game for a sampling period.
		uint followed = (uint)Engine_UiModel.InGameScreen.None;
		if (manager is { } screens)
		{
			uint current = (uint)(screen ?? Engine_UiModel.InGameScreen.None);
			foreach ((uint address, int id) in screens.Owned)
			{
				if (!_drawn.ContainsKey(address))
				{
					continue;
				}

				if ((uint)id == current)
				{
					followed = current;
					break;
				}

				followed = (uint)id;
			}
		}

		_followedScreen = followed;

		string signature = string.Join('|', live.ConvertAll(p => $"{p.Address:X8}:{p.Name}"));
		if (signature == _presentationSignature)
		{
			return;
		}

		_presentationSignature = signature;

		if (live.Count == 0)
		{
			GD.Print("[guest-ui] no live presentations at all — the guest has built no UI");
			return;
		}

		GD.Print($"[guest-ui] {live.Count} live presentation(s):");
		foreach (Engine_UiModel.Presentation p in live)
		{
			GD.Print($"    {p.Address:X8} " +
				$"{(p.IsShowing ? "SHOWING" : "hidden")}" +
				$"{(p.IsFrozen ? " FROZEN" : string.Empty)} '{p.Name}'");
		}
	}

	// No list of in-game screens any more, deliberately. Which presentations are drawn is the
	// guest's statement — see Engine_UiModel.Presentation.IsShowing — so there is nothing here to
	// keep in step with the thirty in-game screens.

	/// <summary>
	/// True when the guest has an in-game presentation standing up.
	///
	/// The window uses this to decide whether the UI layer belongs on screen at all. It is
	/// deliberately about what the GUEST has live rather than about whether we happen to be
	/// drawing it, so a presentation we cannot yet render still keeps the layer up instead of
	/// silently taking the whole UI away.
	/// </summary>
	public bool HasInGameUi { get; private set; }

	private readonly System.Collections.Generic.Dictionary<uint, Engine_UiDrawnPresentation> _drawn = new();

	/// <summary>
	/// WHAT IS ACTUALLY ON SCREEN AT A GIVEN X — a scanline across the canvas, once, late.
	///
	/// The backdrop measurement closed its own question and opened this one: all 28 of its quads
	/// paint, continuously, right across the visible band, with nothing hidden, untextured or
	/// transparent. So the black stripe is not a hole in the backdrop — something is drawn OVER
	/// it. Which means the useful question is no longer "does the backdrop reach here" but "what
	/// is painting here", and no amount of reading the backdrop can answer that.
	///
	/// So this walks EVERY node under the presentation host — the guest's screens, our cloned
	/// rows, the card, the warning — and at each sample x reports the painting quads that cover
	/// it, nearest first. Three of them, because the answer to "why is this black" is usually the
	/// second one.
	///
	/// SAMPLED LATE. It runs once, well after the menu is up, because a scanline taken while the
	/// tree is still being assembled describes a screen the player never saw — the failure this
	/// project has hit repeatedly with once-only diagnostics.
	/// </summary>
	private void Scanline()
	{
		if (_presentation is null)
		{
			return;
		}

		var quads = new System.Collections.Generic.List<(float Min, float Max, float Z, string What)>();
		Collect(_presentation);

		void Collect(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Collect(child);
			}

			if (node is not MeshInstance3D mesh || !mesh.IsVisibleInTree())
			{
				return;
			}

			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			if (!paint.Painted)
			{
				return;
			}

			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			// Only what crosses the canvas centreline, which is where the stripe was measured.
			if (box.Position.Y > 0f || box.End.Y < 0f)
			{
				return;
			}

			// AN UNTEXTURED QUAD IS NOT SKIPPED, it is NAMED. A quad with no texture paints its
			// tint flat across its whole area — which for the default white is a solid white
			// plate. That is the single most likely cause of a screen going white, so a
			// scanline that filtered it out would have dutifully reported whatever was behind
			// the very thing being looked for.
			string texture = paint.Texture is { } albedo
				? albedo.ResourcePath.GetFile()
				: "NO-TEXTURE";
			Color tint = paint.Tint;

			quads.Add((box.Position.X, box.End.X, box.Position.Z,
				$"{mesh.Name}<{(texture.Length > 0 ? texture : "runtime")} " +
				$"{tint.R:F2},{tint.G:F2},{tint.B:F2}@a{tint.A:F2}>"));
		}

		var line = new System.Text.StringBuilder(
			$"[game-ui] scanline on '{_scanning}' at y=0 ({quads.Count} painting quad(s), nearest first)");
		foreach (float x in new[] { -600f, -480f, -400f, -340f, -280f, -160f, 0f, 200f, 400f, 600f })
		{
			var over = new System.Collections.Generic.List<(float Z, string What)>();
			foreach ((float min, float max, float z, string what) in quads)
			{
				if (x >= min && x <= max)
				{
					over.Add((z, what));
				}
			}

			over.Sort((a, b) => b.Z.CompareTo(a.Z));

			line.Append($"\n    x={x,5:F0}: ");
			line.Append(over.Count == 0
				? "NOTHING PAINTS HERE"
				: string.Join("  ", over.ConvertAll(o => $"{o.What}@z{o.Z:F0}").GetRange(
					0, System.Math.Min(3, over.Count))) + $"  (+{System.Math.Max(0, over.Count - 3)} more)");
		}

		GD.Print(line.ToString());
		Inventory();
	}

	/// <summary>
	/// Every named element on a small screen, painting or not — the "what is missing" report.
	///
	/// THE SCANLINE CANNOT ANSWER THIS. It samples the centreline, so anything that lives above
	/// or below y=0 — "Press A To Start", the legal text, the logo's particle group — is invisible
	/// to it whether it is drawn correctly or not there at all. Asking "what is painting at this
	/// x" and "does this element exist" are different questions and the first one silently
	/// answers "no" to both.
	///
	/// So this lists the built tree: name, world bounds, and why it is not on screen if it is
	/// not. An element absent from this list was never built; one present with `HIDDEN` or
	/// `a0.00` was built and suppressed; one with sane bounds and full alpha is on screen and the
	/// complaint is about placement. Those are three different fixes.
	///
	/// SMALL SCREENS ONLY. `frontendmainmenu` carries 3812 elements and would bury the log; the
	/// title screen carries about twenty. The cap is stated rather than silent — a report that
	/// quietly covers some screens and not others is how "it printed nothing" gets read as
	/// "nothing is wrong".
	/// </summary>
	private void Inventory()
	{
		// 128, not 64. The title screen has 77 elements and the first cap skipped it — the one
		// screen the inventory existed to explain. Sized to clear it with room rather than to
		// sit just above it, while still excluding `frontendmainmenu`'s 3812.
		const int cap = 128;

		if (_presentation?.CurrentScreen is not { } screen)
		{
			return;
		}

		// The two front-end screens are exempt from the cap while their rendering is under
		// investigation: stray A-prompt glyphs and missing page furniture cannot be named
		// without the element-by-element state, and these are the screens the report exists
		// for. Over the cap the print is restricted to elements that made it into the BUILT
		// tree — that is where a stray glyph lives, it is a few dozen lines instead of 3,812,
		// and the missing-furniture side is carried by the count of unbuilt elements.
		// THE EXEMPTION IS OVER. Both front-end screens now render correctly, and printing all
		// 3,812 of the main menu's elements cost a visible hitch every time the menu came up —
		// on the title screen, of all places. The cap applies to every screen; the built-tree
		// summary below still carries what a future investigation needs.
		const bool investigating = false;

		if (screen.Nodes.Length > cap && !investigating)
		{
			GD.Print($"[game-ui] inventory of '{_scanning}' SKIPPED — {screen.Nodes.Length} " +
				$"elements, over the {cap} cap");
			return;
		}

		// The built tree, gathered by NAME. Names are not guaranteed unique, but on a screen
		// small enough to pass the cap a collision is visible in the output rather than hidden
		// by it — and the alternative, the builder's ordinal map, is not reachable for a screen
		// the host mounted itself.
		var found = new System.Collections.Generic.Dictionary<string, string>();
		Gather(_presentation);

		void Gather(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Gather(child);
			}

			string name = node.Name;
			if (found.ContainsKey(name))
			{
				return;
			}

			if (node is not MeshInstance3D mesh)
			{
				found[name] = node is Node3D { Visible: false } ? "group HIDDEN" : "group";
				return;
			}

			if (!mesh.IsVisibleInTree())
			{
				found[name] = "HIDDEN";
				return;
			}

			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			found[name] =
				$"x[{box.Position.X:F0}..{box.End.X:F0}] y[{box.Position.Y:F0}..{box.End.Y:F0}] " +
				$"a{paint.Opacity:F2}";
		}

		int unbuilt = 0;
		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			UiNode authored = screen.Nodes[i];
			string name = authored.HasAuthoredName ? authored.Name : $"#{i}";
			if (!found.ContainsKey(name))
			{
				++unbuilt;
			}
		}

		if (OS.GetEnvironment("REFII_UI_INVENTORY") != "1")
		{
			GD.Print($"[game-ui] inventory of '{_scanning}': {screen.Nodes.Length} authored, {found.Count} in tree, {unbuilt} unbuilt");
			return;
		}

		var line = new System.Text.StringBuilder(
			$"[game-ui] inventory of '{_scanning}' ({screen.Nodes.Length} authored element(s))");

		bool builtOnly = screen.Nodes.Length > cap;
		unbuilt = 0;

		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			UiNode authored = screen.Nodes[i];
			string name = authored.HasAuthoredName ? authored.Name : $"#{i}";
			string kind = authored.Quad is not null
				? "quad"
				: authored.Text is not null ? $"text \"{authored.Text.Display}\"" : "group";

			if (!found.TryGetValue(name, out string? state))
			{
				++unbuilt;
				if (builtOnly)
				{
					continue;
				}

				state = "NOT IN THE BUILT TREE";
			}

			line.Append($"\n    {name} [{kind}] {state}");
		}

		if (builtOnly)
		{
			line.Append($"\n    (+ {unbuilt} authored element(s) not in the built tree, elided)");
		}

		// CHUNKED, because one print of a whole screen is silently truncated — the menu's
		// 3,812-element run lost its tail, including the elided-count line that says whether
		// the print is even complete. ~60 rows per print stays far inside the cap.
		string[] rows = line.ToString().Split('\n');
		var chunk = new System.Text.StringBuilder();
		for (int i = 0; i < rows.Length; ++i)
		{
			chunk.Append(rows[i]).Append('\n');
			if ((i + 1) % 60 == 0 || i == rows.Length - 1)
			{
				GD.Print(chunk.ToString().TrimEnd('\n'));
				chunk.Clear();
			}
		}
	}

	/// <summary>
	/// One scanline per presentation, taken once it has settled.
	///
	/// KEYED ON WHICH SCREEN IS UP, not on a single once-ever latch. The first version counted
	/// frames only after the main-menu guard, so it could never fire for the title screen — the
	/// exact screen that turned out to be drawing a white plate. A diagnostic that cannot run on
	/// the broken screen is not a diagnostic, and this is the second time on this feature that
	/// the gate, not the code, was the defect.
	/// </summary>
	private void ScanWhenSettled()
	{
		if (!IsRevealed)
		{
			_scanning = null;
			return;
		}

		string current = _showing ?? Screen;
		if (current != _scanning)
		{
			_scanning = current;
			_scanFrames = 0;
			return;
		}

		// Two seconds in. Long enough for a fade to finish, so an element caught mid-transition
		// is not reported as a permanent one.
		if (++_scanFrames == 120 && _scannedScreens.Add(current))
		{
			Scanline();
		}
	}

	private string? _scanning;

	private int _scanFrames;

	private readonly System.Collections.Generic.HashSet<string> _scannedScreens =
		new(System.StringComparer.Ordinal);

	/// <summary>
	/// Runs the title screen's logo flash — see <see cref="Engine_FrontEndTitle"/>.
	///
	/// GODOT DRIVES IT, which is the point rather than an implementation detail. The alternative
	/// was to follow the guest's own `frontendstartupscreen` presentation and take the opacity it
	/// states, and that would have worked for this one element while handing the screen's
	/// animation back to a presentation the guest reports as `hidden` throughout. The front end
	/// is ours: the menu, the card and the warning are all driven here, and the title screen is
	/// the last piece that was not.
	/// </summary>
	private void DriveTitle(double delta)
	{
		// NO SCREEN-NAME TEST. The first version only ran while `_showing` matched
		// "frontendstartupscreen", and produced no output at all on a run where the scanline —
		// same method, same frames, one line above — reported that very screen twice. Rather
		// than keep reasoning about why a string comparison did not hold, the comparison is
		// gone: the flash is driven wherever the current screen actually has one, and a screen
		// without it says so.
		// NO FRONT END, NO FLASH. LeaveFrontEnd clears `_showing`; the export's starting screen is
		// not the current one. Forgetting the screen here lets the title made again after Quit to
		// Title — a new presentation, whose scene starts over — run its flash from the start.
		if (_showing is not { } current)
		{
			_titleScreen = null;
			return;
		}

		if (current != _titleScreen)
		{
			_titleScreen = current;
			_titleFrames = 0;
			_title.Reset();
		}

		// THE OWNED BOOK'S CLONE FIRST. With Godot drawing the title, the live catalogue points
		// into the hidden mirror — fading that one leaves the white plate on screen and reports
		// success. Falls back to the catalogue for the frames before the book has stood up.
		Node? flash = null;
		Engine_UiOwnedBook? activeTitleBook = null;
		if (_books.Book(Engine_UiBookSet.TitleScreenId) is { } titleBook &&
			IsInstanceValid(titleBook) && titleBook.Visible)
		{
			activeTitleBook = titleBook;
			flash = titleBook.Find(Engine_FrontEndTitle.FlashName);
		}

		string before = _title.LastReport;
		if (flash is not null)
		{
			_title.Advance(flash, delta);
		}
		else
		{
			_title.Advance(_presentation?.Theme, delta);
		}

		if (activeTitleBook is not null && flash is not null)
		{
			_titleFlashHostOpacity[Engine_FrontEndTitle.FlashName] = _title.CurrentAlpha;
			activeTitleBook.HostOpacity = _titleFlashHostOpacity;
		}

		++_titleFrames;

		// REPORTED ON CHANGE, not on a frame count. The frame-count version fired once, sixty
		// frames after process start — which is before the title screen is revealed and before
		// its catalogue exists — and so reported "not in this screen's catalogue" on a run where
		// the fade then worked perfectly. A one-shot sample of a state that has not happened yet
		// is worse than no sample: it reads as a failure.
		if (_title.LastReport != before)
		{
			GD.Print($"[game-ui] logo flash on '{current}' — {_title.LastReport}");
		}

	}


	private int _titleFrames;

	private readonly Engine_FrontEndTitle _title = new();

	private readonly System.Collections.Generic.Dictionary<string, float> _titleFlashHostOpacity =
		new(System.StringComparer.Ordinal);

	private string? _titleScreen;

	/// <summary>The video player, so an embedded movie can be drawn into the element hosting it.</summary>
	public GameVideo? Video { get; set; }

	/// <summary>Element ordinals currently hosting a movie, per presentation address.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, int> _movieSlots = new();

	/// <summary>Set by the frame that asks for an embedded preview; cleared in _Process. A frame
	/// with no ask takes the host's movie down — see the orphan teardown there.</summary>
	private bool _embeddedMovieAsked;

	/// <summary>
	/// True while any drawn presentation hosts an embedded movie — so the window's
	/// "hide the UI" handler does not conceal the screen a preview is playing inside.
	/// </summary>
	public bool HasEmbeddedMovie => _movieSlots.Count > 0;

	/// <summary>Presentations already reported as Godot-owned, so the line prints once each.</summary>
	private readonly System.Collections.Generic.HashSet<string> _unmirrored = new();

	/// <summary>
	/// Presentations withheld because the guest says no screen is up, so that line fires once
	/// each rather than several times a second for as long as the player is walking around.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<uint> _suppressed = new();

	/// <summary>
	/// Prints what an owned menu view actually drew, when it differs from last time.
	///
	/// WHAT THE MODEL SAYS IS NOT EVIDENCE. The pause list reported eight entries with eight
	/// correct labels while the screen showed six blank tabs under two labelled ones, because
	/// every diagnostic we had described the MODEL and the model was right. This one walks the
	/// row nodes, so a row that drew nothing says so — and says whether it was the entry, the
	/// string, the glyph mesh or the position that failed.
	///
	/// Change-keyed rather than per frame: the row set is stable for as long as nobody moves,
	/// and a line per frame would bury it.
	/// </summary>
	private void ReportMenuView(Engine_FrontEndMenuView? view)
	{
		if (view is null || !IsInstanceValid(view))
		{
			return;
		}

		// Keyed with the positions STRIPPED: a slide animation moves every row a little each
		// frame, and a report that fires on coordinates prints sixteen near-identical lines
		// per press. The printed line keeps them — the change that matters is which entries,
		// labels and meshes the rows hold.
		//
		// Key first, string only on a change; runs every frame a view stands. See
		// Engine_FrontEndMenuView.DescribeKey.
		long changeKey = view.DescribeKey();

		// The report is per name; `Node.Name` allocates on every read, so the name is held per
		// instance. A rebuilt view is a new instance and reads its name again.
		ulong instance = view.GetInstanceId();
		if (!_menuViewNames.TryGetValue(instance, out string? key))
		{
			key = view.Name.ToString();
			_menuViewNames[instance] = key;
		}

		if (_menuViews.TryGetValue(key, out long was) && was == changeKey)
		{
			return;
		}

		_menuViews[key] = changeKey;
		GD.Print(view.Describe());
	}

	private readonly System.Collections.Generic.Dictionary<string, long> _menuViews = new();

	private readonly System.Collections.Generic.Dictionary<ulong, string> _menuViewNames = new();

	private Camera3D? _uiCamera;
	private Node3D? _uiCameraMount;

	/// <summary>
	/// Keeps the owned canvas camera CURRENT and FITTED, every frame.
	///
	/// Current: a presentation tree being freed promotes some other camera — including the
	/// authored perspective cameras mirrored screens carry — and one frame of somebody else's
	/// projection is a screen at the wrong scale. Fitted: the window can resize at any time,
	/// and the fit decides whether the design box is cropped or letterboxed.
	/// </summary>
	private void DriveUiCamera()
	{
		if (_viewport is null || _uiCamera is null || !GodotObject.IsInstanceValid(_uiCamera))
		{
			return;
		}

		if (_viewport.GetCamera3D() != _uiCamera)
		{
			string was = _viewport.GetCamera3D()?.Name.ToString() ?? "none";
			_uiCamera.MakeCurrent();
			GD.Print($"[game-ui] the UI camera was '{was}' — re-asserted the canvas camera");
		}

		Vector2 size = _viewport.Size;
		if (size != _uiCameraFitted && size.X > 0 && size.Y > 0)
		{
			_uiCameraFitted = size;
			Engine_UiSceneBuilder.FitCanvasCamera(_uiCamera, size);
		}
	}

	private Vector2 _uiCameraFitted;

	/// <summary>
	/// The key over the last `[ui-camera]` line — see <see cref="ReportCamera"/>. Nullable so
	/// the first sample cannot be mistaken for an unchanged one.
	/// </summary>
	private int? _cameraReported;

	/// <summary>
	/// The projection ground truth, printed on change.
	///
	/// EXISTS BECAUSE OF A SCALE DEFECT NOBODY COULD LOCATE. The in-game UI drew at roughly
	/// 0.57 of its harness size — suspiciously 720/1280 — and every candidate (live
	/// transforms, widescreen fixups, the camera, the SubViewport mapping) was invisible in
	/// the log. This line names the CURRENT camera, its projection numbers, and the three
	/// sizes a quad passes through on its way to the window, so a mis-fit is one grep instead
	/// of a run of pixel measurements on a screenshot.
	/// </summary>
	private void ReportCamera()
	{
		if (_viewport is null || _viewport.GetCamera3D() is not { } camera)
		{
			return;
		}

		// Key first, string only on a change. The key covers exactly what the line prints, at
		// the precision it prints it (`size` to one decimal), so a change too small to show
		// does not fire a report.
		bool pauseUp = _pauseView is not null && GodotObject.IsInstanceValid(_pauseView) &&
			_pauseView.Visible;
		Vector3 pauseAt = pauseUp ? _pauseView!.GlobalPosition : Vector3.Zero;
		var key = new System.HashCode();
		key.Add(camera.GetInstanceId());
		key.Add((int)camera.Projection);
		key.Add((int)camera.KeepAspect);
		key.Add(System.MathF.Round(camera.Size, 1, System.MidpointRounding.AwayFromZero));
		key.Add(camera.GlobalPosition);
		key.Add(_viewport.Size);
		key.Add(_container?.Size);
		key.Add(GetViewport().GetVisibleRect().Size);
		key.Add(pauseUp);
		key.Add(pauseAt);
		key.Add(pauseUp ? camera.UnprojectPosition(pauseAt) : Vector2.Zero);

		int reported = key.ToHashCode();
		if (reported == _cameraReported)
		{
			return;
		}

		_cameraReported = reported;
		GD.Print($"[ui-camera] '{camera.Name}' proj={camera.Projection} " +
			$"keep={camera.KeepAspect} size={camera.Size:0.#} pos={camera.GlobalPosition} " +
			$"viewport={_viewport.Size} container={_container?.Size} " +
			$"window={GetViewport().GetVisibleRect().Size}" +
			(pauseUp
				? $" pauseView@{pauseAt} screen={camera.UnprojectPosition(pauseAt)}"
				: string.Empty));
	}

	/// <summary>The HUD's texture hashes, one exactly-sized array refilled in place per cadence.</summary>
	private uint[]? _hudTextures;

	/// <summary>
	/// The textures told to the HUD's elements (Engine_UiHudStand.Textures: every texture writer's
	/// argument — GuiElement_SetTexture by name, the wheel, d-pad, spell and charge icons, the
	/// image registry) under a mod's icon overrides, into ONE array refilled in place.
	/// </summary>
	private uint[] HudTextures(Engine_UiHudStand stand)
	{
		if (_hudTextures is null || _hudTextures.Length != stand.Count)
		{
			_hudTextures = new uint[stand.Count];
		}

		System.Array.Copy(stand.Textures, _hudTextures, stand.Count);

		// The expression wheel's item icons, as its SetItem states them (Engine_UiWheelItems).
		_wheelItems.Textures(stand.Screen.Document, _hudTextures);

		// AND THE IMAGE REGISTRY'S NAMES (`ImageName`, registry 12): an element the guest gave no
		// texture of its own wears the texture its name was last given — the spell charges'
		// icons (`sub_825E9EA0`), stated as the call names them (`@Gui.Image`).
		if (_hud?.Bindings is { } bindings)
		{
			foreach (Engine_UiBindings.Binding binding in bindings.All)
			{
				if (binding.Kind == Engine_UiBindings.Kind.Image &&
					binding.ElementOrdinal >= 0 && binding.ElementOrdinal < _hudTextures.Length &&
					_hudTextures[binding.ElementOrdinal] == 0u &&
					_hud.Values.RegistryImage(binding.NameHash) is { } image)
				{
					_hudTextures[binding.ElementOrdinal] = image;
				}
			}
		}

		ApplyHudIconOverrides(_hudTextures, HudLocalPlayer);
		_hudTexturesGeneration = stand.Generation;
		return _hudTextures;
	}

	/// <summary>Takes one drawn presentation off screen and forgets it.</summary>
	private void Retire(uint address, string why)
	{
		if (!_drawn.TryGetValue(address, out Engine_UiDrawnPresentation? drawn))
		{
			return;
		}

		GD.Print($"[game-ui] retired '{drawn.Name}' — {why}");
		drawn.Node.QueueFree();
		_drawn.Remove(address);

		// The owned in-game list belongs to the presentation that carried it — its rows are
		// the guest's live text on THAT tree's elements, so both the model and the view go.
		_screenLists.Remove(address);
		if (_screenMenuAddress == address)
		{
			Engine_UiOptionsPending.Leave();
			_screenMenuAddress = 0;
			_screenMenu = null;
			_screenMenuView?.QueueFree();
			_screenMenuView = null;
			_screenMenuScreen = uint.MaxValue;
			Fable2Input.ScreenMenuOwnsInput = false;
		}

		// The owned pause view stands over the mirrored pause tree; it comes down with it.
		// The B swallow clears HERE as well as in DrivePauseMenu, so B can never stay
		// masked past the menu that justified masking it.
		if (drawn.Name == PausePresentation)
		{
			_pauseTreeUp = false;
			if (_pauseView is not null)
			{
				_pauseView.Visible = false;
			}

			Fable2Input.PauseMenuSwallowsBack = false;
		}

		_movieSlots.Remove(address);
		_suppressed.Remove(address);
	}

	/// <summary>
	/// Takes every drawn presentation down.
	///
	/// Needed wherever ownership of the screen changes hands, not just where the guest drops a
	/// presentation — otherwise a screen we were drawing outlives the thing that wanted it.
	/// </summary>
	private void RetireDrawn(string why)
	{
		if (_drawn.Count == 0)
		{
			return;
		}

		foreach (uint address in new System.Collections.Generic.List<uint>(_drawn.Keys))
		{
			Retire(address, why);
		}
	}

	/// <summary>
	/// Draws the front-end presentations the by-name path does not cover.
	///
	/// The by-name path knows two screens — the title and the main menu — because those are the
	/// two the guest names in its own constructor. It has never known about
	/// <c>scrollingbackground</c>, which the guest reports as SHOWING while the menu is up, and
	/// that is the grey void behind the menu: not a missing texture, a whole presentation
	/// nobody was drawing.
	///
	/// This is a step toward the by-name path going away entirely. It is NOT that step: the
	/// gate reports the title screen hidden for stretches while it is plainly on screen (the
	/// attract movie is up), so switching the active screen over to the gate wholesale would
	/// blank the front end. Adding what the gate says is showing, on top of what we already
	/// draw, is the half of that change which cannot regress.
	///
	/// Drawn BEHIND, at negative depth. These are backgrounds by definition — the by-name
	/// screen is the one the player is interacting with — and the front-end screen supplies the
	/// camera, so nothing here activates one.
	/// </summary>
	private void FollowFrontEndExtras(System.Collections.Generic.List<Engine_UiModel.Presentation> live)
	{
		if (_presentation is null)
		{
			return;
		}

		var wanted = new System.Collections.Generic.List<Engine_UiModel.Presentation>();
		foreach (Engine_UiModel.Presentation p in live)
		{
			if (!p.IsShowing)
			{
				continue;
			}

			// Skip whatever the by-name path is already drawing, or it would be built twice.
			string bank = System.IO.Path.GetFileNameWithoutExtension(p.Name.Replace('\\', '/'));
			if (string.Equals(bank, _showing, System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			// AND SKIP THE OTHER FRONT-END SCREEN, which is not an extra at all.
			//
			// There are exactly two — `frontendstartupscreen` and `frontendmainmenu` — and the
			// guest tells us which is ACTIVE; the by-name path draws that one and this path must
			// not draw the other. Its root keeps its active bit set after the hand-over, so
			// "the guest is showing it" is true of both, and drawing both puts the title
			// screen's "Press A" prompt over the menu. That is the stray A glyph floating in the
			// corner of every front-end screenshot: not a widget of ours, the whole title screen
			// underneath one.
			//
			// Derived from our own contract rather than a name list: `PresentationFor` is the
			// one place that enumerates the front end's screens, so a third would be covered
			// here the day it is added.
			if (Engine_UiFrontEndScreens.Is(bank))
			{
				continue;
			}

			wanted.Add(p);
		}

		var gone = new System.Collections.Generic.List<uint>();
		foreach (uint address in _drawn.Keys)
		{
			if (!wanted.Exists(p => p.Address == address))
			{
				gone.Add(address);
			}
		}

		foreach (uint address in gone)
		{
			Retire(address, "the guest stopped showing it");
		}

		foreach (Engine_UiModel.Presentation p in wanted)
		{
			Follow(p, -1);
		}
	}

	/// <summary>
	/// Draws the in-game UI the guest currently has live.
	///
	/// SAME PATH AS THE FRONT END, which is the point. The HUD is a Gameface presentation like
	/// every front-end screen — same factory, same element tree, same root at +500 — so it goes
	/// through the presentation host and the scene builder that already exist rather than
	/// through a second renderer. What the HUD needed was not drawing but FINDING: a walk that
	/// does not go through a field only the front-end GUI object has.
	/// </summary>
	/// <summary>
	/// Whether the guest holds a box surface's presentation hidden: it has one live and none of
	/// them showing. No presentation at all says nothing — the box stands on its Open alone.
	/// </summary>
	private static bool HiddenByGuest(System.Collections.Generic.List<Engine_UiModel.Presentation> live,
		Engine_UiStatedSurface surface)
	{
		bool any = false;
		foreach (Engine_UiModel.Presentation p in live)
		{
			if (!surface.Matches(p))
			{
				continue;
			}

			if (p.IsShowing)
			{
				return false;
			}

			any = true;
		}

		return any;
	}

	private void FollowInGameUi(System.Collections.Generic.List<Engine_UiModel.Presentation> live)
	{
		if (_presentation is null)
		{
			return;
		}

		// WHAT THE GUEST SAYS IS SHOWING, not a list we keep.
		//
		// A hardcoded set of file names is the wrong shape twice over: it draws closed menus
		// (every live screen looks drawable) and it can never show a screen nobody thought to
		// add. The guest states this — a presentation is drawn
		// exactly when its ROOT element's `active` bit is set, which is the test its own update
		// makes before walking the tree at all — so following that needs no host-side list and
		// covers all thirty in-game screens for free.
		var wanted = new System.Collections.Generic.List<Engine_UiModel.Presentation>();

		// THE DISPLAY-ONLY PANELS stand on their boxes' own Open and Close (`@DisplayBox.Open` /
		// `.Close`), not on a presentation: theirs are headless and never mirrored.
		bool intermissionSeen = _surfaces.Intermission.Stand(_presentation,
			HiddenByGuest(live, _surfaces.Intermission));
		bool displayBoxSeen = _surfaces.DisplayBox.Stand(_presentation,
			HiddenByGuest(live, _surfaces.DisplayBox));

		// OWNING A PANEL IS NOT THE SAME AS THERE BEING NO PANEL. `wanted` is the MIRROR set,
		// and an owned surface is deliberately kept out of it — but `HasInGameUi` is read by
		// GameWindow as "is there any UI at all", and it is what keeps the whole layer on
		// screen. Leaving the owned family uncounted meant a query box, a message box or a
		// menu box standing ALONE concealed the viewport it is drawn in: the layer came down
		// on top of the one thing it was up for. Intermission and the display box already
		// count themselves back in this way; this is the same statement for the rest.
		bool ownedSeen = false;
		foreach (Engine_UiModel.Presentation p in live)
		{
			if (!p.IsShowing)
			{
				continue;
			}

			// AN OWNED MODAL BOX REPLACES ITS MIRROR — drawing both is two boxes on one
			// screen, the same rule as every other owned panel. Only while ours is standing
			// in: a box outside the owned family (or a view that failed to build) falls back
			// to the mirror here.
			if (_surfaces.Owns(p))
			{
				ownedSeen = true;
				if (_suppressed.Add(p.Address))
				{
					GD.Print($"[game-ui] not mirroring '{p.Name}' — Godot owns this panel");
				}

				continue;
			}

			if (_surfaces.Intermission.Matches(p) || _surfaces.DisplayBox.Matches(p))
			{
				continue;
			}

			// EVERY PRESENTATION IS HEADLESS (gui_headless_presentation.cpp): one shows by what states
			// it — the screen manager's current screen (AddStatedScreens), or, the HUD and the banner,
			// by being made at all. Its root and element count are not a file's.
			if (!_statedOnly.Contains(p.Address))
			{
				if (p.Is(HudPresentation + ".bgf") || p.Is(CyclerPresentation + ".bgf"))
				{
					wanted.Add(p);
				}

				continue;
			}

			_suppressed.Remove(p.Address);
			wanted.Add(p);
		}

		// The HUD underneath, everything else over it. Depth is not something the guest states
		// in a form we have read, so this is ours — and it is the one ordering that cannot put
		// a menu behind the health bar.
		wanted.Sort((a, b) =>
			(a.Is("hud.bgf") ? 0 : 1).CompareTo(b.Is("hud.bgf") ? 0 : 1));

		// A panel whose presentation left the live set comes down with it.
		if (!intermissionSeen)
		{
			_surfaces.Intermission.Withdraw();
		}

		if (!displayBoxSeen)
		{
			_surfaces.DisplayBox.Withdraw();
		}

		HasInGameUi = wanted.Count > 0 || intermissionSeen || displayBoxSeen || ownedSeen;

		// Retire anything the guest has taken away — and anything whose ADDRESS HAS BEEN REUSED
		// FOR A DIFFERENT PRESENTATION, which is a different fault with the same cure.
		//
		// THE GUEST RECYCLES PRESENTATION ADDRESSES. One run's registry shows 4C8C7BB0 holding
		// `worldmapscreen.bgf`, then — same address, same root — `giftgivingscreen.bgf` after
		// the player changed tab. Everything we key on an address (the built tree, the document,
		// the row pool, the panel's resolved fields) is stale from that moment until the
		// structural comparison happens to notice, and until then the SCREEN MANAGER'S OWN JOIN
		// hands the new screen id straight to the old screen's tree: the log read "Godot owns
		// screen 15's list in 'worldmapscreen' — Quests, Jobs, Sales, Families, Regions" and the
		// items panel duly reported its own fields "NOT IN THE DOCUMENT". Same class as the
		// kernel-handle rule — a guest address is a slot, not an identity.
		var gone = new System.Collections.Generic.List<uint>();
		var recycled = new System.Collections.Generic.List<uint>();
		foreach ((uint address, Engine_UiDrawnPresentation drawn) in _drawn)
		{
			int at = wanted.FindIndex(p => p.Address == address);
			if (at < 0)
			{
				gone.Add(address);
			}
			else if (!string.Equals(ShortName(wanted[at]), drawn.Name,
				System.StringComparison.OrdinalIgnoreCase))
			{
				recycled.Add(address);
			}
		}

		foreach (uint address in gone)
		{
			Retire(address, "the guest stopped showing it");
		}

		foreach (uint address in recycled)
		{
			Retire(address, "the guest reused this address for a different presentation");
		}

		if (wanted.Count > 0)
		{
			// NOTHING FROM THE FRONT END BELONGS OVER THE RUNNING GAME. `LeaveFrontEnd` frees
			// all three when the guest leaves properly; this is the belt to that braces, for the
			// case where in-game UI appears without that handover having been seen — which is
			// how the load fan ended up drawn over the world in the first place.
			if (_menuView is not null)
			{
				_menuView.Visible = false;
			}

			if (_cardView is not null)
			{
				_cardView.Visible = false;
			}

			if (_saveCardView is not null)
			{
				_saveCardView.Visible = false;
			}
		}

		using (Refii.Core.Engine_FramePhases.Measure("ui.follow"))
		{
			for (int depth = 0; depth < wanted.Count; ++depth)
			{
				Follow(wanted[depth], depth);
			}
		}
	}

	/// <summary>
	/// The owned list of one presentation: made on first sight, rebuilt from the stated g_Menu
	/// model and the population template. A headless presentation has no element sample.
	/// </summary>
	private void FollowList(uint address, string name, Engine_UiScreen? screen)
	{
		// THE OWNED LIST'S CONTENTS, re-read every follow — on the guest frame a `g_Menu`
		// statement, the stated screen or tab, the data generation or the page moved — and the
		// model's own comparison is what turns that into a refresh.
		if (screen is not null && Engine_ExpandableMenuItems.FindGroup(screen) is not null)
		{
			if (!_screenLists.TryGetValue(address, out Engine_UiScreenMenu? list))
			{
				list = new Engine_UiScreenMenu();
				_screenLists[address] = list;
			}

			// THE STRING TABLE, because a FOLDED row has no pool button and therefore no
			// resolved label — all the tree carries for it is a Babel tag. Set every follow
			// rather than once: the database arrives with the presentation host, which may not
			// exist yet the first time a list is built.
			if (list.Strings is null)
			{
				list.Strings = _presentation?.Strings;
			}

			// The same two inputs for the DATA-AREA source, which needs the string book to name
			// an item and `globals.gdb` to find its tag. Read once and kept: the GDB is 3 MB and
			// its parse is shared, but re-resolving it per sample would still walk the banks.
			if (Engine_UiInventorySource.Strings is null || Engine_UiInventorySource.Strings != _presentation?.Strings)
			{
				Engine_UiInventorySource.Strings = _presentation?.Strings;
			}

			if (Engine_UiInventorySource.Globals is null &&
				Refii.Gdb.Engine_GameGdb.Database is { } gameGdb)
			{
				// The SHARED database, not a second parse: `Engine_GameGdb` already holds
				// `globals.gdb` and its supplements behind one resolver.
				Engine_UiInventorySource.Globals = gameGdb;
				GD.Print("[screen-menu] the inventory source has the game GDB — item rows can " +
					"be read from the data areas now");
			}

			// THE C++'s OWN STATEMENTS, WHICH WIN WHERE THEY EXIST. `Engine_UiMenuModel` holds
			// what the Gameface Lua would have held, built from the 28 `g_Menu:` mutations the
			// game makes; where it carries rows for this presentation the list is built from
			// them and the UI VM is not read. Set every follow rather than once, for the same
			// reason the string table is: a list can be built before the first statement lands.
			list.Model = _menuModel;

			// WHICH SCREEN THIS LIST IS, from the manager's own presentation-to-id map — the
			// only way a widget that names no population script can find its template. The gift
			// family is the case: see `Engine_UiScreenTemplate.MenuScript`.
			// THE UP SCREEN STANDS IN until the manager's map names this presentation. The
			// map registers an entry a sample or two after the presentation is first drawn,
			// and a list built in that gap at id 0 comes from the g_Menu model; when the id
			// then lands the template wins and the rebuild drops the host's fold and cursor —
			// the weapons book snapped shut the moment the preview model loaded. The guest
			// keeps one in-game screen up at a time, so the screen that is up IS this list's,
			// except for the pause presentation, whose own list is the root's.
			list.ScreenId =
				_screenOwners is { } owners &&
				owners.TryGetValue(address, out int owning)
					? (uint)owning
					: !string.Equals(name, PausePresentation, System.StringComparison.OrdinalIgnoreCase)
						? UpScreenId()
						: 0u;

			// SCREEN 28'S ROWS CARRY THE ESTATE'S OWN STATE, and the guest answers that state a
			// sample or two AFTER the screen opens: the first build sees no building — or the
			// previous one — until the pump answers. Sample once into _estateSnapshot so all
			// row conditions read one consistent snapshot, and invalidate only this list instance
			// when the condition bits change; the rebuild carries the fold and cursor by name.
			if (list.ScreenId == Engine_UiRealEstatePage.ScreenId)
			{
				_estateSnapshot = Fable2Bridge.GetRealEstate();
				list.InvalidateAuthoredIfChanged(Engine_UiRealEstatePage.ConditionKey(_estateSnapshot));
			}

			bool rebuilt;
			using (Refii.Core.Engine_FramePhases.Measure("ui.list.rebuild"))
			{
				rebuilt = list.Rebuild(address, screen);
			}

			if (rebuilt && ReferenceEquals(list, _screenMenu))
			{
				_screenMenuView?.Refresh();
			}

			// THE PAUSE ROOT'S LABELS, from the guest's own `SpawnButton` statements. Its list is
			// ours — the guest keeps no C++ item list — but the STRING in each row is the
			// guest's, and it carries what a tag cannot: the unspent-ability count and the
			// new-item marker, both appended at spawn time. Re-read on every follow: the
			// guest restates the rows when the decorations change.
			if (name == PausePresentation && _pauseMenu is not null)
			{
				// NAMED FIRST, so the label join and the sub-tab source read the same capture.
				Engine_UiMenuSources.StatedPresentation = address;

				bool moved = Engine_UiMenuModel.ApplyStatedLabels(_pauseMenu, _menuModel.For(address).Rows);

				// AND THE SUB-TABS, from the same capture the in-game lists join against.
				//
				// The template authors the SHAPE — which tabs exist, what each dispatches — and
				// the capture supplies the CONTENTS under each. It has to run here rather than at
				// Build: the menu is built when the screen is adopted and the capture fills in
				// afterwards, so building once would freeze whatever the capture held at
				// adoption, which is usually nothing. That is the whole reason the pause tabs
				// had no sub-tabs while the in-game screens did — not a missing mechanism, a
				// mechanism that only ever ran too early.
				int before = CountRows(_pauseMenu);

				// WHOSE LUA THE SOURCE READS, named once for the whole pass. The pause tabs'
				// children are stated in this presentation's own `g_Menu` tree — folded and
				// unfolded alike — and the spawn capture is only the fallback behind it. The
				// set also invalidates the cached read, so one sample is one walk however many
				// tabs ask — it was named above, before the labels joined against it.
				if (Engine_UiTemplates.Menu("pause-root") is { } record &&
					record.RefreshSources(_pauseMenu))
				{
					moved = true;
					GD.Print($"[game-ui] pause sub-tabs re-sourced — {before} row(s) before, " +
						$"{CountRows(_pauseMenu)} after");
				}

				if (moved)
				{
					_pauseView?.Refresh();
					ReportMenuView(_pauseView);
				}
			}
		}

	}

	/// <summary>
	/// The screens the guest's screen manager STATES are up that the registry cannot list: a
	/// headless presentation (gui_headless_presentation.cpp) is in no kernel list. The manager's
	/// slot for the current screen id (`mgr+12+4*id`, the shared slot `+156`) and the display
	/// box's screen name the presentation; its file is the screen's template's. Each joins the
	/// live set as showing and contributing, so every in-game screen stands by its stated id.
	/// </summary>
	private void AddStatedScreens(System.Collections.Generic.List<Engine_UiModel.Presentation> live)
	{
		_statedOnly.Clear();
		if (HostBridge.FrontEndGuiIsUp || HostBridge.GetInGameScreens() is not { } screens)
		{
			return;
		}

		uint current = (uint)screens.Current;
		uint box = Engine_UiSource.Current.DisplayBoxScreen();
		foreach (uint stated in new[] { current, box })
		{
			if (stated is (uint)Engine_UiModel.InGameScreen.None or uint.MaxValue ||
				Engine_UiStatedScreens.Presentation(screens.Owned, stated) is not (uint address, string file))
			{
				continue;
			}

			// The stated screen stands as showing whatever the registry says of a headless
			// presentation — its root and element count are not a file's.
			int listed = live.FindIndex(p => p.Address == address);
			if (listed >= 0)
			{
				live.RemoveAt(listed);
			}

			live.Add(Engine_UiStatedScreens.Entry(address, file));
			_statedOnly.Add(address);
		}
	}

	/// <summary>The addresses <see cref="AddStatedScreens"/> added this sample.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _statedOnly = new();

	/// <summary>
	/// Stands a stated screen: an empty stand-in at its depth (the pause root adopts its owned menu
	/// on it), the file's document for the joins, and the owned list rebuilt from the stated g_Menu
	/// model and the template.
	/// </summary>
	private void FollowStated(uint address, string name, int depth)
	{
		if (_presentation is null)
		{
			return;
		}

		if (_drawn.TryGetValue(address, out Engine_UiDrawnPresentation? drawn) &&
			!string.Equals(drawn.Name, name, System.StringComparison.OrdinalIgnoreCase))
		{
			Retire(address, "the screen manager states another presentation at this address");
			drawn = null;
		}

		bool made = false;
		if (drawn is null)
		{
			if (_presentation.DocumentFor(name) is not { } screen)
			{
				if (_unmirrored.Add("stated:" + name))
				{
					GD.PushWarning($"[game-ui] the screen manager states '{name}', which is not among the shipped presentations");
				}

				return;
			}

			var built = new Node3D { Name = $"Stated_{name}", Visible = false };
			_presentation.AddChild(built);
			drawn = new Engine_UiDrawnPresentation
			{
				Name = name,
				Node = built,
				Screen = screen,
			};
			_drawn[address] = drawn;
			made = true;
			GD.Print($"[game-ui] '{name}' stands by its STATED screen at 0x{address:X8} — " +
				$"{screen.Nodes.Length} file element(s), no guest sample");
		}

		drawn.Node.Position = new Vector3(0f, 0f, depth * Engine_UiPresentationDepth.Step);

		if (made && name == PausePresentation)
		{
			AdoptPauseMenu(drawn.Node, address);
		}

		FollowList(address, name, drawn.Screen);
	}

	/// <summary>
	/// The bank entry name for a live presentation — the guest's string is a full guest path,
	/// e.g. <c>GAME:\data\art\gui\gameface\hud.bgf</c>, with backslashes that mean nothing here.
	///
	/// This is a presentation's IDENTITY, and its address is not: see the recycling note in
	/// <see cref="FollowInGameUi"/>.
	/// </summary>
	private static string ShortName(Engine_UiModel.Presentation presentation) =>
		System.IO.Path.GetFileNameWithoutExtension(presentation.Name.Replace('\\', '/'));

	/// <summary>
	/// The HUD presentation the guest plays, of those stated live: the last `hud.bgf` it made. The
	/// HUD's one loader (`sub_823CA428` 0x823ca460, disc `sub_823FACE0`) loads "HUD.bgf" into
	/// `hud+4`, so the newest is the HUD object's; an in-game load can leave the previous one live
	/// beside it for a while. 0 when none is stated.
	/// </summary>
	private static uint StatedHudPresentation(
		System.Collections.Generic.List<Engine_UiModel.Presentation> live)
	{
		uint address = 0u;
		uint serial = 0u;
		foreach (Engine_UiModel.Presentation p in live)
		{
			if (p.Is(HudPresentation + ".bgf") && (address == 0u || p.Serial > serial))
			{
				address = p.Address;
				serial = p.Serial;
			}
		}

		return address;
	}

	private uint _statedHud;

	/// <summary>Builds or refreshes one live presentation at a given depth.</summary>
	private void Follow(Engine_UiModel.Presentation presentation, int depth)
	{
		string name = ShortName(presentation);

		// THE HUD IS NOT MIRRORED. Godot rebuilds it — see BuildRebuiltHud — from this file's
		// own bindings, its own art and the guest's name-addressed values, so mirroring the
		// element tree as well would draw two HUDs at once and defeat the point: a mirrored
		// tree carries the artist's ARRANGEMENT, and the arrangement is the thing being
		// replaced so the HUD can be moved, modded and scaled to displays the game never
		// shipped for.
		if (string.Equals(name, HudPresentation, System.StringComparison.OrdinalIgnoreCase))
		{
			// The newest stated hud.bgf only: see StatedHudPresentation.
			if (_statedHud != 0u && _statedHud != presentation.Address)
			{
				return;
			}

			EnsureRebuiltHud();

			// The address is still taken: the world icons — the one part of the HUD the guest
			// arranges — are sampled off this presentation without mirroring it. See
			// Engine_HudWorldIcons.
			_hudAddress = presentation.Address;
			if (_hud is not null && GodotObject.IsInstanceValid(_hud) && !_hud.Visible &&
				!_fullCutscene && !_modScreenHidHud)
			{
				// Back after a Quit to Title; LeaveGame hid it with the game.
				_hud.Visible = true;
			}

			return;
		}

		// THE PAUSE BANNER IS OWNED TOO — a whole-tree clone driven by the cycler's one live
		// text (the current screen's name). Sampled, never mirrored.
		if (string.Equals(name, CyclerPresentation, System.StringComparison.OrdinalIgnoreCase))
		{
			_cyclerAddress = presentation.Address;
			return;
		}

		// EVERY PRESENTATION IS HEADLESS: CGameface vtable +28 (TU1 `sub_82BBA588`, disc `sub_82BB3E88`)
		// makes no .bgf scene (gui_headless_presentation.cpp), so there is no element tree to sample.
		// It stands on its file and the statements.
		FollowStated(presentation.Address, name, depth);
	}

	private string? _presentationSignature;

	/// <summary>
	/// The guest's last stated in-game screen, so the line fires on a change rather than
	/// every follow. Null is a distinct value here — "no screen manager" is not
	/// <see cref="Engine_UiModel.InGameScreen.None"/>.
	/// </summary>
	private Engine_UiModel.InGameScreen? _inGameScreen;

	/// <summary>
	/// The screen manager's presentation-to-screen-id map, kept from the same read that settles
	/// <see cref="_inGameScreen"/> — a list needs its own screen id to find a template when the
	/// widget names no population script.
	/// </summary>
	private System.Collections.Generic.IReadOnlyDictionary<uint, int>? _screenOwners;

	/// <summary>Frames between world-icon texture samples outside a held wheel.</summary>
	private const int WorldIconSampleFrames = 30;

	/// <summary>
	/// WHAT A FOLLOW READS, each as the count or value that moves when it does: the stated live
	/// list and screen (a presentation made, destroyed, parked or given a screen; another screen
	/// stated — so a pause tab press), the front end's existence, the stated pause state (the modal
	/// latch and id, the tab), the screen a showing display box stands on, the owned boxes, the
	/// `g_Menu` statements applied to the menu model, the bridge's UI data generation (the lists'
	/// host-built sources), the book's page, and the three host states a follow acts on.
	/// </summary>
	private readonly record struct FollowKey(
		uint Presentations, bool FrontEnd, bool InGame, uint Screen, uint ModalId, uint ModalMode, uint Tab,
		uint BoxScreen, ulong Boxes, int MenuStatements, uint Data, int Page, bool Cutscene, bool ModScreen,
		bool Host);

	private FollowKey FollowInputs()
	{
		Engine_UiModel.PauseState? pause = HostBridge.GetPauseState();
		return new FollowKey(
			HostBridge.PresentationsGeneration(), HostBridge.FrontEndGuiIsUp, pause is not null,
			pause?.ScreenId ?? 0u, pause?.ModalId ?? 0u, pause?.ModalMode ?? 0u, pause?.CurrentTab ?? 0u,
			Engine_UiSource.Current.DisplayBoxScreen(), _boxes.Generation, _menuModel.Applied,
			HostBridge.UiDataGeneration(), Fable2PageProviders.BookPage, _fullCutscene, _modScreenHidHud,
			_presentation is not null);
	}

	private FollowKey _followInputs;

	/// <summary>
	/// The screen whose presentation the follower is drawing — <see cref="Engine_UiModel.InGameScreen.None"/>
	/// when it draws none of the manager's thirty. Restated on every follow, in TracePresentations.
	/// </summary>
	private uint _followedScreen = (uint)Engine_UiModel.InGameScreen.None;

	/// <summary>Guest frames a follow has been repeated for since its inputs last moved.</summary>
	private int _unsettledFrames;

	/// <summary>
	/// Guest frames a follow is repeated for a stated screen whose presentation does not stand.
	/// `NextEpisode` (13) and `NPCHighlightScreen` (20) are in no bank, so that state is reachable
	/// and must end; the next moved input starts it again.
	/// </summary>
	private const int UnsettledFrames = 30;

	/// <summary>
	/// True while the last follow did not finish: the guest states a screen the follower does not
	/// draw (the screen is stated when it is pushed, its presentation over the guest's following
	/// ticks; leaving, the old screen's tree is still up), or the screen is followed and owns its
	/// list but the list has no rows yet. The follow is repeated on the next guest frame rather
	/// than left for an input to move, because what it waits for on our side — a document, the
	/// string table, a sampled source — states nothing when it arrives.
	/// </summary>
	private bool FollowUnsettled()
	{
		// Null is the front end, or no guest at all: the follower's other branch runs there.
		if (HostBridge.GetPauseState() is not { } state)
		{
			_unsettledFrames = 0;
			return false;
		}

		if (state.ScreenId != _followedScreen)
		{
			if (_unsettledFrames >= UnsettledFrames)
			{
				return false;
			}

			++_unsettledFrames;
			return true;
		}

		if (!state.ModalUp && ScreenOwnsList(state.ScreenId) && FindListPresentation(state.ScreenId) is null)
		{
			++_unsettledFrames;
			return true;
		}

		// What the follow waited on, said once per transition.
		if (_unsettledFrames > 0)
		{
			GD.Print($"[game-ui] the follower settled on screen {_followedScreen} after " +
				$"{_unsettledFrames} repeated follow(s)");
			_unsettledFrames = 0;
		}

		return false;
	}

	/// <summary>Puts the UI on screen. Idempotent.</summary>
	public void Reveal()
	{
		if (IsRevealed || _container is null || _viewport is null)
		{
			return;
		}

		IsRevealed = true;
		_container.Visible = true;

		// Always while it is up, not Once-per-rebuild. A menu is static between guest state
		// changes, so redrawing on demand is tempting — but a window resize reallocates the
		// render target and an on-demand viewport has nothing to put back in it, so the front
		// end blanks until the guest happens to change something. 77 unshaded quads a frame is
		// not worth that.
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

		// _showing, never Screen: the export is the STARTING presentation and never changes, so
		// naming it reported the title screen long after the guest had moved on. Null once the
		// front end has gone.
		GD.Print(_showing is { } showing
			? $"[game-ui] revealed '{showing}'"
			: $"[game-ui] revealed with no front-end screen{(HostBridge.IsInGame ? " (in game)" : string.Empty)}");
	}

	/// <summary>
	/// Takes the UI back off screen — a movie playing over the front end.
	///
	/// SAYS WHY, because the reveal does and the pair is only readable together. There are two
	/// callers with completely different meanings — the guest having no live UI left, and a
	/// movie starting — and the movie one used to be silent, so a layer that went down and
	/// stayed down could not be told apart from one that was never revealed.
	/// </summary>
	public void Conceal(string reason = "unstated")
	{
		if (!IsRevealed || _container is null || _viewport is null)
		{
			return;
		}

		IsRevealed = false;
		_container.Visible = false;
		GD.Print($"[game-ui] concealed '{_showing ?? Screen}' — {reason}");

		// The render target goes idle too. Hiding the container alone leaves the front end
		// being drawn into a texture nothing samples.
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
	}
}
