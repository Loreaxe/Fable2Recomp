namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// The TopBox's held buttons on Godot's clock (Fable3TopBoxHold): a fill moves only while the message
/// asks for the hold, this side has the button down and the guest states it held; it drains at the
/// stated released rate; each change is stated back in thousandths; a new message starts empty.
/// </summary>
[TestSuite]
public class Fable3TopBoxHoldTests
{
	// Rates as the guest states them, per millisecond: 1 and 2 per second.
	private static readonly Fable3UiStatements.TopBoxHold Held = new(true, 0.001f, 0.002f);
	private static readonly Fable3UiStatements.TopBoxHold LetGo = new(false, 0.001f, 0.002f);
	private static readonly Fable3TopBoxHold.Button None = new(false, false, default);

	[TestCase]
	public void AHeldButtonFillsAtTheStatedRateAndStatesEachChange()
	{
		var hold = new Fable3TopBoxHold();
		var levers = new List<string>();
		hold.Step(0.25, 7u, new Fable3TopBoxHold.Button(true, true, Held), None, 1, levers);
		AssertThat(levers).ContainsExactly("topbox:1:a:fill:250");
		levers.Clear();
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(true, true, Held), None, 1, levers);
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(true, true, Held), None, 1, levers);
		AssertThat(levers).ContainsExactly("topbox:1:a:fill:750", "topbox:1:a:fill:1000");
		levers.Clear();
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(true, true, Held), None, 1, levers);
		AssertThat(levers).IsEmpty();
		AssertThat(hold.Fill(0)).IsEqual(1f);
	}

	[TestCase]
	public void NothingFillsUntilTheGuestStatesTheHold()
	{
		var hold = new Fable3TopBoxHold();
		var levers = new List<string>();
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(true, true, LetGo), None, 1, levers);
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(true, true, default), None, 1, levers);
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(false, true, Held), None, 1, levers);
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(true, false, Held), None, 1, levers);
		AssertThat(levers).IsEmpty();
		AssertThat(hold.Fill(0)).IsEqual(0f);
	}

	[TestCase]
	public void LettingGoDrainsAtTheReleasedRateAndYIsItsOwnMeter()
	{
		var hold = new Fable3TopBoxHold();
		var levers = new List<string>();
		hold.Step(0.5, 7u, None, new Fable3TopBoxHold.Button(true, true, Held), 2, levers);
		AssertThat(levers).ContainsExactly("topbox:2:y:fill:500");
		levers.Clear();
		// The guest turned the hold into a release (the hero moved): 0.002 per ms, 2 per second down.
		hold.Step(0.125, 7u, None, new Fable3TopBoxHold.Button(true, true, LetGo), 2, levers);
		AssertThat(levers).ContainsExactly("topbox:2:y:fill:250");
		AssertThat(hold.Fill(0)).IsEqual(0f);
	}

	[TestCase]
	public void ANewMessageStartsEmptyAndSaysSo()
	{
		var hold = new Fable3TopBoxHold();
		var levers = new List<string>();
		hold.Step(0.5, 7u, new Fable3TopBoxHold.Button(true, true, Held), None, 1, levers);
		levers.Clear();
		hold.Step(0.0, 9u, new Fable3TopBoxHold.Button(true, false, LetGo), None, 1, levers);
		AssertThat(levers).ContainsExactly("topbox:1:a:fill:0");
		AssertThat(hold.Fill(0)).IsEqual(0f);
	}

	[TestCase]
	public void TheGuestsOwnRatesFillInOneAndAThirdSecondsAndDrainInHalfOne()
	{
		// GUIValues as the disc run stated them: 0.00075 per ms held, 0.002 per ms released.
		var held = new Fable3UiStatements.TopBoxHold(true, 0.00075f, 0.002f);
		var letGo = new Fable3UiStatements.TopBoxHold(false, 0.00075f, 0.002f);
		var hold = new Fable3TopBoxHold();
		var levers = new List<string>();
		hold.Step(1.0, 7u, new Fable3TopBoxHold.Button(true, true, held), None, 1, levers);
		AssertThat(levers).ContainsExactly("topbox:1:a:fill:750");
		levers.Clear();
		hold.Step(1.0 / 3.0, 7u, new Fable3TopBoxHold.Button(true, true, held), None, 1, levers);
		AssertThat(levers).ContainsExactly("topbox:1:a:fill:1000");
		levers.Clear();
		hold.Step(0.25, 7u, new Fable3TopBoxHold.Button(true, true, letGo), None, 1, levers);
		AssertThat(levers).ContainsExactly("topbox:1:a:fill:500");
		hold.Step(0.25, 7u, new Fable3TopBoxHold.Button(true, true, letGo), None, 1, levers);
		AssertThat(hold.Fill(0)).IsEqual(0f);
	}
}
