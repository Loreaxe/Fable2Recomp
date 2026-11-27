namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// THE HUD'S PAD IS GODOT'S: the guest's own pad no longer reaches the emotion choices, the HUD d-pad,
/// the world icons' hold, or the interaction mode while it is the NPC's or the dog's (their channel
/// handlers answer the dispatcher 0 without running), so each button going down or coming up on a pad
/// as the guest is published it becomes the lever of the surface the guest's dispatcher would have
/// handed it to, for the player of that pad (<see cref="Fable3ActingPlayer"/>), named by the game's
/// own action codes (<see cref="Fable3GameActions"/>):
/// <list type="bullet">
/// <item>A, X, Y, RB, LB while a choice record is stated: the emotion choice 1..5 the shipped scheme
/// binds that button to — its pressed code going down, its released code coming up
/// (emotion_choice_answer.cpp). The guest's handler finds the record and applies its own owner test.</item>
/// <item>While the interaction mode's pad is Godot's (`@Interaction.Input`): every face button, bumper
/// and trigger as its GUI code — pressed going down, held about once a guest step while it stays
/// down, released coming up (interaction_mode_input.cpp): B leaves, LT is follow, RT held keeps the
/// weapons pane, A / X / Y drive the script's mini-game bar. A choice's released code goes there too,
/// after the choices got it: the mini-game controller watches for it.</item>
/// <item>A otherwise in play: the world icon's hold, down and up (world_icon_hold.cpp); the guest's
/// handler keeps every gate (icons may draw, the hero still, player one's controller) and its tick the
/// fill and the use.</item>
/// <item>The d-pad: its direction's code down, its released code up (hud_dpad_use.cpp); the widget's
/// own player test and its tick's use one frame later are the guest's.</item>
/// </list>
/// The order is the dispatcher's: the TopBox (channel 4) takes player one's A and Y while its message
/// shows them (<see cref="Fable3Boxes"/> hands those), so they are no GUI code behind it then; the
/// choices are a code of their own and get the press either way; the interaction mode (channel 8) is
/// ahead of the world icons (channel 14), so A is the mode's while its pad is Godot's. A button handed
/// down is handed up to the same surfaces whatever changed meanwhile. The pad read is the one the
/// title publishes to the guest, so a surface of ours that holds the pad neutral releases everything.
/// Pure — no Godot type — so the tests drive it; <see cref="Fable3Input"/> feeds it each published pad.
/// <para>ONE GATE: nothing goes to the world while anything else has the input. The guest's own router
/// hands the dispatcher nothing while a box or a GUI screen has the pad (sub_826019F0 / disc sub_825D6A08
/// answers 1 there and its caller skips the dispatcher: `bne` TU3 0x82601148 / disc 0x825D6164, ahead of
/// the call at 0x82601178 / 0x825D6194), and the UI mode switches the game's control contexts as it is
/// pushed (game_state_hooks.cpp). So the choices, the hold and the d-pad are handed only while the world
/// has the input (<see cref="Surfaces.Play"/>: <see cref="Fable3Input.WorldHasInput"/>, which holds the
/// guest's words — the UI mode, the GUI's pause, a box's hold), and the interaction mode's pad only while
/// no guest surface has it (<see cref="Surfaces.GuestHasPad"/>). A press refused there gets no release
/// either; a press handed before still gets its release where it went. The TopBox is no gate for the
/// choices: its input answers only its own codes 74 / 82 / 79 and 77 / 85 / 81 (`cmpwi` 0x4A / 0x52 / 0x4F,
/// 0x4D / 0x55 / 0x51: TU3 sub_824D0A18 0x824D0A70..0x824D0AB8, disc sub_824B5C28 0x824B5C80..0x824B5CC8), so a choice's code passes it to channel 5.</para>
/// </summary>
public sealed class Fable3HudInput
{
	/// <summary>What is up, as the guest stated it.</summary>
	/// <param name="ChoicesUp">An emotion choice record is stated (<see cref="Fable3WorldIcons.ChoicesUp"/>).</param>
	/// <param name="TopBoxShowsA">The TopBox message up shows A.</param>
	/// <param name="TopBoxShowsY">The TopBox message up shows Y.</param>
	/// <param name="InteractionOwned">The interaction mode up is one whose pad is Godot's (<see cref="Fable3UiStatements.InteractionPad"/>).</param>
	/// <param name="Play">The world has the input (<see cref="Fable3Input.WorldHasInput"/>): no screen of the guest's or ours over it.</param>
	/// <param name="GuestHasPad">A guest surface has the pad (<see cref="Fable3PointerStatements.GuestHoldsThePad"/>: the UI mode, the GUI's pause, a box's hold).</param>
	/// <param name="SanctuaryList">The Sanctuary's mode stands with a list for THIS pad's player (<see cref="SanctuaryHolds"/>): that player's pad is the `guiworld:` levers' and their world gets none.</param>
	public readonly record struct Surfaces(
		bool ChoicesUp, bool TopBoxShowsA, bool TopBoxShowsY = false, bool InteractionOwned = false, bool Play = true,
		bool GuestHasPad = false, bool SanctuaryList = false)
	{
		/// <summary>
		/// The world's levers (the choices, the icons' hold, the d-pad) are handed: the world has the
		/// input and no Sanctuary list holds it. The Sanctuary's mode is channel 8, ahead of the icons'
		/// channel 14, and with a list it answers the pad itself (`guiworld:`, interaction_guiworld.cpp);
		/// with no list it answers A 0 and the dispatcher hands A on, so the icons' hold is handed then.
		/// </summary>
		public bool World => Play && !SanctuaryList;
	}

