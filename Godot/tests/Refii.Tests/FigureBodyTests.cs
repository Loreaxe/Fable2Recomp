using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Where a shown entity's body is between the guest's statement and the feed's node.</summary>
[TestSuite]
public sealed class FigureBodyTests
{
	[TestCase]
	public void AnEntityNoWalkHasReachedIsNotWalked() =>
		AssertThat(Engine_FigureBody.Of(walked: false, statedParts: 0, statesModel: false, hasNode: false))
			.IsEqual(Engine_FigureBody.State.NotWalked);

	[TestCase]
	public void AWalkedEntityWithNoStatementHasNoParts() =>
		AssertThat(Engine_FigureBody.Of(walked: true, statedParts: 0, statesModel: false, hasNode: false))
			.IsEqual(Engine_FigureBody.State.NoParts);

	[TestCase]
	public void StatedPartsWithoutANodeAreBuilding() =>
		AssertThat(Engine_FigureBody.Of(walked: true, statedParts: 9, statesModel: false, hasNode: false))
			.IsEqual(Engine_FigureBody.State.Building);

	[TestCase]
	public void AStatedSingleModelWithoutANodeIsBuilding() =>
		AssertThat(Engine_FigureBody.Of(walked: true, statedParts: 0, statesModel: true, hasNode: false))
			.IsEqual(Engine_FigureBody.State.Building);

	[TestCase]
	public void ANodeIsBuiltWhateverElseIsKept() =>
		AssertThat(Engine_FigureBody.Of(walked: false, statedParts: 0, statesModel: false, hasNode: true))
			.IsEqual(Engine_FigureBody.State.Built);

	[TestCase]
	public void EveryStateSaysSomethingDifferent()
	{
		var said = new System.Collections.Generic.HashSet<string>();
		foreach (Engine_FigureBody.State state in System.Enum.GetValues<Engine_FigureBody.State>())
		{
			said.Add(Engine_FigureBody.Describe(state));
		}

		AssertThat(said.Count).IsEqual(4);
	}
}
