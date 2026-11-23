using Godot;
using Refii.Core;
using Refii.Game;
using Refii.Host;
using Refii.Ui;
using static Refii.Game.GameInput;

namespace Refii.Titles.Fable2;

/// <summary>
/// Fable 2's part in the pads: the host-owned menus' masks over what the guest sees (the front
/// end, the pause book, the query and message boxes, the received-item card, the option and
/// screen lists), the menus' own reading of the owner's pad, the expression wheel's and spells
/// menu's hold levers, and local player two's wheel. The shell's <see cref="GameInput"/> hands
/// each pad here once it has gathered every source.
/// </summary>
public sealed class Fable2Input : GameTitleInput
{
	public static Fable2Input Instance { get; } = new();

	public override Published PublishPad(int pad, bool connected, PadState seen, PadState guest)
	{
		if (pad == 0)
		{
			// THE SPELLS MENU'S HOLD — GameAction 42 on press, 43 on release, which the shipped
			// scheme binds to XboxButton 16: the RIGHT TRIGGER. The 30/255 threshold is the XInput
			// convention. Read after the mods withhold theirs.
			bool spells = guest.RightTrigger > 30;
			SpellsMenuPressed = spells && !_spellsWasDown;
			SpellsMenuReleased = !spells && _spellsWasDown;
			SpellsMenuHeld = spells;
			_spellsWasDown = spells;
		}

		Published published = SetMasked(pad, connected, guest);

		if (pad == 0)
		{
			PublishHolds(seen.Buttons);
		}

		// Local player two's own expression wheel, off their own pad.
		if (pad == PlayerTwoPad)
		{
			PublishWheelTwo(guest.Buttons, guest.LeftX, guest.LeftY);
		}

		// The host-owned menu reads its owner's pad: pad 0 until the guest states player two
		// opened it. Pad 0's is read before the mods withhold anything.
		if (pad == HostMenuPad)
		{
			PublishMenu(pad, seen.Buttons, seen.LeftX, seen.LeftY, seen.RightX, seen.RightY);
		}

		return published;
	}

	public override object? Describe() => new
	{
		menu_mask = MaskMenuInput, pause_mask = MaskPauseInput, query_mask = MaskPauseQueryInput,
		card_mask = MaskReceiveItemsInput, box_mask = MaskMenuBoxInput, screen_mask = MaskScreenMenuInput,
		denied_button_bits = _maskBits[0], held_across_unmask = _heldAcrossUnmask[0],
		menu_direction = MenuDirection, menu_horizontal = MenuHorizontal,
		accept_edge = MenuAcceptPressed, back_edge = MenuBackPressed,
	};

	/// <summary>Keys a pending Android tap to the guest's screens and modals and this title's host ownership — highlights and animation never invalidate a held input.</summary>
	internal override GameInputButtonRoute ButtonRoute()
	{
		bool found = HostBridge.TryGetFrontEnd(out var screen, out uint state, out _, out _);
		Engine_UiModel.PauseState? pause = HostBridge.GetPauseState();
		uint flags = (PauseMenuSwallowsBack ? 1u : 0) | (FrontEndSwallowsBack ? 2u : 0) |
			(ModalBoxOwnsInput ? 4u : 0) | (DisplayMessageBoxUp ? 8u : 0) |
			(ReceiveItemsOwnsInput ? 16u : 0) | (MenuBoxOwnsInput ? 32u : 0) |
			(ScreenMenuOwnsInput ? 64u : 0) | (FrontEndDrive.ListUp ? 128u : 0) |
			(FrontEndDrive.CardUp ? 256u : 0) | (FrontEndDrive.WarningUp ? 512u : 0);
		return new(found, found ? (uint)screen : 0, found ? state : 0,
			pause?.ScreenId ?? uint.MaxValue, pause?.ModalId ?? 0, pause?.ModalMode ?? 0,
			pause?.CurrentTab ?? 0, flags);
	}

	/// <summary>Pad 0 is player one's; local player two's own pad is theirs.</summary>
	public override int ModPlayer(int pad) => pad == 0 ? 1 : pad == PlayerTwoPad ? 2 : 0;

	/// <summary>
	/// In the world with nothing holding the input: no front end, pause book, owned screen, box,
	/// card or pub game board. Decides what Escape and the mouse buttons mean, and whether the mouse
	/// is captured.
	/// </summary>
	public override bool WorldHasInput
	{
		get
		{
			bool menuOwns = Engine_TitleGame.Active.InteractionOwnsPad || HostUiOwnsPad || ScreenMenuOwnsInput || CoopScreenOwnsInput ||
				ModalBoxOwnsInput || MenuBoxOwnsInput ||
				ReceiveItemsOwnsInput || DisplayMessageBoxUp || HostBridge.FrontEndGuiIsUp ||
				GamePubGames.Showing.Length != 0;
			return HostBridge.IsInGame && !menuOwns && !MaskPauseInput &&
				HostBridge.GetPauseState() is not { ModalMode: not 1 };
		}
	}