	/// <summary>
	/// Whether a Sanctuary list holds <paramref name="player"/>'s pad: the Sanctuary's mode stands with a
	/// list (<paramref name="listUp"/>, <see cref="Fable3GuiWorld.ListUp"/>) and it is that player's mode
	/// (<paramref name="listPlayer"/>, <see cref="Fable3GuiWorld.Player"/>, the player its table was filled
	/// for). Both players are principals: one player's Sanctuary list never takes the other's world levers.
	/// </summary>
	public static bool SanctuaryHolds(int player, bool listUp, int listPlayer) => listUp && listPlayer == player;

	/// <summary>
	/// Whether the world has <paramref name="player"/>'s input: every term that holds every player's pad
	/// passes (<paramref name="besidesLists"/>: the guest's UI mode, GUI pause and box hold, a map, the HUD
	/// root hidden, a surface of ours), and no list has the stick (<paramref name="listHasPad"/>,
	/// <see cref="Fable3InteractionList.HasPad"/>) — unless the only list up is a Sanctuary list
	/// (<paramref name="sanctuaryListUp"/> and no interaction-mode list, <paramref name="interactionListUp"/>)
	/// standing for the other player (<paramref name="sanctuaryPlayer"/>): that list holds its own player's
	/// pad only. The interaction mode's list carries no player in what the guest states, so it holds every
	/// pad as before.
	/// </summary>
	public static bool PlayOf(int player, bool besidesLists, bool listHasPad, bool sanctuaryListUp, bool interactionListUp,
		int sanctuaryPlayer) =>
		besidesLists && (!listHasPad || (sanctuaryListUp && !interactionListUp && sanctuaryPlayer != player));

