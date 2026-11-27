namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// The HUD's pad as levers (Fable3HudInput): each button edge on a published pad becomes the lever of
/// the surface the guest's dispatcher would have handed it to, for that pad's player, with the game's
/// own action codes; a button handed down is handed up to the same surfaces.
/// </summary>
[TestSuite]
public class Fable3HudInputTests
{
	private const uint A = 0x1000u, B = 0x2000u, X = 0x4000u, Y = 0x8000u, LB = 0x0100u, RB = 0x0200u;
	private const uint Up = 0x0001u, Down = 0x0002u, Left = 0x0004u, Right = 0x0008u;

	private static readonly Fable3HudInput.Surfaces Play = new(ChoicesUp: false, TopBoxShowsA: false);
	private static readonly Fable3HudInput.Surfaces Choices = new(ChoicesUp: true, TopBoxShowsA: false);

	private static readonly Fable3HudInput.Surfaces Npc = new(ChoicesUp: true, TopBoxShowsA: false, InteractionOwned: true);
	private const uint LT = Fable3HudInput.LeftTriggerBit, RT = Fable3HudInput.RightTriggerBit;

	private static List<string> Step(Fable3HudInput input, int pad, uint mask, Fable3HudInput.Surfaces surfaces, long now = 0)
	{
		var levers = new List<string>();
		input.Step(pad, mask, now, surfaces, levers);
		return levers;
	}

