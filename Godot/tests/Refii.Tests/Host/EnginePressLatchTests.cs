namespace Refii.Tests.Host;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// A host action (Fable 3's G safety toggle, F9 quicksave, their pad chords) runs once per press:
/// the context dropping for a frame under a held key must not re-arm it, and a press that went
/// down out of context never runs.
/// </summary>
[TestSuite]
public class EnginePressLatchTests
{
	[TestCase]
	public void OnePressFiresOnceWhileHeld()
	{
		var latch = new Engine_PressLatch<string>();
		AssertThat(latch.Press("safety_mode", false, true)).IsFalse();
		AssertThat(latch.Press("safety_mode", true, true)).IsTrue();
		for (int frame = 0; frame < 30; ++frame)
		{
			AssertThat(latch.Press("safety_mode", true, true)).IsFalse();
		}

		AssertThat(latch.Press("safety_mode", false, true)).IsFalse();
		AssertThat(latch.Press("safety_mode", true, true)).IsTrue();
	}

	/// <summary>
	/// The defect the old per-frame read had: G held, the world loses the input for a frame (a box,
	/// a notice, an interaction list), then has it again — the same press toggled safety twice.
	/// </summary>
	[TestCase]
	public void AContextDropUnderAHeldKeyDoesNotFireAgain()
	{
		var latch = new Engine_PressLatch<string>();
		AssertThat(latch.Press("safety_mode", true, true)).IsTrue();
		AssertThat(latch.Press("safety_mode", true, false)).IsFalse();
		AssertThat(latch.Press("safety_mode", true, true)).IsFalse();
		AssertThat(latch.Press("safety_mode", true, true)).IsFalse();
	}

	[TestCase]
	public void APressThatWentDownOutOfContextNeverFires()
	{
		var latch = new Engine_PressLatch<string>();
		AssertThat(latch.Press("quick_save", true, false)).IsFalse();
		AssertThat(latch.Press("quick_save", true, true)).IsFalse();
		AssertThat(latch.IsDown("quick_save")).IsTrue();
		AssertThat(latch.Press("quick_save", false, true)).IsFalse();
		AssertThat(latch.IsDown("quick_save")).IsFalse();
		AssertThat(latch.Press("quick_save", true, true)).IsTrue();
	}

	/// <summary>Each input is its own press: two actions, or one action on two players' pads.</summary>
	[TestCase]
	public void InputsAreIndependent()
	{
		var latch = new Engine_PressLatch<(string, int)>();
		AssertThat(latch.Press(("safety_mode", 0), true, true)).IsTrue();
		AssertThat(latch.Press(("safety_mode", 1), true, true)).IsTrue();
		AssertThat(latch.Press(("quick_save", 0), true, true)).IsTrue();
		AssertThat(latch.Press(("safety_mode", 0), true, true)).IsFalse();
		AssertThat(latch.Press(("safety_mode", 1), false, true)).IsFalse();
		AssertThat(latch.Press(("safety_mode", 0), true, true)).IsFalse();
		AssertThat(latch.Press(("safety_mode", 1), true, true)).IsTrue();
	}
}