	/// <summary>
	/// Whether the Sanctuary's pad is Godot's to post as `guiworld:` levers: its mode is up
	/// (<paramref name="modeUp"/>) and nothing of the guest's is ahead of it — no box-manager screen
	/// showing (<paramref name="boxesShowing"/>, the Options screen among them: the box router, channel 1,
	/// answers 72..85 while a box shows, which interaction_guiworld.cpp's BoxTakesAction drops natively),
	/// and no UI mode, GUI pause or box hold (<paramref name="guestHoldsThePad"/>). Those hold EVERY player's
	/// pad in the guest, so the gate is the same for both: the box router answers by the action code and
	/// the box type alone, no pad test (TU3 `lwz r10,0(r30)` / `cmpwi cr6,r10,0x1D` … `cmpwi cr6,r10,0x55`
	/// 0x825F39EC..0x825F3A40, the intermission's type 5 skipped at `cmpwi cr6,r3,5` 0x825F39E4; disc
	/// 0x825C8C60..0x825C8C8C), and the UI mode and the GUI's pause sit on the presented game's one stack.
	/// </summary>
	public static bool SanctuaryTakesPad(bool modeUp, int boxesShowing, bool guestHoldsThePad) =>
		modeUp && boxesShowing == 0 && !guestHoldsThePad;

	/// <summary>The pad's bits the triggers take here (past the 16 button bits).</summary>
	public const uint LeftTriggerBit = 0x10000u, RightTriggerBit = 0x20000u;

	/// <summary>A trigger past this (of 255) is down, as the shell's own menus read it.</summary>
	public const uint TriggerThreshold = 30u;

	/// <summary>How often a held button's held code goes to the interaction mode: the guest's 30 Hz step.</summary>
	public const long HeldEveryMsec = 33;

	private const int ToEmotion = 1, ToWorldIcon = 2, ToDpad = 4, ToInteraction = 8;

	/// <summary>A button: its bit, its GUI codes, and the emotion choice the shipped scheme binds it to (0 none).</summary>
	private readonly record struct Button(uint Bit, int Pressed, int Held, int Released, int Choice);

	// The shipped scheme's choices: 1 A, 2 X, 3 Y, 4 RB, 5 LB (Fable3Input's table).
	private static readonly Button[] Buttons =
	{
		new(Refii.Game.GameInput.PadA, Fable3GameActions.GuiA, Fable3GameActions.GuiAHeld, Fable3GameActions.GuiAReleased, 1),
		new(Refii.Game.GameInput.PadB, Fable3GameActions.GuiB, Fable3GameActions.GuiBHeld, Fable3GameActions.GuiBReleased, 0),
		new(Refii.Game.GameInput.PadX, Fable3GameActions.GuiX, Fable3GameActions.GuiXHeld, Fable3GameActions.GuiXReleased, 2),
		new(Refii.Game.GameInput.PadY, Fable3GameActions.GuiY, Fable3GameActions.GuiYHeld, Fable3GameActions.GuiYReleased, 3),
		new(Refii.Game.GameInput.PadRightShoulder, Fable3GameActions.GuiRightBumper, Fable3GameActions.GuiRightBumperHeld,
			Fable3GameActions.GuiRightBumperReleased, 4),
		new(Refii.Game.GameInput.PadLeftShoulder, Fable3GameActions.GuiLeftBumper, Fable3GameActions.GuiLeftBumperHeld,
			Fable3GameActions.GuiLeftBumperReleased, 5),
		new(LeftTriggerBit, Fable3GameActions.GuiLeftTrigger, Fable3GameActions.GuiLeftTriggerHeld,
			Fable3GameActions.GuiLeftTriggerReleased, 0),
		new(RightTriggerBit, Fable3GameActions.GuiRightTrigger, Fable3GameActions.GuiRightTriggerHeld,
			Fable3GameActions.GuiRightTriggerReleased, 0),
	};

	private static readonly (uint Bit, int Pressed, int Released)[] Directions =
	{
		(Refii.Game.GameInput.PadUp, Fable3GameActions.GuiUp, Fable3GameActions.GuiUpReleased),
		(Refii.Game.GameInput.PadDown, Fable3GameActions.GuiDown, Fable3GameActions.GuiDownReleased),
		(Refii.Game.GameInput.PadLeft, Fable3GameActions.GuiLeft, Fable3GameActions.GuiLeftReleased),
		(Refii.Game.GameInput.PadRight, Fable3GameActions.GuiRight, Fable3GameActions.GuiRightReleased),
	};

	private readonly Engine_UiPadEdges[] _pads = { new(), new(), new(), new() };
	private readonly long[] _heldAt = new long[4];

