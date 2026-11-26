namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The hold-to-accept meter (Engine_UiHoldMeter): it fills while held and drains when let go at one
/// rate, and accepts once per crossing of full, whatever device holds it.
/// </summary>
[TestSuite]
public class EngineUiHoldMeterTests
{
	[TestCase]
	public void AFullHoldAcceptsOnceOnTheStepItIsFull()
	{
		var meter = new Engine_UiHoldMeter(1.0);
		int accepts = 0;
		for (int i = 0; i < 9; ++i)
		{
			accepts += meter.Step(0.1, held: true) ? 1 : 0;
		}

		AssertThat(accepts).IsEqual(0);
		AssertThat(meter.Progress).IsBetween(0.89f, 0.91f);
		accepts += meter.Step(0.1, held: true) ? 1 : 0;
		AssertThat(accepts).IsEqual(1);
		for (int i = 0; i < 20; ++i)
		{
			accepts += meter.Step(0.1, held: true) ? 1 : 0;
		}

		AssertThat(accepts).IsEqual(1);
		AssertThat(meter.Progress).IsEqual(1f);
	}

	[TestCase]
	public void LettingGoDrainsAtTheSameRate()
	{
		var meter = new Engine_UiHoldMeter(1.0);
		meter.Step(0.5, held: true);
		meter.Step(0.25, held: false);
		AssertThat(meter.Progress).IsBetween(0.24f, 0.26f);
		meter.Step(1.0, held: false);
		AssertThat(meter.Progress).IsEqual(0f);
	}

	[TestCase]
	public void ASecondAcceptNeedsTheFillToDropAndClimbAgain()
	{
		var meter = new Engine_UiHoldMeter(1.0);
		AssertThat(meter.Step(1.0, held: true)).IsTrue();
		AssertThat(meter.Step(0.1, held: true)).IsFalse();
		meter.Step(0.2, held: false);
		AssertThat(meter.Step(0.2, held: true)).IsTrue();
		meter.Reset();
		AssertThat(meter.Progress).IsEqual(0f);
	}
}
