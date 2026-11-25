namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Game;
using Refii.Titles.Fable2;

/// <summary>
/// The pure rules behind the second fix round: the debug channel's mouse point and the abilities
/// book's stated discard gate (payload_bridge.h key 20). Pure model code.
/// </summary>
[TestSuite]
public class SweepTwoFixTests
{
	[TestCase]
	public void AMousePointInPixelsStaysInTheWindow()
	{
		AssertThat(GameDebugMouse.WindowPoint(640f, 360f, false, 1280f, 720f)).IsEqual((640f, 360f));
		AssertThat(GameDebugMouse.WindowPoint(5000f, -3f, false, 1280f, 720f)).IsEqual((1279f, 0f));
	}

	[TestCase]
	public void ANormalisedMousePointScalesByTheWindow()
	{
		AssertThat(GameDebugMouse.WindowPoint(0.5f, 0.25f, true, 1920f, 1080f)).IsEqual((960f, 270f));
		AssertThat(GameDebugMouse.WindowPoint(1f, 1f, true, 1920f, 1080f)).IsEqual((1919f, 1079f));
	}

	[TestCase]
	public void TheRowIdIsTheGuestsPackedId()
	{
		AssertThat(Engine_AbilityDiscardGates.RowId(0x1234, 0x5678)).IsEqual(0x12345678u);
		AssertThat(Engine_AbilityDiscardGates.RowId(0, -1)).IsEqual(0x0000FFFFu);
	}

	[TestCase]
	public void TheGateStatementNamesItsRowAndAnswer()
	{
		AssertThat(Engine_AbilityDiscardGates.Decode(0u) is null).IsTrue();
		AssertThat(Engine_AbilityDiscardGates.Decode(0xABCD0001u)).IsEqual(((uint, bool)?)(0xABCD0000u, true));
		AssertThat(Engine_AbilityDiscardGates.Decode(0xABCD0000u)).IsEqual(((uint, bool)?)(0xABCD0000u, false));
	}

	[TestCase]
	public void ARowIsShutUntilTheGuestSaysOtherwise()
	{
		Engine_AbilityDiscardGates.Clear();
		AssertThat(Engine_AbilityDiscardGates.IsOpen(0x80001235u)).IsFalse();
		Engine_AbilityDiscardGates.Observe(0x80001235u);
		AssertThat(Engine_AbilityDiscardGates.IsOpen(0x80001235u)).IsTrue();
		AssertThat(Engine_AbilityDiscardGates.IsOpen(0x80001234u)).IsTrue();
		Engine_AbilityDiscardGates.Observe(0x80001234u);
		AssertThat(Engine_AbilityDiscardGates.IsOpen(0x80001235u)).IsFalse();
		AssertThat(Engine_AbilityDiscardGates.IsOpen(0x90000000u)).IsFalse();
		Engine_AbilityDiscardGates.Clear();
	}
}
