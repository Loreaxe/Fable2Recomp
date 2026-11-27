namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// One pad's buttons as edges (Engine_UiPadEdges): what went down and what came up since the last
/// step, and the per-button owner tag set at a press and read at its release.
/// </summary>
[TestSuite]
public class EngineUiPadEdgesTests
{
	[TestCase]
	public void AStepSplitsTheMaskIntoPressedAndReleased()
	{
		var edges = new Engine_UiPadEdges();
		(uint pressed, uint released) = edges.Step(0x1001u);
		AssertThat(pressed).IsEqual(0x1001u);
		AssertThat(released).IsEqual(0u);
		(pressed, released) = edges.Step(0x1001u);
		AssertThat(pressed | released).IsEqual(0u);
		(pressed, released) = edges.Step(0x4001u);
		AssertThat(pressed).IsEqual(0x4000u);
		AssertThat(released).IsEqual(0x1000u);
		AssertThat(edges.Down).IsEqual(0x4001u);
		(pressed, released) = edges.Step(0u);
		AssertThat(pressed).IsEqual(0u);
		AssertThat(released).IsEqual(0x4001u);
		AssertThat(edges.Down).IsEqual(0u);
	}

	[TestCase]
	public void AButtonsOwnerIsKeptUntilItsRelease()
	{
		var edges = new Engine_UiPadEdges();
		edges.Step(0x1000u);
		edges.Own(0x1000u, 5);
		edges.Own(0x10000u, 2);
		AssertThat(edges.Owner(0x1000u)).IsEqual(5);
		AssertThat(edges.Owner(0x4000u)).IsEqual(0);
		AssertThat(edges.Release(0x1000u)).IsEqual(5);
		AssertThat(edges.Release(0x1000u)).IsEqual(0);
		AssertThat(edges.Release(0x10000u)).IsEqual(2);
	}
}