	[TestCase]
	public void AInPlayIsTheWorldIconsHoldDownAndUp()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 0, A, Play)).ContainsExactly("worldicon:1:hold:down");
		AssertThat(Step(input, 0, A, Play)).IsEmpty();
		AssertThat(Step(input, 0, 0u, Play)).ContainsExactly("worldicon:1:hold:up");
		AssertThat(Step(input, 0, B | X | Y, Play)).IsEmpty();
	}

	[TestCase]
	public void TheChoiceButtonsSendTheGamesPressedAndReleasedCodes()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 0, X, Choices)).ContainsExactly("emotion:1:104");
		AssertThat(Step(input, 0, X | Y, Choices)).ContainsExactly("emotion:1:105");
		AssertThat(Step(input, 0, Y, Choices)).ContainsExactly("emotion:1:109");
		AssertThat(Step(input, 0, Y | RB | LB, Choices)).ContainsExactly("emotion:1:106", "emotion:1:107");
		AssertThat(Step(input, 0, 0u, Choices)).ContainsExactly("emotion:1:110", "emotion:1:111", "emotion:1:112");
	}

	[TestCase]
	public void AWithChoicesUpIsBothTheFirstChoiceAndTheHold()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 0, A, Choices)).ContainsExactly("emotion:1:103", "worldicon:1:hold:down");
		AssertThat(Step(input, 0, 0u, Choices)).ContainsExactly("emotion:1:108", "worldicon:1:hold:up");
	}

	[TestCase]
	public void AReleaseGoesWhereItsPressWentWhateverChangedMeanwhile()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 0, A, Choices)).ContainsExactly("emotion:1:103", "worldicon:1:hold:down");
		// The choice was committed and its records are gone: the release still follows the press.
		AssertThat(Step(input, 0, 0u, Play)).ContainsExactly("emotion:1:108", "worldicon:1:hold:up");
		// A press made with no choices up has no choice release when they appear under it.
		AssertThat(Step(input, 0, X, Play)).IsEmpty();
		AssertThat(Step(input, 0, 0u, Choices)).IsEmpty();
	}

	[TestCase]
	public void TheTopBoxTakesPlayerOnesAAheadOfTheHoldButNotTheChoice()
	{
		var input = new Fable3HudInput();
		var top = new Fable3HudInput.Surfaces(ChoicesUp: false, TopBoxShowsA: true);
		AssertThat(Step(input, 0, A, top)).IsEmpty();
		AssertThat(Step(input, 0, 0u, top)).IsEmpty();
		var both = new Fable3HudInput.Surfaces(ChoicesUp: true, TopBoxShowsA: true);
		AssertThat(Step(input, 0, A, both)).ContainsExactly("emotion:1:103");
		AssertThat(Step(input, 0, 0u, both)).ContainsExactly("emotion:1:108");
		// The TopBox takes player one's pad alone: player two's A is still a hold.
		AssertThat(Step(input, 1, A, top)).ContainsExactly("worldicon:2:hold:down");
	}

	[TestCase]
	public void TheDpadSendsItsDirectionDownAndItsReleasedCodeUp()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 0, Up, Play)).ContainsExactly("dpad:1:29");
		AssertThat(Step(input, 0, Up | Right, Play)).ContainsExactly("dpad:1:32");
		AssertThat(Step(input, 0, Right, Play)).ContainsExactly("dpad:1:33");
		AssertThat(Step(input, 0, Down | Left, Play)).ContainsExactly("dpad:1:30", "dpad:1:31", "dpad:1:36");
		AssertThat(Step(input, 0, 0u, Play)).ContainsExactly("dpad:1:34", "dpad:1:35");
	}

	[TestCase]
	public void EachPadActsForItsOwnPlayer()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 1, X | Down, Choices)).ContainsExactly("emotion:2:104", "dpad:2:30");
		AssertThat(Step(input, 0, X, Choices)).ContainsExactly("emotion:1:104");
		AssertThat(Step(input, 1, 0u, Choices)).ContainsExactly("emotion:2:109", "dpad:2:34");
		AssertThat(Step(input, 7, A, Choices)).IsEmpty();
	}

	[TestCase]
	public void ANeutralPadReleasesEverythingHandedDown()
	{
		var input = new Fable3HudInput();
		Step(input, 0, A | Y | Left, Choices);
		// A surface of ours took the pad: the title publishes it neutral.
		AssertThat(Step(input, 0, 0u, Play)).ContainsExactly(
			"emotion:1:108", "worldicon:1:hold:up", "emotion:1:110", "dpad:1:35");
	}

	[TestCase]
	public void TheMaskTakesTheButtonsAndATriggerPastTheThreshold()
	{
		AssertThat(Fable3HudInput.Mask(A | Up, 0u, 0u)).IsEqual(A | Up);
		AssertThat(Fable3HudInput.Mask(0u, 30u, 31u)).IsEqual(Fable3HudInput.RightTriggerBit);
		AssertThat(Fable3HudInput.Mask(0xFFFF1000u, 255u, 0u)).IsEqual(A | Fable3HudInput.LeftTriggerBit);
	}

	[TestCase]
	public void InAnOwnedInteractionModeEveryButtonIsItsGuiCode()
	{
		var input = new Fable3HudInput();
		// B leaves, LT follows: pressed going down, released coming up.
		AssertThat(Step(input, 0, B, Npc)).ContainsExactly("interaction:1:75");
		AssertThat(Step(input, 0, LT, Npc)).ContainsExactly("interaction:1:80", "interaction:1:86");
		AssertThat(Step(input, 0, 0u, Npc)).ContainsExactly("interaction:1:94");
		// A choice button is the choice's code and the GUI code; the hold is not handed (channel 8 is ahead of 14).
		AssertThat(Step(input, 0, A, Npc)).ContainsExactly("emotion:1:103", "interaction:1:74");
		// Coming up: the choices get the released code, then the mode gets it too, then the GUI release.
		AssertThat(Step(input, 0, 0u, Npc)).ContainsExactly("emotion:1:108", "interaction:1:108", "interaction:1:79");
		AssertThat(Step(input, 1, RB, Npc)).ContainsExactly("emotion:2:106", "interaction:2:89");
	}

	[TestCase]
	public void AHeldButtonRepeatsItsHeldCodeAtTheGuestsStep()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 0, RT, Npc, now: 1000)).ContainsExactly("interaction:1:87");
		AssertThat(Step(input, 0, RT, Npc, now: 1016)).IsEmpty();
		AssertThat(Step(input, 0, RT, Npc, now: 1033)).ContainsExactly("interaction:1:91");
		AssertThat(Step(input, 0, RT, Npc, now: 1040)).IsEmpty();
		AssertThat(Step(input, 0, RT | Y, Npc, now: 1070)).ContainsExactly("emotion:1:105", "interaction:1:77", "interaction:1:91");
		AssertThat(Step(input, 0, RT | Y, Npc, now: 1110)).ContainsExactly("interaction:1:85", "interaction:1:91");
		AssertThat(Step(input, 0, 0u, Npc, now: 1111)).ContainsExactly(
			"emotion:1:110", "interaction:1:110", "interaction:1:81", "interaction:1:95");
	}

	[TestCase]
	public void TheTopBoxTakesPlayerOnesAAndYAheadOfTheMode()
	{
		var input = new Fable3HudInput();
		var top = new Fable3HudInput.Surfaces(ChoicesUp: false, TopBoxShowsA: true, TopBoxShowsY: true, InteractionOwned: true);
		AssertThat(Step(input, 0, A | Y | X, top)).ContainsExactly("interaction:1:76");
		AssertThat(Step(input, 0, 0u, top)).ContainsExactly("interaction:1:78");
	}

	[TestCase]
	public void AModeThatEndsUnderAHeldButtonStillGetsItsRelease()
	{
		var input = new Fable3HudInput();
		AssertThat(Step(input, 0, B, Npc)).ContainsExactly("interaction:1:75");
		// B closed the mode: no held code, but the release follows the press.
		AssertThat(Step(input, 0, B, Play, now: 500)).IsEmpty();
		AssertThat(Step(input, 0, 0u, Play, now: 600)).ContainsExactly("interaction:1:80");
		// The next A is the world icon's again.
		AssertThat(Step(input, 0, A, Play, now: 700)).ContainsExactly("worldicon:1:hold:down");
	}

	[TestCase]
	public void UnderAScreenNothingIsHandedToTheWorld()
	{
		// The save menu (the UI mode), the epilogue (the GUI's pause): one A on the screen is the screen's.
		var input = new Fable3HudInput();
		var screen = new Fable3HudInput.Surfaces(ChoicesUp: true, TopBoxShowsA: false, Play: false, GuestHasPad: true);
		AssertThat(Step(input, 0, A | Up, screen)).IsEmpty();
		AssertThat(Step(input, 0, 0u, screen)).IsEmpty();
		// A press handed in play is released even when a screen came up under it.
		AssertThat(Step(input, 0, A | Left, Choices)).ContainsExactly("emotion:1:103", "worldicon:1:hold:down", "dpad:1:31");
		AssertThat(Step(input, 0, 0u, screen)).ContainsExactly("emotion:1:108", "worldicon:1:hold:up", "dpad:1:35");
	}

	[TestCase]
	public void UnderAGuestSurfaceTheInteractionModeGetsNothing()
	{
		var input = new Fable3HudInput();
		var held = new Fable3HudInput.Surfaces(ChoicesUp: false, TopBoxShowsA: false, InteractionOwned: true, Play: false,
			GuestHasPad: true);
		AssertThat(Step(input, 0, B, held, now: 0)).IsEmpty();
		AssertThat(Step(input, 0, B, held, now: 100)).IsEmpty();
		AssertThat(Step(input, 0, 0u, held, now: 200)).IsEmpty();
		// The mode's pad with no surface over it: B leaves, as before.
		var mode = new Fable3HudInput.Surfaces(ChoicesUp: false, TopBoxShowsA: false, InteractionOwned: true);
		AssertThat(Step(input, 0, B, mode, now: 300)).ContainsExactly("interaction:1:75");
	}

	/// <summary>
	/// int7d N4: in the Sanctuary with a list up the mode answers the pad itself (`guiworld:`), ahead of the
	/// icons' channel, so A is not also the icons' hold, and neither the choices nor the d-pad are handed;
	/// with no list the mode answers A 0 and the dispatcher hands it on, so the hold stands.
	/// </summary>
	[TestCase]
	public void ASanctuaryListHoldsTheWorldsLevers()
	{
		var input = new Fable3HudInput();
		var list = new Fable3HudInput.Surfaces(ChoicesUp: true, TopBoxShowsA: false, SanctuaryList: true);
		AssertThat(Step(input, 0, A, list)).IsEmpty();
		AssertThat(Step(input, 0, 0u, list)).IsEmpty();
		AssertThat(Step(input, 0, Up, list)).IsEmpty();
		AssertThat(Step(input, 0, 0u, list)).IsEmpty();

		var hub = new Fable3HudInput.Surfaces(ChoicesUp: false, TopBoxShowsA: false, SanctuaryList: false);
		AssertThat(Step(input, 0, A, hub)).ContainsExactly("worldicon:1:hold:down");
		AssertThat(Step(input, 0, 0u, hub)).ContainsExactly("worldicon:1:hold:up");
	}

	/// <summary>
	/// int7d N4: the Sanctuary's pad is posted as `guiworld:` levers only while its mode is up and nothing of
	/// the guest's is ahead of it — no box-manager screen (the Options screen) and no UI mode, GUI pause or hold.
	/// </summary>
	[TestCase]
	public void TheSanctuarysPadIsGodotsOnlyWithNothingAheadOfIt()
	{
		AssertThat(Fable3HudInput.SanctuaryTakesPad(true, 0, false)).IsTrue();
		AssertThat(Fable3HudInput.SanctuaryTakesPad(true, 1, false)).IsFalse();
		AssertThat(Fable3HudInput.SanctuaryTakesPad(true, 0, true)).IsFalse();
		AssertThat(Fable3HudInput.SanctuaryTakesPad(false, 0, false)).IsFalse();
	}

	/// <summary>
	/// Both players are principals: player one's Sanctuary list holds player one's pad only — player two's
	/// A on their own pad is still their world icons' hold, their d-pad their own — and the other way round.
	/// </summary>
	[TestCase]
	public void ASanctuaryListHoldsOnlyItsOwnPlayersPad()
	{
		AssertThat(Fable3HudInput.SanctuaryHolds(1, listUp: true, listPlayer: 1)).IsTrue();
		AssertThat(Fable3HudInput.SanctuaryHolds(2, listUp: true, listPlayer: 1)).IsFalse();
		AssertThat(Fable3HudInput.SanctuaryHolds(2, listUp: true, listPlayer: 2)).IsTrue();
		AssertThat(Fable3HudInput.SanctuaryHolds(1, listUp: true, listPlayer: 2)).IsFalse();
		AssertThat(Fable3HudInput.SanctuaryHolds(1, listUp: false, listPlayer: 1)).IsFalse();

		// Player one in the Sanctuary's list, player two in the world: one input, two pads, as Fable3Input steps them.
		var input = new Fable3HudInput();
		Fable3HudInput.Surfaces For(int pad) => new(ChoicesUp: false, TopBoxShowsA: false,
			SanctuaryList: Fable3HudInput.SanctuaryHolds(Fable3ActingPlayer.OfPad(pad), true, 1));
		AssertThat(Step(input, 0, A, For(0))).IsEmpty();
		AssertThat(Step(input, 1, A, For(1))).ContainsExactly("worldicon:2:hold:down");
		AssertThat(Step(input, 1, Up | A, For(1))).ContainsExactly("dpad:2:29");
		AssertThat(Step(input, 0, 0u, For(0))).IsEmpty();
		AssertThat(Step(input, 1, 0u, For(1))).ContainsExactly("worldicon:2:hold:up", "dpad:2:33");

		// The world's input per player: player one's Sanctuary list (the only list) takes player one's world, not player two's.
		AssertThat(Fable3HudInput.PlayOf(1, true, listHasPad: true, sanctuaryListUp: true, interactionListUp: false, sanctuaryPlayer: 1)).IsFalse();
		AssertThat(Fable3HudInput.PlayOf(2, true, listHasPad: true, sanctuaryListUp: true, interactionListUp: false, sanctuaryPlayer: 1)).IsTrue();
		AssertThat(Fable3HudInput.PlayOf(1, true, listHasPad: true, sanctuaryListUp: true, interactionListUp: false, sanctuaryPlayer: 2)).IsTrue();
		// An interaction-mode list states no player: it holds both, as before; so do the guest-wide terms.
		AssertThat(Fable3HudInput.PlayOf(2, true, listHasPad: true, sanctuaryListUp: false, interactionListUp: true, sanctuaryPlayer: 1)).IsFalse();
		AssertThat(Fable3HudInput.PlayOf(2, false, listHasPad: false, sanctuaryListUp: false, interactionListUp: false, sanctuaryPlayer: 1)).IsFalse();
		AssertThat(Fable3HudInput.PlayOf(2, true, listHasPad: false, sanctuaryListUp: false, interactionListUp: false, sanctuaryPlayer: 1)).IsTrue();
	}
}
