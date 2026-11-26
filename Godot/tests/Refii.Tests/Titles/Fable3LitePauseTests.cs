namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Game;
using Refii.Titles.Fable3;

/// <summary>Fable 3's lite pause: when the host may stop stepping the simulation, and what ends it.</summary>
[TestSuite]
public class Fable3LitePauseTests
{
	/// <summary>In play, offline or on the couch (this machine plays both), with the world free: allowed.</summary>
	[TestCase]
	public void ItStartsInPlayWithTheWorldFree()
	{
		AssertThat(Fable3LitePause.Refusal(3u, inGame: true, worldHasInput: true, guestHolds: false, photoMode: false)).IsNull();
	}

	/// <summary>Either side of an online session is refused: the two machines must not drift apart.</summary>
	[TestCase]
	public void AnOnlineSessionIsNeverPaused()
	{
		AssertThat(Fable3LitePause.Refusal(1u, true, true, false, false)).IsNotNull();
		AssertThat(Fable3LitePause.Refusal(2u, true, true, false, false)).IsNotNull();
	}

	/// <summary>Refused outside a game, under a pause the guest states, under photo mode, and while a screen or box has the input.</summary>
	[TestCase]
	public void ItIsRefusedWhileSomethingElseHoldsTheGame()
	{
		AssertThat(Fable3LitePause.Refusal(3u, false, true, false, false)).IsNotNull();
		AssertThat(Fable3LitePause.Refusal(3u, true, true, true, false)).IsNotNull();
		AssertThat(Fable3LitePause.Refusal(3u, true, true, false, true)).IsNotNull();
		AssertThat(Fable3LitePause.Refusal(3u, true, false, false, false)).IsNotNull();
	}

	/// <summary>A, B or Start going down ends it; a held button or another button does not.</summary>
	[TestCase]
	public void APadPressEndsIt()
	{
		AssertThat(Fable3LitePause.PadEnds(GameInput.PadA, 0u)).IsTrue();
		AssertThat(Fable3LitePause.PadEnds(GameInput.PadB, 0u)).IsTrue();
		AssertThat(Fable3LitePause.PadEnds(0x0010u, 0u)).IsTrue();
		AssertThat(Fable3LitePause.PadEnds(GameInput.PadA, GameInput.PadA)).IsFalse();
		AssertThat(Fable3LitePause.PadEnds(GameInput.PadX, 0u)).IsFalse();
	}

	/// <summary>The key is P, read by the title itself: it presses nothing on the pad.</summary>
	[TestCase]
	public void TheKeyIsPAndPressesNoPadButton()
	{
		foreach (GameInput.KeyAction action in new Fable3Input().KeyActions)
		{
			if (action.Name == Fable3LitePause.Action)
			{
				AssertThat(action.Defaults).IsEqual("P");
				AssertThat(action.Press).IsEqual(GameInput.KeyPress.None);
				return;
			}
		}

		AssertThat(false).IsTrue();
	}
}