	/// <summary>The published pad as one mask: its buttons and a bit per trigger past the threshold.</summary>
	public static uint Mask(uint buttons, uint leftTrigger, uint rightTrigger) =>
		(buttons & 0xFFFFu) | (leftTrigger > TriggerThreshold ? LeftTriggerBit : 0u) |
		(rightTrigger > TriggerThreshold ? RightTriggerBit : 0u);

	/// <summary>
	/// One published sample of guest pad <paramref name="pad"/> (0-based) at <paramref name="nowMsec"/>:
	/// appends the levers its edges and held buttons make to <paramref name="levers"/>, in the order they
	/// are to be posted.
	/// </summary>
	public void Step(int pad, uint mask, long nowMsec, in Surfaces surfaces, List<string> levers)
	{
		if (pad < 0 || pad >= _pads.Length)
		{
			return;
		}

		Engine_UiPadEdges edges = _pads[pad];
		(uint pressed, uint released) = edges.Step(mask);
		int player = Fable3ActingPlayer.OfPad(pad);
		bool repeat = nowMsec - _heldAt[pad] >= HeldEveryMsec;
		bool repeated = false;
		foreach (Button button in Buttons)
		{
			if ((pressed & button.Bit) != 0u)
			{
				int to = 0;
				if (surfaces.ChoicesUp && button.Choice != 0 && surfaces.World)
				{
					levers.Add(Fable3BoxAnswers.Emotion(Fable3GameActions.EmotionPressed(button.Choice), player));
					to |= ToEmotion;
				}

				bool topBox = pad == 0 && ((button.Bit == Refii.Game.GameInput.PadA && surfaces.TopBoxShowsA) ||
					(button.Bit == Refii.Game.GameInput.PadY && surfaces.TopBoxShowsY));
				if (!topBox && surfaces.InteractionOwned && !surfaces.GuestHasPad)
				{
					levers.Add(Fable3BoxAnswers.Interaction(button.Pressed, player));
					to |= ToInteraction;
					_heldAt[pad] = nowMsec;
				}
				else if (!topBox && button.Bit == Refii.Game.GameInput.PadA && surfaces.World)
				{
					levers.Add(Fable3BoxAnswers.WorldIconHold(true, player));
					to |= ToWorldIcon;
				}

				edges.Own(button.Bit, to);
			}
			else if ((released & button.Bit) != 0u)
			{
				int to = edges.Release(button.Bit);
				if ((to & ToEmotion) != 0)
				{
					int code = Fable3GameActions.EmotionReleased(button.Choice);
					levers.Add(Fable3BoxAnswers.Emotion(code, player));
					if (surfaces.InteractionOwned && !surfaces.GuestHasPad)
					{
						levers.Add(Fable3BoxAnswers.Interaction(code, player));
					}
				}

				if ((to & ToInteraction) != 0)
				{
					levers.Add(Fable3BoxAnswers.Interaction(button.Released, player));
				}

				if ((to & ToWorldIcon) != 0)
				{
					levers.Add(Fable3BoxAnswers.WorldIconHold(false, player));
				}
			}
			else if (repeat && surfaces.InteractionOwned && !surfaces.GuestHasPad && (mask & button.Bit) != 0u &&
				(edges.Owner(button.Bit) & ToInteraction) != 0)
			{
				levers.Add(Fable3BoxAnswers.Interaction(button.Held, player));
				repeated = true;
			}
		}

		if (repeated)
		{
			_heldAt[pad] = nowMsec;
		}

		foreach ((uint bit, int down, int up) in Directions)
		{
			if ((pressed & bit) != 0u && surfaces.World)
			{
				levers.Add(Fable3BoxAnswers.Dpad(down, player));
				edges.Own(bit, ToDpad);
			}
			else if ((released & bit) != 0u && (edges.Release(bit) & ToDpad) != 0)
			{
				levers.Add(Fable3BoxAnswers.Dpad(up, player));
			}
		}
	}
}