	public override void HoldUntilReleased()
	{
		for (int pad = 0; pad < MaxControllers; pad++)
		{
			_heldAcrossUnmask[pad] = uint.MaxValue;
		}
	}

	/// <summary>The expression wheel and the spell blade are pointed at while held.</summary>
	public override bool PointerOverlay => QuickWheelHeld || SpellsMenuHeld;

	/// The player whose menu is open, as the guest states it: 0 not stated, 1 player one, 2
	/// player two. Set by <c>GameUi</c> every frame, and only ever non-zero on split screen.
	/// </summary>
	/// <remarks>
	/// <para>Not stated is the shared-screen rule, unchanged: the host-owned menus read pad 0 and
	/// the menu masks hold every pad. Stated, the menu belongs to ONE player — the guest's pause is
	/// owned by the player who opened it, and player two's START opens their own reduced root —
	/// so that player's pad drives the host menu and only that pad is masked; the other player's
	/// pad reaches the guest as it would on the console, where nothing masked it.</para>
	/// <para>The hold levers stay on pad 0 whoever owns the menu — the quick wheel (Back), the
	/// spells menu (RT), <see cref="AHeld"/>/<see cref="YHeld"/> and <see cref="LeftStick"/>: the
	/// pump's <c>QuickWheel:</c>, <c>SpellsMenu:</c> and <c>SpellBlade:</c> levers act for the
	/// local player, who is player one in local co-op, and the TopBox's hold prompt is player
	/// one's HUD.</para>
	/// </remarks>
	public static int MenuOwner { get; set; }

	/// <summary>Local player two's guest pad (1..3) while local co-op runs, else −1. Set by <c>GameUi</c>.</summary>
	public static int PlayerTwoPad { get; set; } = -1;

	/// <summary>The pad of the player <see cref="MenuOwner"/> names, or −1 while none is stated.</summary>
	private static int MenuPad => MenuOwner switch
	{
		1 => 0,
		2 when PlayerTwoPad is >= 1 and < MaxControllers => PlayerTwoPad,
		_ => -1,
	};

	/// <summary>The pad the host-owned menus read: the owner's, or pad 0 while no owner is stated.</summary>
	private static int HostMenuPad => MenuPad < 0 ? 0 : MenuPad;

	/// <summary>Whether the menu masks hold <paramref name="pad"/>: every pad until an owner is
	/// stated, then the owner's alone.</summary>
	private static bool MasksPad(int pad) => MenuPad < 0 || pad == MenuPad;

	/// Publishes one pad with the owned menus' masks applied. Two consumers: the guest gets a
	/// filtered set, the host-owned menu gets the real one. The guest still runs its own
	/// front-end menu — see <see cref="WithoutMenuInput"/>.
	/// </summary>
	/// <summary>Per pad: the buttons the guest was denied last frame.</summary>
	private static readonly uint[] _maskBits = new uint[MaxControllers];

	/// <summary>Per pad: buttons still held since a mask over them lifted — hidden from the
	/// guest until released. See <see cref="SetMasked"/>.</summary>
	private static readonly uint[] _heldAcrossUnmask = new uint[MaxControllers];

