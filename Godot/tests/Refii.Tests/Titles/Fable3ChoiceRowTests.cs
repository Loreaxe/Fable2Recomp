namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// A choice row (Fable3ScreenPage.Row.Choice): the value its line ends with stands between two drawn
/// arrows, never between `&lt;` and `&gt;`, which the title's text markup reads as a tag and draws as
/// nothing (int7d D2: the bed's "&lt; Midday &gt;" vanished; Engine_UiTextMarkup consumes a miss).
/// </summary>
[TestSuite]
public class Fable3ChoiceRowTests
{
	[TestCase]
	public void TheArrowsStandEitherSideOfTheChoice()
	{
		// A 300-unit label whose last third is the choice; pips 12 a side.
		(float leftEnd, float rightStart) = Fable3ListPanel.ChoiceArrows(-330f, -30f, 90f, 30f, 12f);
		AssertThat(leftEnd).IsEqualApprox(-130f - 6f, 0.001f);
		AssertThat(rightStart).IsEqualApprox(-30f + 6f, 0.001f);

		// A line that is only the choice (the divorce reason): the arrows bracket the whole label.
		(leftEnd, rightStart) = Fable3ListPanel.ChoiceArrows(0f, 100f, 50f, 50f, 10f);
		AssertThat(leftEnd).IsEqualApprox(-5f, 0.001f);
		AssertThat(rightStart).IsEqualApprox(105f, 0.001f);

		// Nothing measured: the arrows close on the label's end.
		(leftEnd, rightStart) = Fable3ListPanel.ChoiceArrows(0f, 100f, 0f, 10f, 10f);
		AssertThat(leftEnd).IsEqualApprox(95f, 0.001f);
		AssertThat(rightStart).IsEqualApprox(105f, 0.001f);
	}

	[TestCase]
	public void AnAngleBracketedChoiceIsAMarkupTagTheTitleDrawsAsNothing()
	{
		// Why the choice is a mark: the tokeniser consumes "< Midday >" whole, leaving only the label.
		var text = new System.Text.StringBuilder();
		foreach (Engine_UiTextMarkup.Run run in Engine_UiTextMarkup.Vocabulary.Fable3.Split("Sleep    < Midday >"))
		{
			text.Append(run.Text);
		}

		AssertThat(text.ToString()).IsEqual("Sleep    ");
	}
}
