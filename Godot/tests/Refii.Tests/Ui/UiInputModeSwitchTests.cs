namespace Refii.Tests.Ui;

using GdUnit4;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// The device switch takes a mouse movement only from a pointer that reaches the game: the focused window's,
/// or the debug channel's. The desktop pointer crossing the unfocused window (int7g: world prompts turned to
/// keycaps with only pad and Lua input driven) leaves the device as it was. Pure.
/// </summary>
[TestSuite]
public class UiInputModeSwitchTests
{
	[TestCase]
	public void AMovementOverTheUnfocusedWindowDoesNotTakeTheDevice()
	{
		AssertThat(Engine_UiInputMode.MovementSwitches(400f, devicesReachGuest: false, pointerScripted: false)).IsFalse();
	}

	[TestCase]
	public void AFocusedOrScriptedMovementTakesTheDevice()
	{
		AssertThat(Engine_UiInputMode.MovementSwitches(400f, devicesReachGuest: true, pointerScripted: false)).IsTrue();
		AssertThat(Engine_UiInputMode.MovementSwitches(400f, devicesReachGuest: false, pointerScripted: true)).IsTrue();
	}

	[TestCase]
	public void AJitterOfTwoPixelsNeverTakesIt()
	{
		AssertThat(Engine_UiInputMode.MovementSwitches(4f, devicesReachGuest: true, pointerScripted: true)).IsFalse();
	}
}