	private Published SetMasked(int pad, bool connected, PadState state)
	{
		uint buttons = state.Buttons;

		// The owned menus' masks hold the menu owner's pad — every pad until an owner is stated.
		bool owned = MasksPad(pad);
		bool masked = owned && MaskMenuInput;
		bool goldenMenu = Engine_TitleGame.Active.InteractionOwnsPad || (owned && HostUiOwnsPad);
		bool pauseMasked = owned && !masked && MaskPauseInput;
		bool queryMasked = owned && !masked && !pauseMasked && MaskPauseQueryInput;
		// The card's mask keeps the sticks and the D-pad live: its screen does not pause the
		// game, so the stick-zeroing below must not apply to it.
		bool cardMasked = owned && !masked && !pauseMasked && !queryMasked && MaskReceiveItemsInput;
		bool boxMasked = owned && !masked && !pauseMasked && !queryMasked && !cardMasked &&
			MaskMenuBoxInput;
		bool listMasked = owned && !masked && !pauseMasked && !queryMasked && !cardMasked &&
			!boxMasked && MaskScreenMenuInput;
		bool menuMask = goldenMenu || masked || pauseMasked || queryMasked || boxMasked || listMasked;
		System.Func<uint, uint> filter =
			goldenMenu ? (Refii.Mods.ModScreen.Owned ? static held => held & Start : static _ => 0u)
			: masked ? WithoutMenuInput
			: pauseMasked ? WithoutPauseInput
			: queryMasked ? WithoutQueryInput
			: cardMasked ? WithoutCardInput
			: boxMasked ? WithoutQueryInput
			: listMasked ? WithoutListInput
			: static b => b;

		// A MASK LIFTING NEVER HANDS THE GUEST A PRESS. The guest edge-detects against its last
		// pad read (`sub_821894A8`), so a held button that reappears is a fresh down edge, and
		// A's down edge emits GameAction 46/36/6 — which `sub_824047E8` hands to any box then
		// up. A button held across its unmask stays hidden until it is physically released.
		// THE SPELL WHEEL'S PICK BUTTON IS THE HOST'S while the wheel is held. The shipped combat
		// scheme binds every button under the held trigger — X (code 13) is the on-release attack,
		// parry and block, Y (14) the ranged aim — so a mod's secondary pick
		// (Engine_UiSpellAssignSecondary) is withheld from the guest for the hold, and a press
		// still down when the trigger lifts stays hidden until released, like any lifted mask.
		// X applies a partner and Y removes one, so both are the host's under the held trigger.
		// The trigger is pad 0's, so on split screen the pick is withheld from pad 0 alone.
		uint wheelBits = SpellsMenuHeld && Engine_TitleGame.Active.WheelClaimsFaceButtons &&
			(pad == 0 || !Refii.Core.Engine_Views.Split)
			? (ButtonX | ButtonY)
			: 0u;

		// LOCAL PLAYER TWO'S BACK IS THE HOST'S: it holds their expression wheel (the pump's
		// QuickWheel:2: levers). Shipped, the henchman's Back teleported them to the hero — the
		// leash's crutch, which free roam retires (Fable 3 has none).
		if (pad == PlayerTwoPad && PlayerTwoPad >= 1)
		{
			wheelBits |= Back;
		}
		// The mods' wheel takes the face buttons and bumpers of the player whose wheel stands open.
		if ((pad == 0 && Refii.Mods.ModWheel.Open) ||
			(pad == PlayerTwoPad && PlayerTwoPad >= 1 && Refii.Mods.ModWheel.IsOpen(2)))
		{
			wheelBits |= ButtonA | ButtonB | ButtonX | ButtonY | LeftShoulder | RightShoulder;
		}

		uint maskBits = ~filter(uint.MaxValue) | wheelBits;
		_heldAcrossUnmask[pad] |= _maskBits[pad] & ~maskBits & buttons;
		_heldAcrossUnmask[pad] &= buttons;
		_maskBits[pad] = maskBits;

		// The shell publishes it (GameInput.Deliver): the transport is the shell's, the mask the title's.
		return new Published(new GameControllerPacket(
			pad, connected,
			filter(buttons) & ~wheelBits & ~_heldAcrossUnmask[pad],
			goldenMenu ? (byte)0 : state.LeftTrigger, goldenMenu ? (byte)0 : state.RightTrigger,
			// The stick is masked with the buttons: `sub_826E39C0` dead-zones the left stick
			// against ±0.75 and emits the same codes the D-pad does — 28/29 for Y at
			// 0x826E3C04/0x826E3C58, 30/31 for X at 0x826E3B50/0x826E3BAC.
			menuMask ? 0 : state.LeftX, menuMask ? 0 : state.LeftY,
			// The card takes the right stick — it turns the model on the front and scrolls the
			// description on the back — so the camera does not swing under it. The left stick
			// stays live: the card does not pause the game.
			//
			// Invert X is ours: the guest has a vertical invert (CameraLookInverted, record33+0x2A,
			// negated at 0x825ADD28) and nothing for the horizontal. Applied only while the stick
			// is the camera's — never under a menu, which reads it as a direction.
			goldenMenu || cardMasked ? 0 : (InvertCameraX && !menuMask ? -state.RightX : state.RightX),
			goldenMenu || cardMasked ? 0 : state.RightY),
			maskBits,
			filter(state.PhysicalButtons) & ~wheelBits & ~_heldAcrossUnmask[pad],
			filter(state.OtherButtons) & ~wheelBits & ~_heldAcrossUnmask[pad]);
	}

