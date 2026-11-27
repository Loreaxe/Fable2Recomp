namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;
using Kind = Refii.Ui.Engine_UiModel.GuiNativeArgumentKind;
using Button = Refii.Titles.Fable3.Fable3GuiWorldModel.Button;

/// <summary>
/// The Sanctuary's list as the guest states it once it has filled the mode (interaction_guiworld.cpp): the
/// kept rows in push order, the stored cursor, the hold slots; which presses post; the levers' text;
/// the stick's reading and the list step's repeat.
/// </summary>
[TestSuite]
public class Fable3GuiWorldModelTests
{
	/// <summary>The rows table as the statement carries it: `n.TextTag` … for each row in push order.</summary>
	private static Engine_UiModel.GuiNativeArgument Rows(params string[] tags)
	{
		var fields = new Dictionary<string, Engine_UiModel.GuiNativeArgument>(System.StringComparer.Ordinal);
		for (int i = 0; i < tags.Length; ++i)
		{
			string n = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
			fields[n + "TextTag"] = new(Kind.String, tags[i]);
			fields[n + "Icon"] = new(Kind.String, "icon");
			fields[n + "Message"] = new(Kind.String, i == 0 ? "PickOne" : string.Empty);
			fields[n + "SelectedTag"] = new(Kind.String, string.Empty);
			fields[n + "SelectedHeldTag"] = new(Kind.String, string.Empty);
		}

		return new Engine_UiModel.GuiNativeArgument(Kind.Table, fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), fields);
	}

	[TestCase]
	public void TheRowsAreThePushedRowsInOrderAndTheCursorTheStoredOne()
	{
		Fable3GuiWorldModel model = Fable3GuiWorldModel.Stated(2, 0, Rows("TEXT_ONE", "TEXT_TWO", "TEXT_THREE"));
		AssertThat(model.Rows.Count).IsEqual(3);
		AssertThat(model.Rows[0].TextTag).IsEqual("TEXT_ONE");
		AssertThat(model.Rows[0].Message).IsEqual("PickOne");
		AssertThat(model.Rows[1].Message).IsEqual(string.Empty);
		AssertThat(model.Rows[2].TextTag).IsEqual("TEXT_THREE");
		AssertThat(model.Cursor).IsEqual(2);
	}

	[TestCase]
	public void ACursorTheGuestLeftOutsideTheRowsReadsAsTheFirst()
	{
		// With no rows the parser's unsigned clamp leaves CurrentListIndex as it was.
		Fable3GuiWorldModel empty = Fable3GuiWorldModel.Stated(7, 0, Rows());
		AssertThat(empty.Cursor).IsEqual(0);
		AssertThat(empty.Rows.Count).IsEqual(0);
		AssertThat(Fable3GuiWorldModel.Stated(-1, 0, Rows("TEXT_ONE")).Cursor).IsEqual(0);
		AssertThat(Fable3GuiWorldModel.Stated(0, 0, new Engine_UiModel.GuiNativeArgument(Kind.None, string.Empty)).Rows.Count).IsEqual(0);
	}

	[TestCase]
	public void EveryFaceButtonPostsButAHoldSlots()
	{
		// Bit 3 is Y's P_Fill.
		Fable3GuiWorldModel model = Fable3GuiWorldModel.Stated(0, 1 << 3, Rows("TEXT_ONE"));
		AssertThat(model.Verb(Button.A)).IsEqual("a");
		AssertThat(model.Verb(Button.B)).IsEqual("b");
		AssertThat(model.Verb(Button.X)).IsEqual("x");
		AssertThat(model.Verb(Button.Y)).IsNull();
		AssertThat(model.Verb(Button.Start)).IsEqual("start");
		AssertThat(model.Holds(Button.Y)).IsTrue();
		AssertThat(model.Holds(Button.A)).IsFalse();
		AssertThat(model.Holds(Button.Start)).IsFalse();
	}

	[TestCase]
	public void TheLeversNameThePlayerAndTheVerb()
	{
		AssertThat(Fable3GuiWorldModel.Lever(1, "a")).IsEqual("guiworld:1:a");
		AssertThat(Fable3GuiWorldModel.Lever(2, "stick:left")).IsEqual("guiworld:2:stick:left");
		AssertThat(Fable3GuiWorldModel.CursorLever(1, 12)).IsEqual("guiworld:1:cursor:12");
		AssertThat(Fable3GuiWorldModel.DyeValueLever(1, 0.5f)).IsEqual("guiworld:1:dye-value:0.5");
		// Any value but 2 is player one.
		AssertThat(Fable3GuiWorldModel.Lever(0, "b")).IsEqual("guiworld:1:b");
	}

	[TestCase]
	public void TheStickReadsXFirstThenYPastPointSix()
	{
		AssertThat(Fable3GuiWorldModel.StickDirection(0.7f, 0.9f)).IsEqual("right");
		AssertThat(Fable3GuiWorldModel.StickDirection(-0.7f, 0f)).IsEqual("left");
		AssertThat(Fable3GuiWorldModel.StickDirection(0.5f, 0.7f)).IsEqual("up");
		AssertThat(Fable3GuiWorldModel.StickDirection(0f, -0.7f)).IsEqual("down");
		AssertThat(Fable3GuiWorldModel.StickDirection(0.6f, -0.6f)).IsEqual("none");
	}

	[TestCase]
	public void AHeldStickStepsAtOnceThenRepeats()
	{
		var repeat = new Fable3GuiWorldModel.StepRepeat();
		AssertThat(repeat.Step(1, 0.0)).IsEqual(1);
		AssertThat(repeat.Step(1, 0.2)).IsEqual(0);
		AssertThat(repeat.Step(1, Fable3GuiWorldModel.StepRepeat.RepeatAfter)).IsEqual(1);
		AssertThat(repeat.Step(1, Fable3GuiWorldModel.StepRepeat.RepeatAfter + 0.05)).IsEqual(0);
		AssertThat(repeat.Step(1, Fable3GuiWorldModel.StepRepeat.RepeatAfter + Fable3GuiWorldModel.StepRepeat.RepeatEvery)).IsEqual(1);
		AssertThat(repeat.Step(0, 1.0)).IsEqual(0);
		AssertThat(repeat.Step(-1, 1.01)).IsEqual(-1);
	}

	[TestCase]
	public void TheSanctuaryTakesItsOwnersPressesAndOnlyBFromTheOtherPlayer()
	{
		foreach (Button button in new[] { Button.A, Button.B, Button.X, Button.Y })
		{
			AssertThat(Fable3GuiWorldModel.TakesPress(1, 1, button)).IsTrue();
			AssertThat(Fable3GuiWorldModel.TakesPress(2, 2, button)).IsTrue();
		}

		AssertThat(Fable3GuiWorldModel.TakesPress(2, 1, Button.A)).IsFalse();
		AssertThat(Fable3GuiWorldModel.TakesPress(2, 1, Button.X)).IsFalse();
		AssertThat(Fable3GuiWorldModel.TakesPress(2, 1, Button.Y)).IsFalse();
		AssertThat(Fable3GuiWorldModel.TakesPress(1, 2, Button.A)).IsFalse();
		AssertThat(Fable3GuiWorldModel.TakesPress(2, 1, Button.B)).IsTrue();
	}
}
