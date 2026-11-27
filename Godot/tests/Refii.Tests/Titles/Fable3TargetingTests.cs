namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Entities;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// Fable 3's targeting statement `@Target.By(thing, players, interact)` (target_statements.cpp):
/// the players the setter leaves on a thing are what each split half draws its highlight and
/// "Interact" icon by; a thing no player targets is forgotten. No Godot type is built.
/// </summary>
[TestSuite]
public class Fable3TargetingTests
{
	private const uint Trader = 0x4321ABC0u;

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument N(float value) =>
		new(Engine_UiModel.GuiNativeArgumentKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static Engine_UiModel.GuiNativeArgument B(bool value) => new(Engine_UiModel.GuiNativeArgumentKind.Boolean, value ? "1" : "0");

	private static void Target(uint thing, int players, bool interact) =>
		AssertThat(Fable3UiStatements.Take(new Engine_UiModel.GuiNativeCall(
			"@Target.By", new[] { S(thing.ToString("X8")), N(players), B(interact) }))).IsTrue();

	[BeforeTest]
	public void Reset() => Fable3Targeting.Clear();

	[TestCase]
	public void PlayerTwosTargetIsDrawnInPlayerTwosHalfOnly()
	{
		Target(Trader, 2, true);
		AssertThat(Fable3Targeting.TargetedBy(Trader)).IsEqual(2);
		AssertThat(Fable3Targeting.InteractBy(Trader)).IsEqual(2);
		AssertThat(Engine_EntityHighlight.DrawnInView(0, true, Fable3Targeting.TargetedBy(Trader))).IsFalse();
		AssertThat(Engine_EntityHighlight.DrawnInView(1, true, Fable3Targeting.TargetedBy(Trader))).IsTrue();
	}

	[TestCase]
	public void BothPlayersTargetingDrawInBothHalves()
	{
		Target(Trader, 2, true);
		Target(Trader, 3, true);
		AssertThat(Engine_EntityHighlight.DrawnInView(0, true, Fable3Targeting.TargetedBy(Trader))).IsTrue();
		AssertThat(Engine_EntityHighlight.DrawnInView(1, true, Fable3Targeting.TargetedBy(Trader))).IsTrue();
	}

	[TestCase]
	public void AnUntargetedThingIsForgotten()
	{
		Target(Trader, 1, true);
		Target(Trader, 0, true);
		AssertThat(Fable3Targeting.TargetedBy(Trader)).IsEqual(0);
		AssertThat(Fable3Targeting.InteractBy(Trader)).IsEqual(0);
	}

	[TestCase]
	public void AThingWithoutTheIconKeepsItsPromptsEverywhere()
	{
		// +0x28 clear: the setter raises no "Interact" icon, so the thing's pooled prompts are not its.
		Target(Trader, 1, false);
		AssertThat(Fable3Targeting.TargetedBy(Trader)).IsEqual(1);
		AssertThat(Fable3Targeting.InteractBy(Trader)).IsEqual(0);
	}
}
