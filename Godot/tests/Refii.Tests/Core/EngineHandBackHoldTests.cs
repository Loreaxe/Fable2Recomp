namespace Refii.Tests.Core;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// The hand-back mask (Engine_HandBackHold): a button down when the world gets the input back stays
/// hidden from the guest until it is released, on every pad.
/// </summary>
[TestSuite]
public class EngineHandBackHoldTests
{
	private const uint A = 0x1000u;
	private const uint B = 0x2000u;

	/// <summary>One publish of one pad, as a title's mask does it: follow, then what is left for the guest.</summary>
	private static uint Publish(Engine_HandBackHold hold, bool worldHasInput, int pad, uint down)
	{
		hold.Follow(worldHasInput);
		return down & ~hold.Hidden(pad, down);
	}

	[TestCase]
	public void TheButtonThatClosedAMenuIsNotThePressOfTheWorld()
	{
		var hold = new Engine_HandBackHold(4);
		AssertThat(Publish(hold, true, 0, 0u)).IsEqual(0u);

		// A menu has the input: the pad passes (the guest's own menu reads it).
		AssertThat(Publish(hold, false, 0, 0u)).IsEqual(0u);
		AssertThat(Publish(hold, false, 0, B)).IsEqual(B);

		// The menu closed on that B, still down: hidden until released.
		AssertThat(Publish(hold, true, 0, B)).IsEqual(0u);
		AssertThat(Publish(hold, true, 0, B)).IsEqual(0u);
		AssertThat(Publish(hold, true, 0, 0u)).IsEqual(0u);
		AssertThat(Publish(hold, true, 0, B)).IsEqual(B);
	}

	[TestCase]
	public void AButtonPressedAfterTheHandBackPassesBesideTheHeldOne()
	{
		var hold = new Engine_HandBackHold(4);
		Publish(hold, false, 0, A);
		AssertThat(Publish(hold, true, 0, A)).IsEqual(0u);
		AssertThat(Publish(hold, true, 0, A | B)).IsEqual(B);
		AssertThat(Publish(hold, true, 0, B)).IsEqual(B);
		AssertThat(Publish(hold, true, 0, A | B)).IsEqual(A | B);
	}

	[TestCase]
	public void EveryPadIsHeldByTheOneHandBack()
	{
		var hold = new Engine_HandBackHold(4);
		for (int pad = 0; pad < 4; ++pad)
		{
			Publish(hold, false, pad, A);
		}

		// The first pad published after the change sees the hand-back; the others are held by it too.
		AssertThat(Publish(hold, true, 0, A)).IsEqual(0u);
		AssertThat(Publish(hold, true, 1, A)).IsEqual(0u);
		AssertThat(Publish(hold, true, 2, 0u)).IsEqual(0u);
		AssertThat(Publish(hold, true, 2, A)).IsEqual(A);
		AssertThat(Publish(hold, true, 3, A)).IsEqual(0u);
	}

	[TestCase]
	public void TheWorldKeepingTheInputHoldsNothing()
	{
		var hold = new Engine_HandBackHold(4);
		Publish(hold, true, 0, 0u);
		AssertThat(Publish(hold, true, 0, A)).IsEqual(A);
		AssertThat(Publish(hold, true, 0, A)).IsEqual(A);
	}

	[TestCase]
	public void TheFirstFrameInTheWorldIsAHandBack()
	{
		// The load before it had the input: a button down as the world comes up is not a press in it.
		var hold = new Engine_HandBackHold(4);
		AssertThat(Publish(hold, true, 0, A)).IsEqual(0u);
	}

	[TestCase]
	public void AnAskedHoldHidesWhatIsDownAtTheNextPublish()
	{
		var hold = new Engine_HandBackHold(4);
		Publish(hold, true, 0, 0u);
		hold.HoldAll();
		AssertThat(Publish(hold, true, 0, A)).IsEqual(0u);
		AssertThat(Publish(hold, true, 0, 0u)).IsEqual(0u);
		AssertThat(Publish(hold, true, 0, A)).IsEqual(A);
	}

	[TestCase]
	public void APadOutOfRangeHidesNothing()
	{
		var hold = new Engine_HandBackHold(2);
		hold.HoldAll();
		AssertThat(hold.Hidden(5, A)).IsEqual(0u);
		AssertThat(hold.Hidden(-1, A)).IsEqual(0u);
	}
}
