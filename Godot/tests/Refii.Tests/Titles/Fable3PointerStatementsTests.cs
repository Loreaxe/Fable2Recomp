namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// The guest's two holds that free the pointer (Fable3PointerStatements): `@GameMode.Ui` and
/// `@Box.Hold`, as Fable3UiStatements routes them.
/// </summary>
[TestSuite]
public class Fable3PointerStatementsTests
{
	private static readonly List<string> Logged = new();

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument B(bool value) => new(Engine_UiModel.GuiNativeArgumentKind.Boolean, value ? "1" : "0");

	private static Engine_UiModel.GuiNativeCall Call(string name, params Engine_UiModel.GuiNativeArgument[] arguments) => new(name, arguments);

	private static void Fresh()
	{
		Logged.Clear();
		Fable3Elements.Log = Logged.Add;
		Fable3Elements.Clear();
		Fable3UiStatements.Clear();
	}

	[TestCase]
	public void TheUiModeHoldsThePadWhileItIsOnTop()
	{
		Fresh();
		AssertThat(Fable3PointerStatements.GuestHoldsThePad).IsFalse();

		AssertThat(Fable3UiStatements.Take(Call("@GameMode.Ui", B(true)))).IsTrue();
		AssertThat(Fable3PointerStatements.UiMode).IsTrue();
		AssertThat(Fable3PointerStatements.GuestHoldsThePad).IsTrue();

		Fable3UiStatements.Take(Call("@GameMode.Ui", B(false)));
		AssertThat(Fable3PointerStatements.GuestHoldsThePad).IsFalse();
	}

	[TestCase]
	public void TheGuisPauseHoldsThePadWhileItIsOnTop()
	{
		Fresh();
		AssertThat(Fable3UiStatements.Take(Call("@GameMode.Gui", B(true)))).IsTrue();
		AssertThat(Fable3PointerStatements.GuiMode).IsTrue();
		AssertThat(Fable3PointerStatements.UiMode).IsFalse();
		AssertThat(Fable3PointerStatements.GuestHoldsThePad).IsTrue();

		Fable3UiStatements.Take(Call("@GameMode.Gui", B(false)));
		AssertThat(Fable3PointerStatements.GuestHoldsThePad).IsFalse();

		Fable3UiStatements.Take(Call("@GameMode.Gui", B(true)));
		Fable3UiStatements.Clear();
		AssertThat(Fable3PointerStatements.GuiMode).IsFalse();
	}

	[TestCase]
	public void TheRestatedUiModeIsSaidOnce()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@GameMode.Ui", B(true)));
		ulong generation = Fable3UiStatements.Generation;
		int said = Logged.Count;

		// The hook restates it on every write of the presented stack.
		Fable3UiStatements.Take(Call("@GameMode.Ui", B(true)));
		AssertThat(Fable3UiStatements.Generation).IsEqual(generation);
		AssertThat(Logged.Count).IsEqual(said);
	}

	[TestCase]
	public void ABoxHoldsUntilItsOwnHide()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@Box.Hold", S("40B06C08"), B(true)));
		Fable3UiStatements.Take(Call("@Box.Hold", S("40B07000"), B(true)));
		AssertThat(Fable3PointerStatements.BoxHolds).IsTrue();

		Fable3UiStatements.Take(Call("@Box.Hold", S("40B06C08"), B(false)));
		AssertThat(Fable3PointerStatements.BoxHolds).IsTrue();

		Fable3UiStatements.Take(Call("@Box.Hold", S("40B07000"), B(false)));
		AssertThat(Fable3PointerStatements.BoxHolds).IsFalse();
		AssertThat(Fable3PointerStatements.GuestHoldsThePad).IsFalse();
	}

	[TestCase]
	public void BoxesFreedWithTheirScreenManagerHoldNothing()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@Box.Hold", S("40B06C08"), B(true)));
		Fable3UiStatements.Take(Call("@GameMode.Ui", B(true)));

		Fable3UiStatements.ScreensDestroyed();
		AssertThat(Fable3PointerStatements.BoxHolds).IsFalse();
		// The mode is the stack's own statement: the next run states it.
		AssertThat(Fable3PointerStatements.UiMode).IsTrue();

		Fable3UiStatements.Clear();
		AssertThat(Fable3PointerStatements.GuestHoldsThePad).IsFalse();
	}
}
