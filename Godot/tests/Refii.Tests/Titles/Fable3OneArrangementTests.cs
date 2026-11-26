namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The shared menu view's one-arrangement mode (Fable 3) against its two device looks (Fable 2):
/// what the list draws must not depend on the device under the first and must under the second.
/// Pure rules; no Godot object is built.
/// </summary>
[TestSuite]
public class Fable3OneArrangementTests
{
	/// <summary>Under one arrangement the cursor is drawn for the pad and for the keyboard and mouse alike.</summary>
	[TestCase]
	public void OneArrangementDrawsTheCursorOnEveryDevice()
	{
		AssertThat(Engine_FrontEndMenuView.CursorDrawnOn(oneArrangement: true, keycaps: false)).IsTrue();
		AssertThat(Engine_FrontEndMenuView.CursorDrawnOn(oneArrangement: true, keycaps: true)).IsTrue();
	}

	/// <summary>Under one arrangement the mouse's scroll bar is drawn for no device, so a switch moves nothing.</summary>
	[TestCase]
	public void OneArrangementDrawsNoScrollBarOnAnyDevice()
	{
		AssertThat(Engine_FrontEndMenuView.ScrollBarOn(oneArrangement: true, keyboardMouse: false)).IsFalse();
		AssertThat(Engine_FrontEndMenuView.ScrollBarOn(oneArrangement: true, keyboardMouse: true)).IsFalse();
	}

	/// <summary>Fable 2's looks are untouched: the ring cursor is the pad's, the scroll bar the mouse's.</summary>
	[TestCase]
	public void TheDeviceLooksStandWhenTheModeIsOff()
	{
		AssertThat(Engine_FrontEndMenuView.CursorDrawnOn(oneArrangement: false, keycaps: false)).IsTrue();
		AssertThat(Engine_FrontEndMenuView.CursorDrawnOn(oneArrangement: false, keycaps: true)).IsFalse();
		AssertThat(Engine_FrontEndMenuView.ScrollBarOn(oneArrangement: false, keyboardMouse: true)).IsTrue();
		AssertThat(Engine_FrontEndMenuView.ScrollBarOn(oneArrangement: false, keyboardMouse: false)).IsFalse();
	}
}
