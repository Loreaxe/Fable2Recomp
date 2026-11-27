namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// A on a row of the Controls page (Engine_UiControlsPage.AcceptedIndex): a rebind row goes to its
/// "press an input" position, where the page arms the capture; a reset row flips; a choice steps
/// on and wraps; a heading stays. And the reset row's caption names the driver's commit button.
/// </summary>
[TestSuite]
public class EngineUiControlsAcceptTests
{
	private static readonly string[] Keys = { "F8", "F9", "E" };

	[TestCase]
	public void ARebindRowGoesToItsCapturePosition()
	{
		var key = new Engine_UiControlsPage.KeyLine("quick_save", "F9", Keys);
		var pad = new Engine_UiControlsPage.PadLine(1, PadInput.X, () => GlyphStyle.Xbox, () => false, null);
		var chord = new Engine_UiControlsPage.PadActionLine("quick_save", () => GlyphStyle.Xbox, () => false);
		foreach (Engine_UiControlsPage.Line line in new Engine_UiControlsPage.Line[] { key, pad, chord })
		{
			AssertThat(line.Captures is not null).IsTrue();
			for (int index = 0; index < line.Choices; ++index)
			{
				AssertThat(Engine_UiControlsPage.AcceptedIndex(line, index)).IsEqual(0);
			}
		}

		AssertThat(Engine_UiControlsPage.KeyLine.CaptureIndex).IsEqual(0);
		AssertThat(Engine_UiControlsPage.PadLine.CaptureIndex).IsEqual(0);
		AssertThat(Engine_UiControlsPage.PadActionLine.CaptureIndex).IsEqual(0);
	}

	[TestCase]
	public void AResetRowFlips()
	{
		var reset = new Engine_UiControlsPage.ResetLine("Reset everything", "all");
		AssertThat(Engine_UiControlsPage.AcceptedIndex(reset, 0)).IsEqual(1);
		AssertThat(Engine_UiControlsPage.AcceptedIndex(reset, 1)).IsEqual(0);
	}

	[TestCase]
	public void AChoiceStepsOnAndWraps()
	{
		var choice = new Engine_UiControlsPage.ChoiceLine("Mouse sensitivity", "mouse_sensitivity",
			new[] { ("1.0", "1.0"), ("0.5", "0.5"), ("2.0", "2.0"), ("3.0", "3.0") });
		AssertThat(Engine_UiControlsPage.AcceptedIndex(choice, 0)).IsEqual(1);
		AssertThat(Engine_UiControlsPage.AcceptedIndex(choice, 2)).IsEqual(3);
		AssertThat(Engine_UiControlsPage.AcceptedIndex(choice, 3)).IsEqual(0);
	}

	[TestCase]
	public void AHeadingStays()
	{
		var header = new Engine_UiControlsPage.Header("Mouse");
		AssertThat(Engine_UiControlsPage.AcceptedIndex(header, 0)).IsEqual(0);
	}

	/// <summary>Fable 2's page says A (its Done); a driver whose commit is another button names that one.</summary>
	[TestCase]
	public void TheResetCaptionNamesTheCommitButton()
	{
		var reset = new Engine_UiControlsPage.ResetLine("Reset everything", "all");
		AssertThat(reset.Caption(0)).IsEqual("-");
		AssertThat(reset.Caption(1)).IsEqual("Reset (A saves, B keeps)");
		AssertThat(Engine_UiControlsPage.ResetLine.Caption(1, "X")).IsEqual("Reset (X saves, B keeps)");
		AssertThat(Engine_UiControlsPage.ResetLine.Caption(0, "X")).IsEqual("-");
	}
}
