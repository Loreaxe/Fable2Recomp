namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

/// <summary>
/// Player two's half keeps its own HUD statement through hybrid's swap (<see cref="Fable2PlayerTwoHudHold{T}"/>):
/// the guest stops stating hero two's d-pad when it is told "shared", while the half is still drawn for
/// the swap; before a split's first statement the half has none. Never the shared HUD's (player one's).
/// </summary>
[TestSuite]
public class PlayerTwoHudHoldTests
{
	private readonly record struct Hud(string Up);

	[TestCase]
	public void AtRestTheHalfShowsWhatIsStatedNow()
	{
		var hold = new Fable2PlayerTwoHudHold<Hud>();
		AssertThat(hold.Take(true, new Hud("coin")) is { Up: "coin" }).IsTrue();
		AssertThat(hold.Take(true, new Hud("potion")) is { Up: "potion" }).IsTrue();
	}

	[TestCase]
	public void TheRejoinSwapKeepsPlayerTwosLastStatementUntilTheHalfIsParked()
	{
		var hold = new Fable2PlayerTwoHudHold<Hud>();
		hold.Take(true, new Hud("coin"));

		// Told "shared": the guest states nothing for hero two, the half runs back for up to two seconds.
		for (int frame = 0; frame < 120; ++frame)
		{
			AssertThat(hold.Take(true, null) is { Up: "coin" }).IsTrue();
		}

		// Parked: nothing, and nothing carried into the next split.
		AssertThat(hold.Take(false, null) is null).IsTrue();
		AssertThat(hold.Take(true, null) is null).IsTrue();
	}

	[TestCase]
	public void ASplitBeforeItsFirstStatementHasNoneThenTakesIt()
	{
		var hold = new Fable2PlayerTwoHudHold<Hud>();
		AssertThat(hold.Take(true, null) is null).IsTrue();
		AssertThat(hold.Take(true, null) is null).IsTrue();
		AssertThat(hold.Take(true, new Hud("coin")) is { Up: "coin" }).IsTrue();
	}

	[TestCase]
	public void AStatementWhileParkedIsNotKept()
	{
		var hold = new Fable2PlayerTwoHudHold<Hud>();
		AssertThat(hold.Take(false, new Hud("stale")) is null).IsTrue();
		AssertThat(hold.Take(true, null) is null).IsTrue();
	}
}
