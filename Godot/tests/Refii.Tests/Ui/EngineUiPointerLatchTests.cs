namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The pointer on a pinned list (Engine_UiPointerLatch): the row it picked stays its row while the
/// carousel slides other rows under the still pointer, until the pointer moves or the cursor is
/// taken on by another device.
/// </summary>
[TestSuite]
public class EngineUiPointerLatchTests
{
	[TestCase]
	public void NothingPickedIsTheRowUnderThePointer()
	{
		var latch = new Engine_UiPointerLatch();
		AssertThat(latch.Row(7, 10f, 20f, 2)).IsEqual(7);
		AssertThat(latch.Row(-1, 10f, 20f, 2)).IsEqual(-1);
		AssertThat(latch.Holding).IsFalse();
	}

	[TestCase]
	public void AClickedRowStaysThePointersRowWhileTheListSlides()
	{
		var latch = new Engine_UiPointerLatch();
		latch.Pick(5, 10f, 20f);

		// The cursor went to 5, the slide left row 7, then nothing, under the still pointer.
		AssertThat(latch.Row(7, 10f, 20f, 5)).IsEqual(5);
		AssertThat(latch.Row(-1, 10f, 20f, 5)).IsEqual(5);
		AssertThat(latch.Row(8, 10f, 20f, 5)).IsEqual(5);
		AssertThat(latch.Holding).IsTrue();
	}

	[TestCase]
	public void MovingThePointerLetsGo()
	{
		var latch = new Engine_UiPointerLatch();
		latch.Pick(5, 10f, 20f);
		AssertThat(latch.Row(7, 10f + Engine_UiPointerLatch.Slack, 20f, 5)).IsEqual(5);
		AssertThat(latch.Row(7, 10f, 20f - Engine_UiPointerLatch.Slack - 1f, 5)).IsEqual(7);
		AssertThat(latch.Holding).IsFalse();

		// It stays let go when the pointer comes back.
		AssertThat(latch.Row(6, 10f, 20f, 5)).IsEqual(6);
	}

	[TestCase]
	public void APadStepLetsGo()
	{
		var latch = new Engine_UiPointerLatch();
		latch.Pick(5, 10f, 20f);
		AssertThat(latch.Row(7, 10f, 20f, 5)).IsEqual(5);
		AssertThat(latch.Row(7, 10f, 20f, 6)).IsEqual(7);
		AssertThat(latch.Holding).IsFalse();
	}

	[TestCase]
	public void AWalkedCursorIsWaitedFor()
	{
		var latch = new Engine_UiPointerLatch();
		latch.Pick(5, 10f, 20f);
		foreach (int cursor in new[] { 2, 3, 4, 5, 5 })
		{
			AssertThat(latch.Row(9, 10f, 20f, cursor)).IsEqual(5);
		}

		AssertThat(latch.Row(9, 10f, 20f, 4)).IsEqual(9);
	}

	[TestCase]
	public void APickOnTheCursorRowHolds()
	{
		var latch = new Engine_UiPointerLatch();
		latch.Pick(3, 0f, 0f);
		AssertThat(latch.Row(3, 0f, 0f, 3)).IsEqual(3);
		latch.Pick(3, 0f, 0f);
		AssertThat(latch.Row(4, 0f, 0f, 3)).IsEqual(3);
	}

	[TestCase]
	public void ClearForgets()
	{
		var latch = new Engine_UiPointerLatch();
		latch.Pick(5, 10f, 20f);
		latch.Clear();
		AssertThat(latch.Holding).IsFalse();
		AssertThat(latch.Row(7, 10f, 20f, 5)).IsEqual(7);
	}

	[TestCase]
	public void ANewPickReplacesTheOld()
	{
		var latch = new Engine_UiPointerLatch();
		latch.Pick(5, 10f, 20f);
		AssertThat(latch.Row(7, 10f, 20f, 5)).IsEqual(5);
		latch.Pick(1, 300f, 40f);
		AssertThat(latch.Row(7, 300f, 40f, 5)).IsEqual(1);
		AssertThat(latch.Row(7, 300f, 40f, 1)).IsEqual(1);
	}
}