	/// <summary>
	/// Whether the guest's own front-end menu should be shielded from this frame's input.
	/// True while the guest's active screen is MainMenu in a substate the host draws over; the
	/// title keeps every button (A there is the guest's contract, `a2 == 36 &amp;&amp;
	/// *(BYTE*)(a1+80)` at 0x826E3078). `screen+24` is 0 for the menu list; 1 is the guest's own
	/// input lock, 2/3/4 the gender card, 5 the load-game carousel, 6/7 a modal. 1 and 6/7 are
	/// never masked: a modal parks the screen in substate 1 until an A press, and
	/// `sub_826DEE40` answers the dialog itself in mode 6 (36 at 0x826df9c0, 37 at 0x826dfc98).
	/// The screen and its mode are the guest's own statements (HostBridge.TryGetFrontEnd).
	/// </summary>
	private static bool MaskMenuInput
	{
		get
		{
			bool wouldMask =
				HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen, out uint state,
					out _, out _) &&
				screen == Engine_UiModel.FrontEndScreen.MainMenu && HostDrawsOver(state);

			// Never mask once a world is up, whatever the front-end decode says: masking exists
			// only to stop Godot's main menu and the guest's from acting on one press, and a
			// misdecoded MainMenu here would zero the left stick and freeze the player.
			if (wouldMask && (HostBridge.IsInGame || HostBridge.WorldIsActive()))
			{
				if (!_reportedInGameMask)
				{
					_reportedInGameMask = true;
					GD.Print("[game-input] the front end decoded as MainMenu while a world is up — " +
						"NOT masking input. Left unmasked the player would have been unable to " +
						"move, and the camera with them.");
				}

				return false;
			}

			return wouldMask;
		}
	}

	private static bool _reportedInGameMask;

	/// <summary>Set while the owned pause menu has a group expanded: B then collapses the
	/// group instead of reaching the guest, whose own B on the pause root resumes the game.</summary>
	public static bool PauseMenuSwallowsBack { get; set; }

	/// <summary>
	/// Set while the owned front end answers B itself — a folder open in the list, the gender
	/// card or warning box up, or a list book or host page book standing over the rows. The guest's B on the root list leaves for the title, and its Lua
	/// highlight always reads "at root" here (28/29 are masked), so B must not reach it.
	/// </summary>
	public static bool FrontEndSwallowsBack { get; set; }

	/// <summary>
	/// What the front end GODOT DRAWS is showing, for the unattended driver.
	///
	/// The driver used to read the guest's scene slide and treat slide 1 as "PressStart, the row
	/// list is not up yet". That slide never leaves 1 on this build, so the driver pressed A on
	/// a list it could see perfectly well — re-opening `New Game` and the gender card once every
	/// cooldown, for ever, fighting anything else driving the run.
	///
	/// It is the wrong question to ask the guest at all. Godot owns this menu: it builds the
	/// rows, holds the highlight and decides whether the card or the warning stands. So the
	/// driver asks the thing that draws it, by ROW NAME rather than by counted steps.
	/// </summary>
	public readonly record struct FrontEndDriveState(
		bool ListUp, bool CardUp, bool WarningUp, string Highlighted);

	public static FrontEndDriveState FrontEndDrive { get; set; }

	/// <summary>

	/// <summary>
	/// Set by the <c>GameUi</c> modal-box drives while an owned box view is standing (query box,
	/// message box, numeric adjuster). A, B and the adjuster's D-pad are then the box's
	/// vocabulary, dispatched through the guest's router; the guest must not also see the press
	/// or the box answers twice. Re-derived every frame, and ANDed with the guest's own state.
	/// </summary>
	public static bool ModalBoxOwnsInput { get; set; }

	/// <summary>
	/// Set by <c>GameUi</c> while a host surface the guest has no part in stands over the play or
	/// the title: a host box on the front end, a mod's menu opened with no list under it. The whole
	/// pad is the host's then, as under the Golden Cock's menu — the guest sees no press, stick or
	/// trigger. Re-derived every frame; a button held across the lift stays hidden until released.
	/// </summary>
	public static bool HostUiOwnsPad { get; set; }

	/// <summary>Set while the guest has a display MessageBox open — stated by the class's own
	/// Open and Close, not the router's modal latch, which such a box never sets.</summary>
	public static bool DisplayMessageBoxUp { get; set; }

	/// <summary>Masks the box's buttons while the guest still says a modal dialog is pending
	/// (the mode latch, over ANY screen — the openers never change the screen id), has a display
	/// box open — which never sets that latch — or a HOST question or notice stands in the query
	/// slot: a standing box owns the pad whoever raised it, or A answers it and reaches the guest
	/// too. Fails open.</summary>
	private static bool MaskPauseQueryInput =>
		ModalBoxOwnsInput &&
		(DisplayMessageBoxUp || Engine_UiDisplayBoxes.Showing is not null ||
			HostBridge.GetPauseState() is { ModalUp: true } ||
			Refii.Ui.Engine_UiHostBoxes.Question is not null ||
			Refii.Ui.Engine_UiHostBoxes.Notice is not null);

	/// <summary>
	/// Set by <c>GameUi.DriveReceiveItems</c> while the owned received-item card is standing.
	/// The card does not pause the game, so only its own buttons are taken.
	/// </summary>
	public static bool ReceiveItemsOwnsInput { get; set; }

	private static bool MaskReceiveItemsInput =>
		ReceiveItemsOwnsInput &&
		HostBridge.GetPauseState() is { ScreenId: 8, ModalMode: 1 };

	/// <summary>A (done), Y (flip) and X (equip, code 75): each is posted as the card's own action, so the guest must not read the pad for it as well.</summary>
	private static uint WithoutCardInput(uint buttons) => buttons & ~(ButtonA | ButtonY | ButtonX);

	/// <summary>
	/// Set by <c>GameUi.DriveMenuBox</c> while the owned option list is standing. Its whole
	/// vocabulary is taken: the D-pad moves the guest's selection through our lever, so a press
	/// the guest also saw would double every step.
	/// </summary>
	public static bool MenuBoxOwnsInput { get; set; }

	private static bool MaskMenuBoxInput => MenuBoxOwnsInput;

	/// <summary>
	/// Set by <c>GameUi.DriveScreenMenu</c> while the owned in-game list is standing. The whole
	/// vocabulary goes, stick included: every press is forwarded to the guest as its own lever
	/// (a direction becomes <c>g_Menu:ScrollUp/ScrollDown()</c>, A/B become action codes 36/37),
	/// and `sub_826E39C0` turns the left stick into the same 28..31 codes the D-pad emits.
	/// Re-derived every frame and ANDed with the guest's own state. Fails open.
	/// </summary>
	public static bool ScreenMenuOwnsInput { get; set; }

	/// <summary>
	/// Set by <c>GameUi</c> while the co-op screen stands. The guest reads that screen's pad
	/// itself, so nothing is masked; it only stops counting as play — the cursor is free for its
	/// buttons and rows, and B is back, not magic.
	/// </summary>
	public static bool CoopScreenOwnsInput { get; set; }

	private static bool MaskScreenMenuInput =>
		ScreenMenuOwnsInput && HostBridge.GetPauseState() is { ModalMode: 1, ScreenId: not 0 };

	/// <summary>
	/// The buttons with the owned list's WHOLE vocabulary taken out. Godot owns the cursor and
	/// every press on it: A and the directions are its own levers, B is forwarded as the screen's
	/// own back, LB/RB as the tab cycle (72/73), Y is the ability unlearn or a mod's row action,
	/// X a mod's row action. The guest's input router `sub_824047E8` would otherwise answer the
	/// same pad press against ITS highlight — Y arms the abilities discard (`sub_826C2958` code
	/// 39 → `+198 = 0`) for the guest's row, and a shoulder would cycle the tab twice.
	/// </summary>
	private static uint WithoutListInput(uint buttons) =>
		WithoutQueryInput(buttons) & ~(ButtonX | ButtonY | LeftShoulder | RightShoulder);

	/// <summary>The buttons with the owned query box's whole vocabulary taken out — A and B
	/// both, unlike the pause root where B is the guest's resume.</summary>
	private static uint WithoutQueryInput(uint buttons) =>
		buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA | ButtonB);

	/// <summary>
	/// Whether the guest's own pause menu should be shielded from this frame's input. Gated on
	/// the guest's own statement, not the UI's: the mask engages the frame the guest says the
	/// pause root is the active screen (id 0, no modal pending), because the owned view appears
	/// only on the mirror's sampling cadence. The bridge admits only the in-game GUI object
	/// (vtable 0x82001610), so this can never read "pause root" over the front end. Every pause
	/// modal changes the screen id or sets the modal id (query boxes 23/24/25/28), so a dialog
	/// unmasks the frame it opens. Fails open: no pause state, no mask.
	/// </summary>
	private static bool MaskPauseInput =>
		HostBridge.GetPauseState() is { RootActive: true };

	/// <summary>
	/// The buttons with the pause menu's navigation taken out: the guest's pause list is still
	/// populated and `expandablemenuinput.lua` still binds A. LB/RB stay through — the guest's
	/// own tab switch is the lever our tab entries write. B is conditional, see
	/// <see cref="PauseMenuSwallowsBack"/>.
	/// </summary>
	/// <summary>
	/// The buttons a standing list's ROW ACTIONS bind this frame (GameUi.DriveRowActionsOn),
	/// withheld from the guest. Re-derived every frame: reset before the drives, set by the one
	/// that stands a list with row actions.
	/// </summary>
	public static uint RowActionHolds { get; set; }

	public const uint PadButtonX = ButtonX;

	public const uint PadButtonY = ButtonY;

	private static uint WithoutPauseInput(uint buttons)
	{
		uint filtered = buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA | RowActionHolds);
		if (PauseMenuSwallowsBack)
		{
			filtered &= ~ButtonB;
		}

		return filtered;
	}

	/// <summary>
	/// `screen+24` while the menu LIST is the thing on screen — see <see cref="MaskMenuInput"/>.
	/// </summary>
	private const uint MenuRootState = 0;

	/// <summary>`screen+24` while the guest's own gender card stands (2 both down, 3 boy, 4 girl).</summary>
	private const uint CardFirstState = 2;

	private const uint CardLastState = 4;

	/// <summary>`screen+24` while the guest's load-game carousel stands.</summary>
	private const uint LoadCarouselState = 5;

	/// <summary>
	/// Whether the host draws its own panel over this substate, so the guest's handler for the
	/// same press must not run. Root: the host's row list. Carousel: the host's load fan posts
	/// `LoadSave:N` by its own cursor, while `sub_826DEE40` would commit the guest's cursor
	/// `+72` on A (0x826df880) and move it on 30/31 (0x826df170, 0x826df2bc). Card: the host's
	/// card posts "New Game" / "New Game Female" itself, so only while it stands. B stays
	/// through on the carousel (<see cref="WithoutMenuInput"/>): leaving it is the guest's own
	/// `+24 = 0` at 0x826dfb7c, and the host's fan follows that substate.
	/// </summary>
	private static bool HostDrawsOver(uint state) =>
		state == MenuRootState ||
		state == LoadCarouselState ||
		(state >= CardFirstState && state <= CardLastState && FrontEndDrive.CardUp);

	/// <summary>
	/// The buttons with the front end's navigation taken out. Godot owns the main menu and
	/// dispatches by name, but the guest never stopped running its own: `sub_826DEE40` still
	/// forwards A and B to `g_Menu` through `OnPressA`/`OnPressB` while `screen+24` is 0, and
	/// the two highlights are different objects. B is left through at the root, where it is the
	/// guest's own path to the title, and on the carousel, where it is the guest's own exit;
	/// <see cref="FrontEndSwallowsBack"/> masks it while a host panel answers B itself.
	/// </summary>
	private static uint WithoutMenuInput(uint buttons)
	{
		uint filtered = buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA);
		if (FrontEndSwallowsBack)
		{
			filtered &= ~ButtonB;
		}

		return filtered;
	}

	/// <summary>
	/// The same frame's input, in the terms a host-owned menu needs. Reuses the buttons already
	/// gathered for the guest rather than polling the devices a second time, so the two readers
	/// cannot drift. The stick folds into the direction on the shipped threshold —
	/// `THUMBSTICK_RELEASE_THRESHOLD` 0.65, from `guistateandconstants.lua`.
	/// </summary>
	private static void PublishMenu(int pad, uint buttons, int leftX, int leftY, int rightX, int rightY)
	{
		// A NEW SOURCE HANDS THE MENU NO PRESS. When the menu changes hands, a button already down
		// on the new pad is taken as held, not as a fresh edge.
		if (pad != _menuSourcePad)
		{
			_menuSourcePad = pad;
			_acceptWasDown = (buttons & ButtonA) != 0;
			_backWasDown = (buttons & ButtonB) != 0;
			_flipWasDown = (buttons & ButtonY) != 0;
			_tabPrevWasDown = (buttons & LeftShoulder) != 0;
			_tabNextWasDown = (buttons & RightShoulder) != 0;
			_equipWasDown = (buttons & ButtonX) != 0;
		}

		// The deadzone is ours here: `page.lua` reads `GUI.State.RightThumbStickY` raw (page.dis
		// main.1 pc18..21) because the guest has already dead-zoned it before firing the contract
		// event, and our page reads the pad instead. XInput's right-thumb constant is 8689 of
		// 32767, rescaled so travel starts at zero on the deadzone edge.
		const float RightThumbDeadzone = 8689f;
		float magnitude = System.Math.Abs(rightY);
		RightStickY = magnitude <= RightThumbDeadzone
			? 0f
			: System.Math.Sign(rightY) *
				((magnitude - RightThumbDeadzone) / (32767f - RightThumbDeadzone));

		float sideways = System.Math.Abs(rightX);
		RightStickX = sideways <= RightThumbDeadzone
			? 0f
			: System.Math.Sign(rightX) *
				((sideways - RightThumbDeadzone) / (32767f - RightThumbDeadzone));

		int direction = 0;
		if ((buttons & DpadUp) != 0)
		{
			direction = -1;
		}
		else if ((buttons & DpadDown) != 0)
		{
			direction = 1;
		}
		else
		{
			// Axis() returns the Xbox's signed range, and up is positive after the negation
			// above — so a positive Y is "up", which is -1 in list order.
			double stick = leftY / 32767.0;
			if (Held(ref _pushingUp, stick))
			{
				direction = -1;
			}
			else if (Held(ref _pushingDown, -stick))
			{
				direction = 1;
			}
		}

		MenuDirection = direction;

		// The same fold across X, for the panels navigated sideways (gender card, load-game
		// carousel). `sub_826E39C0` compares the dead-zoned X against ±0.75 at
		// 0x826E3B3C/0x826E3B98 and emits code 30 left, 31 right, as it emits 28/29 off Y. Its
		// 0.75 is a stick-as-button threshold; one threshold governs both axes here, or a
		// diagonal push scrolls a menu and flips a card at once.
		int horizontal = 0;
		if ((buttons & DpadLeft) != 0)
		{
			horizontal = -1;
		}
		else if ((buttons & DpadRight) != 0)
		{
			horizontal = 1;
		}
		else
		{
			double stick = leftX / 32767.0;
			if (Held(ref _pushingLeft, -stick))
			{
				horizontal = -1;
			}
			else if (Held(ref _pushingRight, stick))
			{
				horizontal = 1;
			}
		}

		MenuHorizontal = horizontal;

		// Edge-detected, as the guest does: holding A must not activate the entry every frame.
		bool a = (buttons & ButtonA) != 0;
		bool b = (buttons & ButtonB) != 0;
		bool y = (buttons & ButtonY) != 0;
		bool lb = (buttons & LeftShoulder) != 0;
		bool rb = (buttons & RightShoulder) != 0;
		bool x = (buttons & ButtonX) != 0;
		MenuAcceptPressed = a && !_acceptWasDown;
		MenuAcceptReleased = !a && _acceptWasDown;
		MenuBackPressed = b && !_backWasDown;
		MenuFlipPressed = y && !_flipWasDown;
		MenuTabPrevPressed = lb && !_tabPrevWasDown;
		MenuTabNextPressed = rb && !_tabNextWasDown;
		MenuEquipPressed = x && !_equipWasDown;
		_acceptWasDown = a;
		_backWasDown = b;
		_flipWasDown = y;
		_tabPrevWasDown = lb;
		_tabNextWasDown = rb;
		_equipWasDown = x;
	}

	/// <summary>The pad the host menu read last frame — see <see cref="PublishMenu"/>.</summary>
	private static int _menuSourcePad;

	/// <summary>
	/// The quick wheel's hold, edge-detected off pad 0 alone: the pump's quick-wheel levers act
	/// for the local player, who is player one in local co-op (see <see cref="MenuOwner"/>).
	/// </summary>
	private static void PublishHolds(uint buttons)
	{
		bool wheel = (buttons & Back) != 0;

		// With a controller and mods on it, Back opens and closes the mods' wheel instead of holding this one.
		if (Refii.Mods.ModWheel.Follow(wheel, Instance.WorldHasInput, out bool held))
		{
			wheel = held;
		}

		QuickWheelPressed = wheel && !_wheelWasDown;
		QuickWheelReleased = !wheel && _wheelWasDown;
		QuickWheelHeld = wheel;
		_wheelWasDown = wheel;
	}

	/// <summary>
	/// Local player two's expression-wheel levers, edge-detected off their own pad: Back holds
	/// the wheel, A selects (its release ends a held expression), B backs out, the left stick
	/// points. The pump's <c>QuickWheel:2:</c> levers open it for player two.
	/// </summary>
	private static void PublishWheelTwo(uint buttons, int leftX, int leftY)
	{
		bool wheel = (buttons & Back) != 0;

		// Player two's own mods' wheel, as player one's (PublishHolds): Back opens and closes it.
		if (Refii.Mods.ModWheel.Follow(2, wheel, Instance.WorldHasInput, out bool held))
		{
			wheel = held;
		}

		bool lb = (buttons & LeftShoulder) != 0;
		bool rb = (buttons & RightShoulder) != 0;
		WheelTwoPrevPressed = lb && !_lbTwoWasDown;
		WheelTwoNextPressed = rb && !_rbTwoWasDown;
		_lbTwoWasDown = lb;
		_rbTwoWasDown = rb;
		bool a = (buttons & ButtonA) != 0;
		bool b = (buttons & ButtonB) != 0;
		QuickWheelTwoPressed = wheel && !_wheelTwoWasDown;
		QuickWheelTwoReleased = !wheel && _wheelTwoWasDown;
		QuickWheelTwoHeld = wheel;
		WheelTwoAcceptPressed = a && !_acceptTwoWasDown;
		WheelTwoAcceptReleased = !a && _acceptTwoWasDown;
		WheelTwoBackPressed = b && !_backTwoWasDown;
		bool x = (buttons & ButtonX) != 0;
		XTwoPressed = x && !_xTwoWasDown;
		LeftStickTwo = new Vector2(leftX / 32767f, leftY / 32767f);
		_wheelTwoWasDown = wheel;
		_acceptTwoWasDown = a;
		_backTwoWasDown = b;
		_xTwoWasDown = x;
	}

	private static bool _wheelTwoWasDown;
	private static bool _lbTwoWasDown;
	private static bool _rbTwoWasDown;
	private static bool _acceptTwoWasDown;
	private static bool _backTwoWasDown;
	private static bool _xTwoWasDown;

	/// <summary>True on the frame local player two's X went down — their hero toggle on the co-op screen.</summary>
	public static bool XTwoPressed { get; private set; }

	public static bool QuickWheelTwoPressed { get; private set; }
	public static bool QuickWheelTwoReleased { get; private set; }
	public static bool QuickWheelTwoHeld { get; private set; }
	public static bool WheelTwoAcceptPressed { get; private set; }
	public static bool WheelTwoAcceptReleased { get; private set; }
	public static bool WheelTwoBackPressed { get; private set; }

	/// <summary>Local player two's LB / RB went down: their mods' wheel turns its page.</summary>
	public static bool WheelTwoPrevPressed { get; private set; }
	public static bool WheelTwoNextPressed { get; private set; }
	public static Vector2 LeftStickTwo { get; private set; }

	/// <summary>
	/// One direction's stick hysteresis, as `guistateandconstants.lua` states it: engages above
	/// `THUMBSTICK_PUSH_THRESHOLD` (0.70), stays engaged until below
	/// `THUMBSTICK_RELEASE_THRESHOLD` (0.65), hence one held flag per direction.
	/// </summary>
	/// <param name="magnitude">The stick along that direction, signed so pushing this way is
	/// positive.</param>
	private static bool Held(ref bool held, double magnitude)
	{
		held = held
			? magnitude > Refii.Ui.Engine_FrontEndMenu.ThumbstickReleaseThreshold
			: magnitude >= Refii.Ui.Engine_FrontEndMenu.ThumbstickPushThreshold;
		return held;
	}

	/// <summary>The four `Pushing*Held` flags the shipped input model keeps.</summary>
	private static bool _pushingUp;
	private static bool _pushingDown;
	private static bool _pushingLeft;
	private static bool _pushingRight;

	/// <summary>-1 up, +1 down, 0 centred — for a host-owned menu.</summary>
	public static int MenuDirection { get; private set; }

	/// <summary>-1 left, +1 right, 0 centred — for a host-owned panel navigated sideways.</summary>
	public static int MenuHorizontal { get; private set; }

	/// <summary>True on the frame A went down.</summary>
	public static bool MenuAcceptPressed { get; private set; }

	/// <summary>True on the frame A came up — the expression wheel's held-expression end.</summary>
	public static bool MenuAcceptReleased { get; private set; }

	/// <summary>

	/// <summary>True on the frame B went down.</summary>
	public static bool MenuBackPressed { get; private set; }

	/// <summary>
	/// True on the frame LB / RB went down — the tab cycle. Forwarded as action codes 72/73 by
	/// the owned drives, because the guest's route from the shoulder bits to those codes is the
	/// GDB-driven action-consumer chain, which does not fire here. The screen handler keeps its
	/// own gates: an open submenu eats the cycle (`screen+420`), `sub_826B83D0` skips
	/// unavailable tabs.
	/// </summary>
	public static bool MenuTabPrevPressed { get; private set; }

	public static bool MenuTabNextPressed { get; private set; }

	private static bool _tabPrevWasDown;
	private static bool _tabNextWasDown;

	/// <summary>True on the frame Y went down — the received-item card's flip.</summary>
	public static bool MenuFlipPressed { get; private set; }

	/// <summary>True on the frame X went down — the received-item card's equip (code 75).</summary>
	public static bool MenuEquipPressed { get; private set; }

	private static bool _equipWasDown;

	private static bool _acceptWasDown;

	private static bool _backWasDown;

	private static bool _flipWasDown;

	/// <summary>
	/// The expression wheel's hold — GameAction 40 on press, 41 on release. A HOST binding: the
	/// shipped scheme binds GameAction 40 to `XboxButton` code 15 (the left trigger), but the
	/// wheel rides the pad's Back button and keyboard Q here. Deliberate, not undecoded — the
	/// code → button table IS known (10 right bumper, 15 left trigger, 16 right trigger), read
	/// out of the GDB, where these pairings live; the executable ships the binding table empty.
	/// </summary>
	public static bool QuickWheelPressed { get; private set; }

	public static bool QuickWheelReleased { get; private set; }

	public static bool QuickWheelHeld { get; private set; }

	private static bool _wheelWasDown;

	/// <summary>
	/// The spells menu's hold — GameAction 42 on press, 43 on release, on the RIGHT TRIGGER
	/// (`XboxButton` 16). Unlike the wheel above this is the SHIPPED binding, not a host one:
	/// `GameControlsCombatOnRelease` aliases a scheme that overrides `GameActionGUISpellsMenu`
	/// onto 16 over a base of 10.
	/// </summary>
	public static bool SpellsMenuPressed { get; private set; }

	public static bool SpellsMenuReleased { get; private set; }

	public static bool SpellsMenuHeld { get; private set; }

	private static bool _spellsWasDown;

	/// The title screen's idle state. A is gated by the armed byte `titleScreen+80` (what
	/// <c>canPressA</c> reports): `a2 == 36 &amp;&amp; *(BYTE *)(a1 + 80)` at 0x826E3078, armed by
	/// the presentation's CAN_PRESS_A event at 0x826E2E38; states 0, 9, 10 and 11 all reach it.
	/// Waiting for state 0 avoids state 8, where the attract movie takes A down 0x826E3030 and
	/// only sets its skip flag.
	/// </summary>
	private const uint TitleIdleState = 0;

	/// <summary>
	/// `PressStart` — the main menu's opening slide, where the row list is not up yet and A is
	/// the only button that does anything. The row sequence waits for the slide to leave it.
	/// </summary>
	private const int PressStartSlide = 1;
}
