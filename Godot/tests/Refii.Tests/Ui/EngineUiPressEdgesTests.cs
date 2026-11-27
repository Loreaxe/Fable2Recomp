namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// A pad's buttons as presses for a Godot-driven screen (Engine_UiPressEdges): a button counts
/// once, and one held when the screen gets the pad is not a press until it has come up.
/// </summary>
[TestSuite]
public class EngineUiPressEdgesTests
{
	private const uint A = 0x1000u;
	private const uint B = 0x2000u;
	private const uint Start = 0x0010u;

	[TestCase]
	public void APressCountsOnce()
	{
		var edges = default(Engine_UiPressEdges);
		AssertThat(edges.Take(A) == A).IsTrue();
		AssertThat(edges.Take(A) == 0u).IsTrue();
		AssertThat(edges.Take(A | B) == B).IsTrue();
		AssertThat(edges.Take(0u) == 0u).IsTrue();
		AssertThat(edges.Take(A) == A).IsTrue();
	}

	/// <summary>The START that opens a screen, still down on the screen's first frame, does not close it.</summary>
	[TestCase]
	public void AButtonHeldWhenTheScreenGetsThePadIsNotAPress()
	{
		var edges = default(Engine_UiPressEdges);
		edges.Hold();
		AssertThat(edges.Take(Start) == 0u).IsTrue();
		AssertThat(edges.Take(Start) == 0u).IsTrue();
		AssertThat(edges.Take(0u) == 0u).IsTrue();
		AssertThat(edges.Take(Start) == Start).IsTrue();
	}

	/// <summary>The B that leaves the page over a screen is not that screen's B; another button pressed with it is.</summary>
	[TestCase]
	public void OnlyWhatWasHeldIsWithheld()
	{
		var edges = default(Engine_UiPressEdges);
		edges.Take(0u);
		edges.Hold();
		edges.Hold();
		AssertThat(edges.Take(B) == 0u).IsTrue();
		AssertThat(edges.Take(B | A) == A).IsTrue();
	}

	/// <summary>A chord input still down when a capture lets the pad back is swallowed, however long the capture held.</summary>
	[TestCase]
	public void HoldWhileCapturingSwallowsWhatIsStillDown()
	{
		var edges = default(Engine_UiPressEdges);
		AssertThat(edges.Take(A) == A).IsTrue();
		for (int frame = 0; frame < 200; ++frame)
		{
			edges.Hold();
		}

		AssertThat(edges.Take(B) == 0u).IsTrue();
		AssertThat(edges.Take(0u) == 0u).IsTrue();
		AssertThat(edges.Take(B) == B).IsTrue();
	}
}
