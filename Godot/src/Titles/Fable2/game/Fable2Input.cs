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
			SpellsMenuHeld = guest.RightTrigger > 30;
		}

		Published published = SetMasked(pad, connected, guest);

		// The platform keyboard has every pad while it is open, as the console's does.
		if (Engine_UiPlatformKeyboard.IsOpen)
		{
			published = new(new GameControllerPacket(pad, connected, 0u, 0u, 0u, 0, 0, 0, 0), guest.Buttons, 0u, 0u);
		}

		if (pad == 0)
		{
			PublishHolds(seen.Buttons);
		}

		// Player two's save picker on the co-op screen, off player two's own pad.
		if (pad == CoopPickerPad)
		{
			PublishCoopPicker(pad, seen);
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
			PublishMenu(pad, seen.Buttons, seen.LeftX, seen.LeftY, seen.RightX, seen.RightY,
				seen.LeftTrigger, seen.RightTrigger);
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
		uint flags = (FrontEndSwallowsBack ? 2u : 0) |
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
			return HostBridge.IsInGame && !menuOwns && !MaskPauseInput && !Engine_UiPlatformKeyboard.IsOpen &&
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
	public override bool PointerOverlay => QuickWheelHeld || WheelOpenStated || SpellsMenuHeld || Engine_UiPlatformKeyboard.IsOpen;

	/// <summary>
	/// The guest states player one's expression wheel open (`RefiiHostGetExpressionWheelPlayer` = 1),
	/// whatever opened it — RB in toggle mode leaves it up after the release. Set by <c>GameUi</c>.
	/// </summary>
	public static bool WheelOpenStated { get; set; }

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

	/// <summary>
	/// The pad of the player <see cref="MenuOwner"/> names, or −1 while none is stated. On the front
	/// end it is the pad the title took A from (<see cref="HostBridge.FrontEndPad"/>): the main menu
	/// answers that pad alone (`cmpw cr6,r5,r4` TU1 0x826DEE60 / disc 0x826C72C8).
	/// </summary>
	private static int MenuPad => FrontEndHoldsPads ? HostBridge.FrontEndPad : MenuOwner switch
	{
		1 => 0,
		2 when PlayerTwoPad is >= 1 and < MaxControllers => PlayerTwoPad,
		_ => -1,
	};

	/// <summary>The front-end GUI is up with no world: the front end's own pad rule holds.</summary>
	private static bool FrontEndHoldsPads => HostBridge.FrontEndGuiIsUp && !HostBridge.IsInGame;

	/// <summary>
	/// THE TITLE'S A IS A LEVER. The title's input slot (TU1 `sub_826E2F18` / disc `sub_826C61B8`)
	/// acts on A alone outside its dialog's marketplace X (38 under `+56`) and the attract's skip
	/// (Start 7 in mode 8, as A is): Godot posts A from every pad as `TitleStart:&lt;pad&gt;`, or as the
	/// dialog's `FrontEndDialog:&lt;pad&gt;:36` while the title's own dialog takes it, and the guest's
	/// pad route never sees A here. Every other button still reaches the guest: the title has no
	/// arm for them but the two above, and any press skips a playing movie through the type-12
	/// handler (TU1 0x82377DC8, `stb r10,0xF4(r11)`).
	/// </summary>
	private static bool TitleTakesA =>
		FrontEndHoldsPads &&
		HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen, out _, out _, out _) &&
		screen == Engine_UiModel.FrontEndScreen.Title;

	/// <summary>Per pad: A was down last frame on the title — its press edge posts the title's A.</summary>
	private static readonly bool[] _titleAWasDown = new bool[MaxControllers];

	/// <summary>Per pad: B was down last frame under the menu mask — its press edge posts `FrontEndBack:`.</summary>
	private static readonly bool[] _menuBWasDown = new bool[MaxControllers];

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

	/// <summary>Each pad's trigger held since a surface masked it (SetMasked); published as 0 until let go.</summary>
	private static readonly bool[] _leftTriggerAcross = new bool[MaxControllers];

	private static readonly bool[] _rightTriggerAcross = new bool[MaxControllers];

	/// <summary>Per pad: Start was down last frame — its press edge posts `Start:`.</summary>
	private static readonly bool[] _startWasDown = new bool[MaxControllers];

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
		// THE CO-OP SCREEN'S PADS ARE GODOT'S (CoopPresses): each player's own pad, with no box over the
		// screen — a box answers through the modal router, never the screen's slot.
		int coopPlayer = !goldenMenu && !masked && !pauseMasked && !queryMasked && !cardMasked && !boxMasked &&
			!listMasked && CoopScreenOwnsInput && HostBridge.GetPauseState() is not { ModalMode: not 1 } &&
			ModPlayer(pad) is var padPlayer and not 0
				? Fable2ActingPlayer.OfPad(CoopSession.IsClient, padPlayer)
				: 0;
		CoopPresses(pad, coopPlayer, buttons, state.LeftX, state.LeftY);
		bool coopMasked = coopPlayer != 0;

		// THE WHOLE PAD UNDER A SURFACE GODOT OWNS (S8): the pause book, the owned lists and screens,
		// every box Godot answers, the menu box and the co-op screen. Every consequence there is a
		// lever, and the guest's handlers for these buttons are stubs or answered by levers; Start
		// stays in the filter only so its press can be posted as `Start:` below.
		bool wholePad = pauseMasked || queryMasked || boxMasked || listMasked || coopMasked;
		bool menuMask = goldenMenu || masked || wholePad;
		System.Func<uint, uint> filter =
			goldenMenu ? (Refii.Mods.ModScreen.Owned ? static held => held & Start : static _ => 0u)
			: wholePad ? static held => held & Start
			: masked ? WithoutMenuInput
			: cardMasked ? WithoutCardInput
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

		// IN PLAY: RB, the WHEEL AND THE BLADE (S8). RB's only bindings are the wheel's open and close
		// (GameActions 40 RB press / 41 RB release, 73) — Godot holds the wheel on it as on Back
		// (PublishHolds) and the guest's own open (`sub_824040B8` case 40, `OpenExpressionWheel`
		// 0x824041E8 / 0x823B5428) no longer runs beside it. While the guest states the expression
		// wheel open for this pad's player, its A (accept), B (back), D-pad and left stick (cursor)
		// are Godot's `QuickWheel:` levers (the guest's own route: `sub_82405190` 36 / 37,
		// SetCursorFromStick 0x82403EB0); X, Y, the triggers and the right stick stay the guest's, as
		// combat stays live under the wheel. While player one's spells menu stands, its D-pad, left
		// stick and A are Godot's `SpellBlade:` levers (`lhGuiWillAssignWidget_OnScreenAction` from
		// `sub_82405190`); the right trigger stays the guest's: its press opens the menu (42), its
		// hold sucks orbs (55), its release closes it (43, 56), one trigger for all four.
		bool playStick = false;
		int handPlayer = ModPlayer(pad);
		if (HostBridge.IsInGame && handPlayer != 0)
		{
			wheelBits |= RightShoulder;
			if (!CoopSession.IsClient && Fable2Bridge.GetExpressionWheelPlayer() == handPlayer)
			{
				wheelBits |= ButtonA | ButtonB | DpadUp | DpadDown | DpadLeft | DpadRight;
				playStick = true;
			}

			if (!CoopSession.IsClient && pad == 0 && SpellsMenuPlayer == 1)
			{
				wheelBits |= ButtonA | DpadUp | DpadDown | DpadLeft | DpadRight;
				playStick = true;
			}
		}

		int leftX = state.LeftX;

		// START IS A LEVER IN GAME: the guest's only consumer is the GUI input's raw pass, which
		// hands it to `sub_82371B10(G, player, 0)` (`CGUIInput_vf6`, 0x82403D50) — open the book,
		// close it from any pause screen, or the lite pause box. Godot posts `Start:<player>` on the
		// press and the guest never sees the button. A pad with no player keeps its Start (the
		// local co-op drop-in), and so does the front end.
		int startPlayer = HostBridge.IsInGame && !masked ? ModPlayer(pad) : 0;
		if (startPlayer != 0)
		{
			bool startDown = (filter(buttons) & ~wheelBits & ~_heldAcrossUnmask[pad] & Start) != 0;
			if (startDown && !_startWasDown[pad])
			{
				string lever = Fable2PortedScreens.Start(Fable2ActingPlayer.OfPad(CoopSession.IsClient, startPlayer));
				HostBridge.PostFrontEndAction(lever);
				GD.Print($"[input] pad {pad} Start posted '{lever}'");
			}

			_startWasDown[pad] = startDown;
			wheelBits |= Start;
		}
		else
		{
			_startWasDown[pad] = (buttons & Start) != 0;
		}

		// THE MAIN MENU'S B IS A LEVER where the guest would have taken it: under the menu mask
		// with no host panel answering B (FrontEndSwallowsBack), the root's 37 (`sub_826DEE40`) —
		// back to the title from the list (mode 7, 0x826dfbb0 / disc 0x826c8004), the load fan's
		// close in mode 5 (0x826dfb7c / 0x826c7fd0) — is posted as `FrontEndBack:<pad>`, and B never
		// reaches the guest here.
		if (masked)
		{
			bool bDown = (buttons & ~_heldAcrossUnmask[pad] & ButtonB) != 0;
			if (bDown && !_menuBWasDown[pad] && !FrontEndSwallowsBack)
			{
				string lever = Fable2FrontEndLevers.Back(pad);
				HostBridge.PostFrontEndAction(lever);
				GD.Print($"[input] pad {pad} B on the main menu posted '{lever}'");
			}

			_menuBWasDown[pad] = bDown;
		}
		else
		{
			_menuBWasDown[pad] = (buttons & ButtonB) != 0;
		}

		if (TitleTakesA)
		{
			bool aDown = (filter(buttons) & ~wheelBits & ~_heldAcrossUnmask[pad] & ButtonA) != 0;
			if (aDown && !_titleAWasDown[pad])
			{
				bool raised = HostBridge.TryGetFrontEndDialog(out bool accepting, out bool byTitle) && byTitle;
				if (Fable2FrontEndLevers.TitleA(pad, raised, accepting) is { } lever)
				{
					HostBridge.PostFrontEndAction(lever);
					GD.Print($"[input] pad {pad} A on the title posted '{lever}'");
				}
			}

			_titleAWasDown[pad] = aDown;
			wheelBits |= ButtonA;
		}
		else
		{
			_titleAWasDown[pad] = (buttons & ButtonA) != 0;
		}

		uint maskBits = ~filter(uint.MaxValue) | wheelBits;
		_heldAcrossUnmask[pad] |= _maskBits[pad] & ~maskBits & buttons;
		_heldAcrossUnmask[pad] &= buttons;
		_maskBits[pad] = maskBits;

		// The triggers likewise: the guest makes LT / RT presses (buttons 15 / 16) from their analog
		// values against the 30/255 threshold, so a trigger held while a surface masks it stays hidden
		// after the mask lifts until it is let go.
		bool triggersMasked = goldenMenu || wholePad;
		_leftTriggerAcross[pad] = state.LeftTrigger > 30 && (triggersMasked || _leftTriggerAcross[pad]);
		_rightTriggerAcross[pad] = state.RightTrigger > 30 && (triggersMasked || _rightTriggerAcross[pad]);

		// The shell publishes it (GameInput.Deliver): the transport is the shell's, the mask the title's.
		return new Published(new GameControllerPacket(
			pad, connected,
			filter(buttons) & ~wheelBits & ~_heldAcrossUnmask[pad],
			_leftTriggerAcross[pad] ? (byte)0 : state.LeftTrigger, _rightTriggerAcross[pad] ? (byte)0 : state.RightTrigger,
			// The stick is masked with the buttons: `sub_826E39C0` dead-zones the left stick
			// against ±0.75 and emits the same codes the D-pad does — 28/29 for Y at
			// 0x826E3C04/0x826E3C58, 30/31 for X at 0x826E3B50/0x826E3BAC.
			menuMask || playStick ? 0 : leftX, menuMask || playStick ? 0 : state.LeftY,
			// The card takes the right stick — it turns the model on the front and scrolls the
			// description on the back — so the camera does not swing under it. The left stick
			// stays live: the card does not pause the game.
			//
			// Invert X is ours: the guest has a vertical invert (CameraLookInverted, record33+0x2A,
			// negated at 0x825ADD28) and nothing for the horizontal. Applied only while the stick
			// is the camera's — never under a menu, which reads it as a direction.
			goldenMenu || cardMasked || wholePad ? 0 : (InvertCameraX && !menuMask ? -state.RightX : state.RightX),
			goldenMenu || cardMasked || wholePad ? 0 : state.RightY),
			maskBits,
			filter(state.PhysicalButtons) & ~wheelBits & ~_heldAcrossUnmask[pad],
			filter(state.OtherButtons) & ~wheelBits & ~_heldAcrossUnmask[pad]);
	}

	/// <summary>
	/// Whether the guest's own front-end menu should be shielded from this frame's input.
	/// True while the guest's active screen is MainMenu in a substate the host draws over; the
	/// title's A is its own lever (<see cref="TitleTakesA"/>). `screen+24` is 0 for the menu list; 1
	/// is the guest's own input lock, 2/3/4 the gender card, 5 the load-game carousel, 6 the
	/// warning, 7 leaving for the title. 1 and 6 are masked while the host's warning box stands.
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

	/// <summary>
	/// The card's buttons: A (done), Y (flip), X (equip, code 75) are posted as the card's own levers,
	/// and its slot takes nothing from the pad (ported, S7 B6), so B, the bumpers and the D-pad go too.
	/// The left stick and the triggers stay live: the card does not pause the game.
	/// </summary>
	private static uint WithoutCardInput(uint buttons) =>
		buttons & ~(ButtonA | ButtonB | ButtonX | ButtonY | LeftShoulder | RightShoulder |
			DpadUp | DpadDown | DpadLeft | DpadRight);

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
	/// Set by <c>GameUi</c> while the co-op screen stands: each player's pad is Godot's there and
	/// its presses go to the screen's own input slot as `CoopAction:` (<see cref="CoopPresses"/>);
	/// it stops counting as play — the cursor is free for its buttons and rows, and B is back, not
	/// magic.
	/// </summary>
	public static bool CoopScreenOwnsInput { get; set; }

	/// <summary>Per pad: the co-op screen's press edges, and whether the pad was the screen's last frame.</summary>
	private static readonly Fable2CoopScreenPresses.PadEdges[] _coopEdges = new Fable2CoopScreenPresses.PadEdges[MaxControllers];

	private static readonly bool[] _coopEngaged = new bool[MaxControllers];

	/// <summary>
	/// THE CO-OP SCREEN'S PRESSES. Both pads reach screen 24 on the console (the per-player filter
	/// exempts it, TU1 `sub_824047E8` table 0x82404898 / disc 0x823B5AD8), each for its own player
	/// (vf4: player one's GUI input 1, 2 on an online joiner; player two's 2). Here each player's
	/// press edges are posted as `CoopAction:&lt;code&gt;:&lt;player&gt;` to the screen's own slot and the pad
	/// is masked; <see cref="Fable2CoopScreenPresses.Forwarded"/> keeps what Godot answers itself.
	/// A pad newly the screen's hands no press: what is down then is taken as held.
	/// </summary>
	private static void CoopPresses(int pad, int player, uint buttons, int leftX, int leftY)
	{
		if (player == 0)
		{
			_coopEngaged[pad] = false;
			return;
		}

		System.Collections.Generic.List<int> codes = _coopEdges[pad].Feed(
			(buttons & DpadUp) != 0, (buttons & DpadDown) != 0, (buttons & DpadLeft) != 0,
			(buttons & DpadRight) != 0, (buttons & ButtonA) != 0, (buttons & ButtonB) != 0,
			(buttons & ButtonX) != 0, (buttons & ButtonY) != 0, leftX, leftY);
		if (!_coopEngaged[pad])
		{
			_coopEngaged[pad] = true;
			return;
		}

		Engine_CoopSavePicker.Press withheld = pad == CoopPickerPad ? CoopPickerWithheld : Engine_CoopSavePicker.Press.None;
		// Local co-op with player two not signed in: player two's X is also the guest's sign-in ask.
		bool askSignIn = player == 2 && codes.Contains(Fable2CoopScreenPresses.ButtonX) &&
			Fable2Bridge.GetCoopState() is { Known: true, Session: 2, RightSignedIn: false };
		foreach (int code in codes)
		{
			if (Fable2CoopScreenPresses.Forwarded(code, player, withheld, askSignIn))
			{
				string lever = Fable2CoopScreenPresses.Lever(code, player);
				HostBridge.PostFrontEndAction(lever);
				GD.Print($"[input] pad {pad} on the co-op screen posted '{lever}'");
			}
		}
	}

	private static bool MaskScreenMenuInput =>
		ScreenMenuOwnsInput && HostBridge.GetPauseState() is { ModalMode: 1, ScreenId: not 0 };

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
	/// The buttons a standing list's ROW ACTIONS bind this frame (GameUi.DriveRowActionsOn),
	/// withheld from the guest where the rest of the pad is not (the front end's menu: every
	/// in-game list holds the whole pad). Re-derived every frame: reset before the drives, set by
	/// the one that stands a list with row actions.
	/// </summary>
	public static uint RowActionHolds { get; set; }

	public const uint PadButtonX = ButtonX;

	public const uint PadButtonY = ButtonY;

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
	/// same press must not run. The warning: the host's box posts `FrontEndDialog:&lt;pad&gt;:36|37`
	/// (GameUi.DriveGuestDialog) in every mode it stands over, so the owner's slot (36 at
	/// 0x826df9c0 / disc 0x826c7e14, 37 under `+206` at 0x826dfc90 / 0x826c80e4) is answered once.
	/// Root: the host's row list. Carousel: the host's load fan posts
	/// `LoadSave:N` by its own cursor, while `sub_826DEE40` would commit the guest's cursor
	/// `+72` on A (0x826df880) and move it on 30/31 (0x826df170, 0x826df2bc). Card: the host's
	/// card posts "New Game" / "New Game Female" itself, so only while it stands. B on the
	/// carousel is the guest's own exit, posted as `FrontEndBack:` (`+24 = 0` at 0x826dfb7c), and
	/// the host's fan follows that substate.
	/// </summary>
	private static bool HostDrawsOver(uint state) =>
		state == MenuRootState ||
		state == LoadCarouselState ||
		(state >= CardFirstState && state <= CardLastState && FrontEndDrive.CardUp) ||
		FrontEndDrive.WarningUp;

	/// <summary>
	/// The buttons with the front end's navigation taken out. Godot owns the main menu and
	/// dispatches by name, but the guest never stopped running its own: `sub_826DEE40` still
	/// forwards A and B to `g_Menu` through `OnPressA`/`OnPressB` while `screen+24` is 0, and
	/// the two highlights are different objects. B is the guest's own path to the title at the
	/// root and its exit from the carousel: <see cref="SetMasked"/> posts it as `FrontEndBack:`
	/// unless a host panel answers B itself (<see cref="FrontEndSwallowsBack"/>).
	/// </summary>
	private static uint WithoutMenuInput(uint buttons) =>
		buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA | ButtonB | RowActionHolds);

	/// <summary>
	/// The same frame's input, in the terms a host-owned menu needs. Reuses the buttons already
	/// gathered for the guest rather than polling the devices a second time, so the two readers
	/// cannot drift. The stick folds into the direction on the shipped threshold —
	/// `THUMBSTICK_RELEASE_THRESHOLD` 0.65, from `guistateandconstants.lua`.
	/// </summary>
	private static void PublishMenu(int pad, uint buttons, int leftX, int leftY, int rightX, int rightY,
		uint leftTrigger, uint rightTrigger)
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
			_pageButtons.Hold((buttons & LeftShoulder) != 0, (buttons & RightShoulder) != 0, leftTrigger, rightTrigger);
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
		(MenuPageNextPressed, MenuPagePrevPressed) = _pageButtons.Feed(lb, rb, leftTrigger, rightTrigger);

		// The platform keyboard reads the pad itself while it is open: the menus get no press, and
		// a button still down when it closes is held, not a fresh edge (the was-down state below).
		if (Engine_UiPlatformKeyboard.IsOpen)
		{
			MenuAcceptPressed = MenuAcceptReleased = MenuBackPressed = MenuFlipPressed = false;
			MenuTabPrevPressed = MenuTabNextPressed = MenuEquipPressed = false;
			MenuPageNextPressed = MenuPagePrevPressed = false;
			MenuHorizontal = 0;
			MenuDirection = 0;
		}
		_acceptWasDown = a;
		_backWasDown = b;
		_flipWasDown = y;
		_tabPrevWasDown = lb;
		_tabNextWasDown = rb;
		_equipWasDown = x;
	}

	/// <summary>
	/// The pad of the player whose co-op save picker is up — local player two's, or pad 0 on an
	/// online joiner — while it is up, else −1. Set by <c>GameUi</c> every frame.
	/// </summary>
	public static int CoopPickerPad
	{
		get => _coopPickerPad;
		set
		{
			if (value != _coopPickerPad)
			{
				_coopPickerSource = -1;
			}

			_coopPickerPad = value;
		}
	}

	private static int _coopPickerPad = -1;

	/// <summary>Player two's presses the guest must not see on its picker this frame. Set by <c>GameUi</c>.</summary>
	public static Engine_CoopSavePicker.Press CoopPickerWithheld { get; set; }

	private static Engine_CoopSavePicker.PadEdges _coopPickerEdges;
	private static Engine_CoopSavePicker.Press _coopPickerPresses;
	private static int _coopPickerSource = -1;

	/// <summary>The picker's presses since the last take, and clears them.</summary>
	public static Engine_CoopSavePicker.Press TakeCoopPickerPresses()
	{
		Engine_CoopSavePicker.Press presses = _coopPickerPresses;
		_coopPickerPresses = Engine_CoopSavePicker.Press.None;
		return presses;
	}

	/// <summary>
	/// Edge-detects the picker's presses off its player's pad. A pad newly taken hands no press:
	/// whatever is down then is taken as held.
	/// </summary>
	private static void PublishCoopPicker(int pad, PadState seen)
	{
		uint buttons = seen.Buttons;
		Engine_CoopSavePicker.Press edges = _coopPickerEdges.Feed(
			(buttons & DpadLeft) != 0, (buttons & DpadRight) != 0, (buttons & ButtonA) != 0,
			(buttons & LeftShoulder) != 0, (buttons & RightShoulder) != 0, seen.LeftTrigger, seen.RightTrigger,
			seen.LeftX);
		if (pad != _coopPickerSource)
		{
			_coopPickerSource = pad;
			_coopPickerPresses = Engine_CoopSavePicker.Press.None;
			return;
		}

		_coopPickerPresses |= edges;
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

		// RB, the shipped binding (GameActions 40 / 41, masked from the guest in play, SetMasked): its
		// press and release are posted as themselves (`QuickWheel:rb:press|release`), and the pump runs
		// them as the guest's dispatcher does, its toggle option deciding hold or toggle. A press counts
		// from the world; its release follows whatever its press did.
		bool rb = (buttons & RightShoulder) != 0;
		RbWheelPressed = rb && !_rbWheel && Instance.WorldHasInput && !Refii.Mods.ModWheel.Open;
		RbWheelReleased = !rb && _rbWheelDown;
		_rbWheelDown = rb && (_rbWheelDown || RbWheelPressed);
		_rbWheel = rb;

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

		bool rbTwo = (buttons & RightShoulder) != 0;
		RbWheelTwoPressed = rbTwo && !_rbWheelTwo && Instance.WorldHasInput && !Refii.Mods.ModWheel.IsOpen(2);
		RbWheelTwoReleased = !rbTwo && _rbWheelTwoDown;
		_rbWheelTwoDown = rbTwo && (_rbWheelTwoDown || RbWheelTwoPressed);
		_rbWheelTwo = rbTwo;

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

	/// <summary>
	/// True on the frame RB or RT (LB or LT) went down — the save fans' next (previous) page. The
	/// keyboard reaches them through the bumpers' rebindable actions (<c>tab_next</c> PageDown / ],
	/// <c>tab_prev</c> PageUp / [); the guest's main menu acts on none of the four in its fan
	/// (`sub_826DEE40` handles codes 26..38 only).
	/// </summary>
	public static bool MenuPageNextPressed { get; private set; }

	public static bool MenuPagePrevPressed { get; private set; }

	private static Engine_FrontEndSavePages.PageButtons _pageButtons;

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

	/// <summary>Pad 0's RB last frame, and whether its press was posted to the wheel (PublishHolds).</summary>
	private static bool _rbWheel;

	private static bool _rbWheelDown;

	/// <summary>Local player two's RB last frame, and whether its press was posted (PublishWheelTwo).</summary>
	private static bool _rbWheelTwo;

	private static bool _rbWheelTwoDown;

	/// <summary>Pad 0's RB went down in the world: GameAction 40 (<c>QuickWheel:rb:press</c>).</summary>
	public static bool RbWheelPressed { get; private set; }

	/// <summary>Pad 0's RB came up after a posted press: GameAction 41 (<c>QuickWheel:rb:release</c>).</summary>
	public static bool RbWheelReleased { get; private set; }

	/// <summary>Local player two's RB went down in the world (<c>QuickWheel:2:rb:press</c>).</summary>
	public static bool RbWheelTwoPressed { get; private set; }

	/// <summary>Local player two's RB came up after a posted press (<c>QuickWheel:2:rb:release</c>).</summary>
	public static bool RbWheelTwoReleased { get; private set; }

	/// <summary>
	/// The spells menu's hold — GameAction 42 on press, 43 on release, on the RIGHT TRIGGER
	/// (`XboxButton` 16). Unlike the wheel above this is the SHIPPED binding, not a host one:
	/// `GameControlsCombatOnRelease` aliases a scheme that overrides `GameActionGUISpellsMenu`
	/// onto 16 over a base of 10. The guest opens and closes the menu on it itself.
	/// </summary>
	public static bool SpellsMenuHeld { get; private set; }

	/// <summary>
	/// Whose spells menu the guest states open: 1 player one (`QM+36`), 2 player two (`QM+37`), 0
	/// none (Engine_UiSpellAssignSet.WheelPlayer). Set by <c>GameUi</c> every frame.
	/// </summary>
	public static int SpellsMenuPlayer { get; set; }
}
