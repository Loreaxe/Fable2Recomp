namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// Which player a press on a Godot-owned Fable 3 surface acts for (Fable3ActingPlayer): the pads'
/// players, each player's own press edges, the pointer's player, and the levers' player field.
/// </summary>
[TestSuite]
public class Fable3ActingPlayerTests
{
	private const uint A = 0x1000u;
	private const uint B = 0x2000u;

	[TestCase]
	public void PadZeroIsPlayerOnesAndPadOnePlayerTwos()
	{
		AssertThat(Fable3ActingPlayer.LocalOfPad(0)).IsEqual(1);
		AssertThat(Fable3ActingPlayer.LocalOfPad(1)).IsEqual(2);
		AssertThat(Fable3ActingPlayer.LocalOfPad(2)).IsEqual(0);
		AssertThat(Fable3ActingPlayer.LocalOfPad(3)).IsEqual(0);
		AssertThat(Fable3ActingPlayer.PadOf(1)).IsEqual(0);
		AssertThat(Fable3ActingPlayer.PadOf(2)).IsEqual(1);
		AssertThat(Fable3ActingPlayer.LocalOfPad(Fable3ActingPlayer.PadOf(2))).IsEqual(2);
	}

	[TestCase]
	public void TheTitleStatesPlayerTwosPadToTheShell()
	{
		var input = new Fable3Input();
		AssertThat(input.ModPlayer(0)).IsEqual(1);
		AssertThat(input.ModPlayer(1)).IsEqual(2);
		AssertThat(input.ModPlayer(2)).IsEqual(0);
	}

	[TestCase]
	public void ThePointerIsThePlayerOfTheKeyboardsPad()
	{
		AssertThat(Fable3ActingPlayer.Pointer).IsEqual(Fable3ActingPlayer.LocalOfPad(0));
	}

	[TestCase]
	public void EachPlayersPressIsAnEdgeOnThatPlayersPad()
	{
		var edges = new Fable3ActingPlayer.Edges();
		AssertThat(edges.Pressed(1, A)).IsEqual(A);
		// Player one still holds A: player two's A is still a press.
		AssertThat(edges.Pressed(2, A)).IsEqual(A);
		AssertThat(edges.Pressed(1, A)).IsEqual(0u);
		AssertThat(edges.Pressed(2, A | B)).IsEqual(B);
		AssertThat(edges.Pressed(2, 0u)).IsEqual(0u);
		AssertThat(edges.Pressed(2, A)).IsEqual(A);
	}

	[TestCase]
	public void SettledButtonsAreNotPresses()
	{
		var edges = new Fable3ActingPlayer.Edges();
		edges.Settle(A, B);
		AssertThat(edges.Pressed(1, A | B)).IsEqual(B);
		AssertThat(edges.Pressed(2, A | B)).IsEqual(A);
	}

	[TestCase]
	public void AHoldIsForWhoeverHoldsPlayerOneFirst()
	{
		AssertThat(Fable3ActingPlayer.Holder(false, false, false)).IsEqual(0);
		AssertThat(Fable3ActingPlayer.Holder(true, false, true)).IsEqual(1);
		AssertThat(Fable3ActingPlayer.Holder(false, true, true)).IsEqual(Fable3ActingPlayer.Pointer);
		AssertThat(Fable3ActingPlayer.Holder(false, false, true)).IsEqual(2);
	}

	[TestCase]
	public void TheLeversCarryThePlayer()
	{
		AssertThat(Fable3BoxAnswers.CardHold(0x40B06C08u, 2)).Contains("box:2:40B06C08");
		AssertThat(Fable3BoxAnswers.MenuCancel(2)).IsEqual("menubox-cancel:2");
		// Only player one selects on the menu box (the mapper's `cmpwi r4,1`).
		AssertThat(Fable3BoxAnswers.MenuPick(0, 2)).IsNull();
		AssertThat(Fable3BoxAnswers.MenuPick(0, 1)).IsNotNull();
		AssertThat(Fable3BoxAnswers.SaveMenuRow(1, true, Fable3ActingPlayer.Pointer)).IsEqual("savemenu-row:1:2:accept");
		AssertThat(Fable3InteractionList.Step(Fable3ActingPlayer.Pointer, down: true)).IsEqual("interaction-list:1:down");
	}
}
